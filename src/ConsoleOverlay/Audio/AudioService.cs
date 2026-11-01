using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace ConsoleOverlay.Audio;

public sealed record AudioDevice(string Id, string Name, bool IsDefault, bool Muted);

/// <summary>One app in the mixer. An app can own several audio sessions (one per process or stream); they move together.</summary>
public sealed class AppAudio
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required uint ProcessId { get; init; }
    public int Volume { get; internal set; }
    public bool Muted { get; internal set; }
    internal List<SimpleAudioVolume> Controls { get; } = [];
}

/// <summary>
/// Reads and changes Windows audio through Core Audio: devices, the default output's master volume, and
/// every app's volume on that output. Holds no COM objects between <see cref="Refresh"/> and
/// <see cref="Release"/>, so it costs nothing while the Sound page is closed.
/// </summary>
public sealed class AudioService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _output;
    private MMDevice? _input;
    private readonly List<AudioSessionControl> _sessions = [];
    private readonly Dictionary<uint, string> _processNames = [];

    public IReadOnlyList<AudioDevice> Outputs { get; private set; } = [];
    public IReadOnlyList<AudioDevice> Inputs { get; private set; } = [];
    public IReadOnlyList<AppAudio> Apps { get; private set; } = [];
    public bool HasOutput => _output is not null;
    public int MasterVolume { get; private set; }
    public bool MasterMuted { get; private set; }

    /// <summary>Re-reads devices, the master volume and the app mixer. Apps whose process is <paramref name="firstProcessId"/> come first.</summary>
    public void Refresh(uint firstProcessId)
    {
        ReleaseDevices();
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            _output = GetDefault(DataFlow.Render);
            _input = GetDefault(DataFlow.Capture);
            Outputs = ListDevices(DataFlow.Render, _output?.ID);
            Inputs = ListDevices(DataFlow.Capture, _input?.ID);

            if (_output is not null)
            {
                var endpoint = _output.AudioEndpointVolume;
                MasterVolume = ToPercent(endpoint.MasterVolumeLevelScalar);
                MasterMuted = endpoint.Mute;
            }
            Apps = ReadApps(firstProcessId);
        }
        catch (Exception ex)
        {
            Log.Write($"Reading audio failed: {ex.Message}");
        }
    }

    // ---- Changing things --------------------------------------------------------------------

    public void SetMasterVolume(int percent)
    {
        if (_output is null) return;
        Try("set master volume", () =>
        {
            _output.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
            MasterVolume = percent;
            if (percent > 0 && MasterMuted)
                SetMasterMuted(false); // turning it up should be heard
        });
    }

    public void SetMasterMuted(bool muted)
    {
        if (_output is null) return;
        Try("mute output", () =>
        {
            _output.AudioEndpointVolume.Mute = muted;
            MasterMuted = muted;
        });
    }

    public void SetAppVolume(AppAudio app, int percent)
    {
        Try($"set volume of {app.Name}", () =>
        {
            foreach (var control in app.Controls)
            {
                control.Volume = percent / 100f;
                if (percent > 0 && app.Muted)
                    control.Mute = false;
            }
            app.Volume = percent;
            if (percent > 0) app.Muted = false;
        });
    }

    public void SetAppMuted(AppAudio app, bool muted)
    {
        Try($"mute {app.Name}", () =>
        {
            foreach (var control in app.Controls)
                control.Mute = muted;
            app.Muted = muted;
        });
    }

    /// <summary>Mutes or unmutes a microphone (or any device) by id.</summary>
    public void SetDeviceMuted(string id, bool muted)
    {
        if (_enumerator is null) return;
        Try("mute device", () =>
        {
            using var device = _enumerator.GetDevice(id);
            device.AudioEndpointVolume.Mute = muted;
        });
    }

    /// <summary>Makes a device the default for games, apps and calls, like picking it in the Windows sound settings.</summary>
    public void SetDefaultDevice(string id)
    {
        Try("switch device", () =>
        {
            var policy = (IPolicyConfig)new PolicyConfigClient();
            try
            {
                foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        });
    }

    /// <summary>Lets go of every COM object. The next <see cref="Refresh"/> opens them again.</summary>
    public void Release()
    {
        ReleaseDevices();
        _enumerator?.Dispose();
        _enumerator = null;
        Apps = [];
        _processNames.Clear();
    }

    public void Dispose() => Release();

    // ---- Reading ----------------------------------------------------------------------------

    private MMDevice? GetDefault(DataFlow flow) =>
        _enumerator!.HasDefaultAudioEndpoint(flow, Role.Multimedia) ? _enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia) : null;

    private List<AudioDevice> ListDevices(DataFlow flow, string? defaultId)
    {
        var list = new List<AudioDevice>();
        foreach (var device in _enumerator!.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                try
                {
                    bool isDefault = device.ID == defaultId;
                    bool muted = flow == DataFlow.Capture && isDefault && device.AudioEndpointVolume.Mute;
                    list.Add(new AudioDevice(device.ID, device.FriendlyName, isDefault, muted));
                }
                catch (Exception ex)
                {
                    Log.Write($"Skipping an audio device: {ex.Message}");
                }
            }
        }
        // Default first, then alphabetical, so the list doesn't jump around.
        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private List<AppAudio> ReadApps(uint firstProcessId)
    {
        if (_output is null) return [];

        var manager = _output.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        var apps = new Dictionary<string, AppAudio>();
        uint ownProcess = (uint)Environment.ProcessId;

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            _sessions.Add(session);
            try
            {
                if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired)
                    continue;

                uint pid = session.GetProcessID;
                if (pid == ownProcess) continue;

                bool system = session.IsSystemSoundsSession;
                string name = system ? "System sounds" : ProcessName(pid);
                // Group by app name so a browser with many processes is one row.
                string key = system ? "system" : name.ToLowerInvariant();

                if (!apps.TryGetValue(key, out var app))
                {
                    var volume = session.SimpleAudioVolume;
                    app = new AppAudio { Key = key, Name = name, ProcessId = pid, Volume = ToPercent(volume.Volume), Muted = volume.Mute };
                    apps[key] = app;
                }
                else if (pid == firstProcessId)
                {
                    // Keep the game's own process as the one used for sorting.
                    var replaced = new AppAudio { Key = key, Name = name, ProcessId = pid, Volume = app.Volume, Muted = app.Muted };
                    replaced.Controls.AddRange(app.Controls);
                    apps[key] = app = replaced;
                }
                app.Controls.Add(session.SimpleAudioVolume);
            }
            catch (Exception ex)
            {
                Log.Write($"Skipping an audio session: {ex.Message}");
            }
        }

        return apps.Values
            .OrderByDescending(a => a.ProcessId == firstProcessId && firstProcessId != 0)
            .ThenBy(a => a.Key == "system") // system sounds last
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The app's own name ("Google Chrome"), falling back to its exe name ("chrome").</summary>
    private string ProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var cached))
            return cached;

        string name = $"App {pid}";
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process != IntPtr.Zero)
        {
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    string path = buffer.ToString();
                    name = Path.GetFileNameWithoutExtension(path);
                    try
                    {
                        var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                        if (!string.IsNullOrEmpty(description) && description.Length <= 40)
                            name = description;
                    }
                    catch
                    {
                        // No version info; the exe name will do.
                    }
                }
            }
            finally
            {
                CloseHandle(process);
            }
        }

        _processNames[pid] = name;
        return name;
    }

    private void ReleaseDevices()
    {
        foreach (var session in _sessions)
        {
            try { session.Dispose(); } catch { /* already gone */ }
        }
        _sessions.Clear();
        _output?.Dispose();
        _input?.Dispose();
        _output = null;
        _input = null;
    }

    private static int ToPercent(float scalar) => (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100);

    private static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not {what}: {ex.Message}");
        }
    }

    // ---- Interop ----------------------------------------------------------------------------

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    /// <summary>
    /// The interface Windows' own sound settings use to change the default device. It isn't documented, but has
    /// stayed the same since Windows 7. Only the slot order matters for the methods we don't call.
    /// </summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
        [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int defaultPeriod, IntPtr defaultPeriodOut, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr deviceId, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        [PreserveSig] int SetEndpointVisibility(IntPtr deviceId, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient
    {
    }
}
