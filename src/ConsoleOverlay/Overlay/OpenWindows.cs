using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

public sealed record OpenWindow(IntPtr Handle, string Title, ImageSource? Icon);

/// <summary>Lists the windows a person would see in Alt+Tab, and switches to one.</summary>
internal static class OpenWindows
{
    private const int MaxWindows = 12;
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];

    /// <summary>Visible app windows, front to back (the app you were just in comes first).</summary>
    public static List<OpenWindow> List()
    {
        var result = new List<OpenWindow>();
        uint ownPid = (uint)Environment.ProcessId;

        Native.EnumWindows((hwnd, _) =>
        {
            if (result.Count >= MaxWindows) return false;
            if (IsSwitchable(hwnd, ownPid))
                result.Add(new OpenWindow(hwnd, GetTitle(hwnd), GetIcon(hwnd)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static void SwitchTo(IntPtr hwnd)
    {
        if (!Native.IsWindow(hwnd)) return;
        if (Native.IsIconic(hwnd))
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(hwnd);
    }

    private static bool IsSwitchable(IntPtr hwnd, uint ownPid)
    {
        if (!Native.IsWindowVisible(hwnd)) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        if (Native.GetWindowTextLength(hwnd) == 0) return false;

        var exStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;

        // Store apps keep hidden ("cloaked") windows around; skip them.
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == ownPid) return false;

        var className = new StringBuilder(64);
        Native.GetClassName(hwnd, className, className.Capacity);
        return Array.IndexOf(ShellClasses, className.ToString()) < 0;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(Native.GetWindowTextLength(hwnd) + 1);
        Native.GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static ImageSource? GetIcon(IntPtr hwnd)
    {
        try
        {
            // Ask the window for its icon, but never wait on a hung app.
            Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(Native.ICON_BIG), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 50, out var icon);
            if (icon == IntPtr.Zero)
                Native.SendMessageTimeout(hwnd, Native.WM_GETICON, new IntPtr(Native.ICON_SMALL2), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 50, out icon);
            if (icon == IntPtr.Zero)
                icon = Native.GetClassLongPtr(hwnd, Native.GCLP_HICON);
            if (icon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"No icon for window {hwnd}: {ex.Message}");
            return null;
        }
    }
}
