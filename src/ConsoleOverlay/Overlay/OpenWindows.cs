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

/// <summary>An open window. <paramref name="AppName"/> is the program's friendly name, for example "Google Chrome".</summary>
public sealed record OpenWindow(IntPtr Handle, string Title, string? AppName, ImageSource? Icon);

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
            {
                var path = GetExePath(hwnd);
                var title = GetTitle(hwnd);
                result.Add(new OpenWindow(hwnd, title, GetAppName(path, title), GetIcon(hwnd, path)));
            }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Asks a window to close, the same as clicking its X. The app may still ask to save first.</summary>
    public static void Close(IntPtr hwnd)
    {
        if (Native.IsWindow(hwnd))
            Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool Exists(IntPtr hwnd) => Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd);

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

    // Friendly names barely change, so remember them for the life of the app (a few dozen entries at most).
    private static readonly Dictionary<string, string?> AppNames = new(StringComparer.OrdinalIgnoreCase);

    private static string? GetExePath(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            return Native.QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    /// <summary>The program's description ("Google Chrome"), or its file name. Null when it would only repeat the title.</summary>
    private static string? GetAppName(string? path, string title)
    {
        if (path is null) return null;
        if (!AppNames.TryGetValue(path, out var name))
        {
            string file = System.IO.Path.GetFileNameWithoutExtension(path);
            // Store apps all run inside ApplicationFrameHost; its name says nothing useful.
            if (string.Equals(file, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                name = null;
            else
            {
                try
                {
                    var description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                    name = string.IsNullOrEmpty(description) ? file : description;
                }
                catch (Exception)
                {
                    name = file;
                }
            }
            AppNames[path] = name;
        }

        if (name is null || title.Equals(name, StringComparison.OrdinalIgnoreCase))
            return null;
        return name;
    }

    private static ImageSource? GetIcon(IntPtr hwnd, string? path)
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
                return path is null ? null : GetFileIcon(path);

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

    private static ImageSource? GetFileIcon(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"No icon in {path}: {ex.Message}");
            return null;
        }
    }
}
