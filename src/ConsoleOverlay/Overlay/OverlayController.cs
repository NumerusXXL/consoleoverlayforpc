using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Decides how and where to open the overlay, and gives focus back to the game when it closes.
/// </summary>
public sealed class OverlayController
{
    private readonly OverlayWindow _window;
    private readonly ControllerService _controller;
    private AppSettings _settings;
    private IntPtr _previousForeground;
    private bool _closing;
    private readonly Stopwatch _openedAt = new();

    public OverlayController(OverlayWindow window, ControllerService controller, AppSettings settings)
    {
        _window = window;
        _controller = controller;
        _settings = settings;

        _window.CloseRequested += () => Close(restorePrevious: true);
        _window.Deactivated += (_, _) =>
        {
            // Clicking or Alt+Tabbing elsewhere closes the overlay without stealing focus back.
            // Windows can send a brief deactivation while the overlay is still taking focus, so
            // ignore the first moments after opening and check who really has focus before closing.
            if (!IsOpen || _closing || _openedAt.ElapsedMilliseconds < 500)
                return;
            _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (IsOpen && !_closing && Native.GetForegroundWindow() != _window.Handle)
                {
                    Log.Write("Overlay lost focus to another window; closing.");
                    Close(restorePrevious: false);
                }
            });
        };
        _controller.Navigate += command =>
        {
            if (IsOpen)
                _window.Navigate(command);
        };
    }

    public bool IsOpen { get; private set; }

    public void UpdateSettings(AppSettings settings) => _settings = settings;

    public void Toggle()
    {
        if (IsOpen)
            Close(restorePrevious: true);
        else
            Open();
    }

    public void Open()
    {
        if (IsOpen) return;

        var foreground = Native.GetForegroundWindow();
        if (foreground == _window.Handle)
            foreground = IntPtr.Zero;

        bool exclusive = IsExclusiveFullscreen();
        if (exclusive)
        {
            var process = GetProcessName(foreground);
            if (!_settings.ExclusiveFullscreenFallback || IsExcluded(process))
            {
                Log.Write($"Not opening: {process ?? "unknown app"} is in exclusive fullscreen and the fallback is off for it.");
                return;
            }
            Log.Write($"Opening in exclusive fullscreen mode over {process ?? "unknown app"}.");
        }

        _previousForeground = foreground;
        IsOpen = true;
        _openedAt.Restart();

        var monitor = GetMonitorRect(foreground);
        Log.Write($"Opening over {GetProcessName(foreground) ?? "desktop"} on monitor {monitor.Left},{monitor.Top} {monitor.Width}x{monitor.Height}, exclusive={exclusive}.");
        _window.ShowOn(monitor, exclusive);
        ForceForeground(_window.Handle);
        _controller.NavigationEnabled = true;
        Log.Write($"Opened. Visible={_window.IsVisible}, has focus={Native.GetForegroundWindow() == _window.Handle}.");
    }

    public void Close(bool restorePrevious)
    {
        if (!IsOpen) return;

        _closing = true;
        try
        {
            IsOpen = false;
            Log.Write($"Closing (restore focus to game: {restorePrevious}).");
            _controller.NavigationEnabled = false;
            _window.Hide();

            var previous = _previousForeground;
            _previousForeground = IntPtr.Zero;
            if (restorePrevious && previous != IntPtr.Zero && Native.IsWindow(previous))
            {
                // An exclusive fullscreen game minimizes itself when it loses focus; bring it back.
                if (Native.IsIconic(previous))
                    Native.ShowWindow(previous, Native.SW_RESTORE);
                Native.SetForegroundWindow(previous);
            }
        }
        finally
        {
            _closing = false;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static bool IsExclusiveFullscreen() =>
        Native.SHQueryUserNotificationState(out var state) == 0
        && state == Native.QueryUserNotificationState.RunningD3DFullScreen;

    private bool IsExcluded(string? process)
    {
        if (process is null) return false;
        return _settings.ExclusiveFullscreenExcludedGames.Any(name =>
            string.Equals(Path.GetFileNameWithoutExtension(name.Trim()), process, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Native.RECT GetMonitorRect(IntPtr hwnd)
    {
        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor != IntPtr.Zero && Native.GetMonitorInfo(monitor, ref info))
            return info.rcMonitor;

        var primary = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        return new Native.RECT { Left = primary.Left, Top = primary.Top, Right = primary.Right, Bottom = primary.Bottom };
    }

    /// <summary>
    /// Windows only lets an app take focus if it received the last input. A keyboard shortcut counts,
    /// but a controller press doesn't, so if the plain call fails we briefly attach to the
    /// foreground window's input queue, which Windows allows.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd)
            return;

        var foreground = Native.GetForegroundWindow();
        uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        uint ourThread = Native.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == ourThread)
        {
            Native.SetForegroundWindow(hwnd);
            return;
        }

        Native.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            Native.AttachThreadInput(ourThread, foregroundThread, false);
        }

        if (Native.GetForegroundWindow() != hwnd)
            Log.Write("Could not take focus from the foreground app; the overlay is shown but the game may still get input.");
    }
}
