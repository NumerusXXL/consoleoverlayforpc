using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ConsoleOverlay.Perf;

/// <summary>One reading of the PC's load.</summary>
internal sealed record StatsSample(
    int CpuLoad, double? CpuGhz,
    int? GpuLoad, int? GpuTemperature, double? GpuGhz, int? GpuWatts,
    double MemoryUsedGb, double MemoryTotalGb,
    double? VideoMemoryUsedGb, double? VideoMemoryTotalGb);

/// <summary>
/// Reads CPU, memory and GPU load. Cheap enough to call once a second: CPU from the system's idle / busy times,
/// memory from Windows, GPU from the NVIDIA driver when there is one, otherwise from Windows' GPU counters (which give
/// the load and video memory in use, but no temperature or clock).
/// </summary>
internal sealed class SystemStats : IDisposable
{
    private long _lastIdle, _lastTotal;
    private PerformanceCounter? _cpuPerformance;
    private readonly double _baseGhz;
    private PerformanceCounterCategory? _gpuEngine;
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = [];
    private DateTime _gpuInstancesRead;
    private bool _countersFailed;

    public SystemStats()
    {
        Nvml.Init();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz) _baseGhz = mhz / 1000.0;
        }
        catch { }
    }

    public bool NvidiaGpu => Nvml.Available;

    public StatsSample Read()
    {
        int cpu = CpuLoad();
        double? cpuGhz = CpuGhz();
        var (usedRam, totalRam) = Memory();

        int? gpuLoad = Nvml.Load();
        var vram = Nvml.Memory();
        double? vramUsed = vram?.UsedGb, vramTotal = vram?.TotalGb;
        if (!Nvml.Available)
            gpuLoad = WindowsGpuLoad();

        return new StatsSample(cpu, cpuGhz,
            gpuLoad, Nvml.Temperature(), Nvml.ClockMHz() is int mhz ? mhz / 1000.0 : null, Nvml.PowerWatts(),
            usedRam, totalRam, vramUsed, vramTotal);
    }

    private int CpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        long idleTicks = idle.Ticks, total = kernel.Ticks + user.Ticks; // kernel time includes idle time
        long idleDelta = idleTicks - _lastIdle, totalDelta = total - _lastTotal;
        _lastIdle = idleTicks;
        _lastTotal = total;
        if (totalDelta <= 0) return 0;
        return (int)Math.Round(Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100));
    }

    /// <summary>The CPU's current speed: its base clock times how hard it is boosting (% Processor Performance).</summary>
    private double? CpuGhz()
    {
        if (_baseGhz <= 0 || _countersFailed) return null;
        try
        {
            _cpuPerformance ??= new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
            float percent = _cpuPerformance.NextValue();
            return percent > 0 ? _baseGhz * percent / 100 : _baseGhz;
        }
        catch (Exception ex)
        {
            _countersFailed = true;
            Log.Write($"CPU clock not available: {ex.Message}");
            return null;
        }
    }

    /// <summary>The busiest GPU engine type's load, the way Task Manager shows "GPU" (other GPUs than NVIDIA).</summary>
    private int? WindowsGpuLoad()
    {
        if (_countersFailed) return null;
        try
        {
            _gpuEngine ??= new PerformanceCounterCategory("GPU Engine");
            // New processes get new instances; look at the list every few seconds, not every reading.
            if (DateTime.UtcNow - _gpuInstancesRead > TimeSpan.FromSeconds(5))
            {
                _gpuInstancesRead = DateTime.UtcNow;
                var names = _gpuEngine.GetInstanceNames().Where(n => n.EndsWith("engtype_3D", StringComparison.Ordinal)).ToHashSet();
                foreach (var gone in _gpuCounters.Keys.Where(k => !names.Contains(k)).ToList())
                {
                    _gpuCounters[gone].Dispose();
                    _gpuCounters.Remove(gone);
                }
                foreach (var name in names.Where(n => !_gpuCounters.ContainsKey(n)))
                    _gpuCounters[name] = new PerformanceCounter("GPU Engine", "Utilization Percentage", name, readOnly: true);
            }
            double sum = 0;
            foreach (var counter in _gpuCounters.Values)
            {
                try { sum += counter.NextValue(); } catch { }
            }
            return (int)Math.Round(Math.Clamp(sum, 0, 100));
        }
        catch (Exception ex)
        {
            _countersFailed = true;
            Log.Write($"GPU load not available: {ex.Message}");
            return null;
        }
    }

    private static (double Used, double Total) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        double total = status.ullTotalPhys / 1073741824.0;
        return (total - status.ullAvailPhys / 1073741824.0, total);
    }

    public void Dispose()
    {
        _cpuPerformance?.Dispose();
        foreach (var counter in _gpuCounters.Values) counter.Dispose();
        _gpuCounters.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low, High;
        public long Ticks => ((long)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
}
