using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ConsoleOverlay.Perf;

/// <summary>
/// NVIDIA's management library (nvml.dll), which the NVIDIA driver installs. It reads the GPU's temperature, load,
/// clock, video memory and power straight from the driver. Missing on other GPUs: then <see cref="Available"/> is false
/// and the overlay says "Not supported" for what only it can tell.
/// </summary>
internal static class Nvml
{
    private const string Dll = "nvml.dll";
    private static bool _tried;
    private static IntPtr _device;

    public static bool Available { get; private set; }
    public static string? GpuName { get; private set; }

    public static bool Init()
    {
        if (_tried) return Available;
        _tried = true;
        try
        {
            // The driver puts nvml.dll in System32; older drivers only in NVSMI.
            if (!NativeLibrary.TryLoad(Dll, typeof(Nvml).Assembly, DllImportSearchPath.System32, out _))
            {
                var old = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", Dll);
                if (!System.IO.File.Exists(old) || !NativeLibrary.TryLoad(old, out _)) return false;
                NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, _, _) =>
                    name == Dll && NativeLibrary.TryLoad(old, out var handle) ? handle : IntPtr.Zero);
            }
            if (nvmlInit_v2() != 0) return false;
            if (nvmlDeviceGetHandleByIndex_v2(0, out _device) != 0) return false;
            var name = new StringBuilder(96);
            if (nvmlDeviceGetName(_device, name, (uint)name.Capacity) == 0) GpuName = name.ToString();
            Available = true;
            Log.Write($"GPU stats: NVIDIA driver found ({GpuName}).");
        }
        catch (Exception ex)
        {
            Log.Write($"GPU stats: no NVIDIA driver library ({ex.GetType().Name}). NVIDIA-only stats show as not supported.");
        }
        return Available;
    }

    public static int? Temperature() => Available && nvmlDeviceGetTemperature(_device, 0, out uint t) == 0 ? (int)t : null;

    public static int? Load() => Available && nvmlDeviceGetUtilizationRates(_device, out var u) == 0 ? (int)u.Gpu : null;

    public static int? ClockMHz() => Available && nvmlDeviceGetClockInfo(_device, 0, out uint mhz) == 0 ? (int)mhz : null;

    public static int? PowerWatts() => Available && nvmlDeviceGetPowerUsage(_device, out uint mw) == 0 ? (int)(mw / 1000) : null;

    public static (double UsedGb, double TotalGb)? Memory()
    {
        if (!Available || nvmlDeviceGetMemoryInfo(_device, out var m) != 0) return null;
        return (m.Used / 1073741824.0, m.Total / 1073741824.0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

    [DllImport(Dll)] private static extern int nvmlInit_v2();
    [DllImport(Dll)] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(Dll, CharSet = CharSet.Ansi)] private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);
    [DllImport(Dll)] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temperature);
    [DllImport(Dll)] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Dll)] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);
    [DllImport(Dll)] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Dll)] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out MemoryInfo memory);
}
