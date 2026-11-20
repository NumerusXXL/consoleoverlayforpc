using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ConsoleOverlay.Perf;

/// <summary>
/// Counts a game's frames the way PresentMon and other FPS counters do: Windows reports every frame a game presents
/// as an event (DirectX 10/11/12 through DXGI, DirectX 9 through D3D9). Listening needs permission to start an event
/// session: running as administrator, or being in the Performance Log Users group. Nothing is injected into the game.
/// </summary>
internal sealed class FrameCounter : IDisposable
{
    private const string SessionName = "ConsoleOverlay-Frames";
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;

    private readonly object _gate = new();
    private readonly Dictionary<int, List<double>> _frames = []; // process id → present times (ms), last 2 seconds
    private TraceEventSession? _session;
    private readonly Dictionary<int, long> _lastSeen = []; // when each program last drew a frame (TickCount64)

    /// <summary>Why it isn't counting, for the page to show (null while it works).</summary>
    public string? Problem { get; private set; }

    public bool Running => _session is not null;

    public void Start()
    {
        if (_session is not null) return;
        try
        {
            var session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(Dxgi, TraceEventLevel.Informational);
            session.EnableProvider(D3D9, TraceEventLevel.Informational);
            session.Source.AllEvents += OnEvent;
            _session = session;
            Problem = null;
            Task.Run(() =>
            {
                try { session.Source.Process(); }
                catch (Exception ex) { Log.Write($"Frame counter stopped: {ex.Message}"); }
            });
        }
        catch (Exception ex)
        {
            // Without admin rights (or Performance Log Users) Windows refuses the event session.
            bool denied = ex is UnauthorizedAccessException || TraceEventSession.IsElevated() != true;
            Problem = denied ? "permission" : "error";
            Log.Write(denied
                ? "Frame counter: no permission to listen for frames (needs admin or Performance Log Users)."
                : $"Frame counter could not start: {ex.Message}");
        }
    }

    /// <summary>
    /// Adds this Windows account to the Performance Log Users group (Windows asks for admin once), so frames can be
    /// counted from then on without running as administrator. Takes effect after signing out and in again.
    /// Returns false if the person said no or it failed.
    /// </summary>
    public static bool AllowForThisAccount()
    {
        try
        {
            // By SID, so it works whatever the group is called in this Windows language.
            string account = $"{Environment.UserDomainName}\\{Environment.UserName}".Replace("'", "''");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Add-LocalGroupMember -SID 'S-1-5-32-559' -Member '{account}' -ErrorAction SilentlyContinue\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            });
            if (process is null) return false;
            process.WaitForExit(15000);
            Log.Write($"Added {account} to Performance Log Users (exit code {(process.HasExited ? process.ExitCode : -1)}).");
            return !process.HasExited || process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not add this account to Performance Log Users: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        var session = _session;
        _session = null;
        try { session?.Dispose(); } catch { }
        lock (_gate)
        {
            _frames.Clear();
            _lastSeen.Clear();
        }
    }

    private void OnEvent(TraceEvent e)
    {
        bool present = (e.ProviderGuid == Dxgi && (int)e.ID == DxgiPresentStart)
                       || (e.ProviderGuid == D3D9 && (int)e.ID == D3D9PresentStart);
        if (!present) return;
        double now = e.TimeStampRelativeMSec;
        lock (_gate)
        {
            if (!_frames.TryGetValue(e.ProcessID, out var list))
                _frames[e.ProcessID] = list = new List<double>(512);
            list.Add(now);
            _lastSeen[e.ProcessID] = Environment.TickCount64;
            int old = 0;
            while (old < list.Count && list[old] < now - 2000) old++;
            if (old > 0) list.RemoveRange(0, old);
        }
    }

    /// <summary>
    /// Frames per second over the last second for a process, the frame time, and the "1% low" (the frame rate of
    /// the slowest 1% of frames in the last two seconds). Null when the process presented nothing.
    /// When <paramref name="processId"/> is 0, the busiest process is used.
    /// </summary>
    public FrameStats? Read(int processId)
    {
        lock (_gate)
        {
            // A program that stopped drawing (minimized, closed) keeps its old frames; forget it.
            // (Windows hands events over in batches about a second apart, hence the slack.)
            long tooOld = Environment.TickCount64 - 2500;
            foreach (var stale in _lastSeen.Where(p => p.Value < tooOld).Select(p => p.Key).ToList())
            {
                _frames.Remove(stale);
                _lastSeen.Remove(stale);
            }

            if (processId == 0 || !_frames.ContainsKey(processId))
            {
                // The busiest one, but never Console Overlay itself (WPF draws with Direct3D too).
                processId = _frames.Where(f => f.Value.Count > 1 && f.Key != Environment.ProcessId).OrderByDescending(f => f.Value.Count).Select(f => f.Key).FirstOrDefault();
                if (processId == 0) return null;
            }
            var list = _frames[processId];
            if (list.Count < 2) return null;
            double last = list[^1];
            int inLastSecond = list.Count(t => t > last - 1000);
            var gaps = new List<double>(list.Count);
            for (int i = 1; i < list.Count; i++) gaps.Add(list[i] - list[i - 1]);
            gaps.Sort();
            double frameTime = gaps.Average();
            double worst = gaps[Math.Max(0, (int)Math.Floor(gaps.Count * 0.99) - 1)];
            return new FrameStats(processId, inLastSecond, frameTime, worst > 0 ? 1000 / worst : 0);
        }
    }

    public void Dispose() => Stop();
}

internal sealed record FrameStats(int ProcessId, int Fps, double FrameTimeMs, double OnePercentLow);
