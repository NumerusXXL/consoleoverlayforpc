using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// While the overlay is open, the game behind it usually keeps running flat out and takes the whole CPU and GPU, so
/// the overlay's own frames arrive late and pages feel laggy. While open we ask Windows to serve the overlay first
/// (CPU and GPU), and keep garbage collection from pausing the UI. When the overlay hides, everything goes back to
/// normal, so the game gets the machine to itself again. The overlay is idle most of the time it is open, so the
/// game loses next to nothing.
/// </summary>
internal static class OverlayPriority
{
    private const int GpuNormal = 2;      // D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL
    private const int GpuAboveNormal = 3; // ..._ABOVE_NORMAL (HIGH and REALTIME need admin rights)
    private static bool _boosted;
    private static bool _gpuFailedLogged;

    public static void Boost(bool on)
    {
        if (on == _boosted) return;
        _boosted = on;

        try
        {
            using var process = Process.GetCurrentProcess();
            process.PriorityClass = on ? ProcessPriorityClass.AboveNormal : ProcessPriorityClass.Normal;

            int status = D3DKMTSetProcessSchedulingPriorityClass(process.Handle, on ? GpuAboveNormal : GpuNormal);
            if (status != 0 && !_gpuFailedLogged)
            {
                _gpuFailedLogged = true;
                Log.Write($"Could not raise the overlay's GPU priority (status 0x{status:X8}); only CPU priority is raised.");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not change the overlay's priority: {ex.Message}");
        }

        try
        {
            // Background collections only while open; blocking full collections are fine while hidden.
            GCSettings.LatencyMode = on ? GCLatencyMode.SustainedLowLatency : GCLatencyMode.Interactive;
        }
        catch (Exception)
        {
            // Not supported with this GC configuration; nothing to do.
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr process, int priority);
}
