using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Steam app's own chat window, placed inside the overlay on a friend's page.
///
/// This only uses ordinary Windows window management, the same as dragging the window yourself: the chat is opened with
/// the official steam://friends/message link, then its window is moved and sized into the frame on the page and kept
/// above the overlay. Typing and clicks are ordinary keyboard and mouse input (like Windows' touch keyboard). Nothing is
/// injected into Steam and nothing reads Steam's memory, so it is as safe for the account as using the chat window
/// by hand. When you leave, the window goes back where it was (or closes, if the overlay opened it).
/// </summary>
internal sealed class SteamChatWindow
{
    private readonly bool _openedByUs;
    private readonly bool _wasTopmost;
    private Native.WINDOWPLACEMENT _placement;
    private Native.RECT _placed;
    private bool _released;

    private SteamChatWindow(IntPtr hwnd, bool openedByUs)
    {
        Hwnd = hwnd;
        _openedByUs = openedByUs;
        _placement = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(hwnd, ref _placement);
        _wasTopmost = (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
    }

    /// <summary>The chat window placed in the overlay right now (the overlay stays open while it has focus).</summary>
    public static SteamChatWindow? Active { get; private set; }

    public IntPtr Hwnd { get; }

    /// <summary>The window still exists (Steam can close or re-create it, for example after an update).</summary>
    public bool Alive => Native.IsWindow(Hwnd) && Native.IsWindowVisible(Hwnd);

    /// <summary>
    /// Opens your chat with <paramref name="steamId"/> in the Steam app and finds its window: the window titled with
    /// their name, or a new Steam window that appeared. Null if Steam didn't open one within a few seconds.
    /// </summary>
    public static async Task<SteamChatWindow?> OpenAsync(string steamId, string name, CancellationToken cancel)
    {
        var before = new HashSet<IntPtr>(SteamWindows().ConvertAll(w => w.Hwnd));
        try
        {
            Process.Start(new ProcessStartInfo($"steam://friends/message/{steamId}") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"Steam chat: could not open the steam:// link: {ex.Message}");
            return null;
        }

        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 12000)
        {
            await Task.Delay(200, cancel);
            var windows = SteamWindows();
            foreach (var w in windows)
                if (Matches(w.Title, name))
                    return Found(w, openedByUs: !before.Contains(w.Hwnd));
            // A docked chat (Friends List with tabs) may not carry their name; a new window that showed up is it.
            if (watch.ElapsedMilliseconds > 2500)
                foreach (var w in windows)
                    if (!before.Contains(w.Hwnd) && !IsMainWindow(w.Title))
                        return Found(w, openedByUs: true);
        }
        Log.Write("Steam chat: no chat window appeared. Steam windows: "
            + string.Join(", ", SteamWindows().ConvertAll(w => $"\"{w.Title}\" ({w.Class})")));
        return null;
    }

    private static SteamChatWindow Found((IntPtr Hwnd, string Title, string Class) w, bool openedByUs)
    {
        Log.Write($"Steam chat: using the window \"{w.Title}\" ({w.Class}), opened by the overlay: {openedByUs}.");
        return new SteamChatWindow(w.Hwnd, openedByUs);
    }

    /// <summary>Puts the window into <paramref name="rect"/> (screen pixels), above the overlay, and gives it focus.</summary>
    public void Attach(Native.RECT rect)
    {
        Active = this;
        if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_RESTORE);
        Place(rect);
        Focus();
    }

    /// <summary>Moves and sizes it (the frame on the page changed: the keyboard opened, the window resized).</summary>
    public void Place(Native.RECT rect)
    {
        if (!Alive) return;
        _placed = rect;
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height,
            Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Keeps it above the overlay after the overlay took focus (for its on-screen keyboard).</summary>
    public void Raise()
    {
        if (!Alive) return;
        Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Gives it keyboard focus, so typing (and the overlay's keyboard) goes into the chat.</summary>
    public bool Focus()
    {
        if (!Alive) return false;
        if (Native.GetForegroundWindow() == Hwnd) return true;
        uint ourThread = Native.GetCurrentThreadId();
        uint theirThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        bool attach = theirThread != 0 && theirThread != ourThread;
        if (attach) Native.AttachThreadInput(ourThread, theirThread, true);
        try
        {
            Native.BringWindowToTop(Hwnd);
            Native.SetForegroundWindow(Hwnd);
        }
        finally
        {
            if (attach) Native.AttachThreadInput(ourThread, theirThread, false);
        }
        return Native.GetForegroundWindow() == Hwnd;
    }

    public bool HasFocus => Native.GetForegroundWindow() == Hwnd;

    /// <summary>The middle of the conversation, in screen pixels (where the pointer starts, and scrolling happens).</summary>
    public Native.POINT Middle => new() { X = _placed.Left + _placed.Width / 2, Y = _placed.Top + _placed.Height / 2 };

    public Native.RECT Bounds => _placed;

    /// <summary>Gives the window back: where it was before, not above other windows; closed if the overlay opened it.</summary>
    public void Release()
    {
        if (Active == this) Active = null;
        if (_released || !Native.IsWindow(Hwnd)) return;
        _released = true;
        if (!_wasTopmost)
            Native.SetWindowPos(Hwnd, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        if (_openedByUs)
        {
            // Closing the chat window doesn't end the conversation; Steam keeps it, as when you close it yourself.
            Native.PostMessage(Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        var placement = _placement;
        if (placement.showCmd == Native.SW_MINIMIZE || placement.showCmd == 2 /* SW_SHOWMINIMIZED */)
            placement.showCmd = Native.SW_SHOWNORMAL; // put it back in its spot first, then minimize it again
        Native.SetWindowPlacement(Hwnd, ref placement);
        if (_placement.showCmd is 2 or Native.SW_MINIMIZE) Native.ShowWindow(Hwnd, Native.SW_MINIMIZE);
    }

    /// <summary>Lets go of the chat window, if one is placed (the overlay is closing).</summary>
    public static void ReleaseActive() => Active?.Release();

    // ---- Finding Steam's windows ------------------------------------------------------------

    private static bool Matches(string title, string name) =>
        name.Length > 0 && (title.Equals(name, StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith(name + " -", StringComparison.OrdinalIgnoreCase)
            || title.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The main Steam window and the friends list aren't a chat.</summary>
    private static bool IsMainWindow(string title) =>
        title.Equals("Steam", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Friends List", StringComparison.OrdinalIgnoreCase)
        || title.Equals("Список друзей", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Big Picture", StringComparison.OrdinalIgnoreCase);

    /// <summary>Visible windows of Steam's interface process (steamwebhelper.exe), with a title.</summary>
    private static List<(IntPtr Hwnd, string Title, string Class)> SteamWindows()
    {
        var found = new List<(IntPtr, string, string)>();
        var names = new Dictionary<uint, bool>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            int length = Native.GetWindowTextLength(hwnd);
            if (length == 0) return true;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!names.TryGetValue(pid, out bool steam))
                names[pid] = steam = IsSteamProcess(pid);
            if (!steam) return true;
            var title = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, title, title.Capacity);
            var cls = new StringBuilder(128);
            Native.GetClassName(hwnd, cls, cls.Capacity);
            found.Add((hwnd, title.ToString(), cls.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsSteamProcess(uint pid)
    {
        var process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(1024);
            int size = name.Capacity;
            if (!Native.QueryFullProcessImageName(process, 0, name, ref size)) return false;
            var file = System.IO.Path.GetFileName(name.ToString());
            return file.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase)
                || file.Equals("steam.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}

/// <summary>Ordinary keyboard and mouse input to the window that has focus, as Windows' touch keyboard sends it.</summary>
internal static class SyntheticInput
{
    private static readonly int Size = Marshal.SizeOf<Native.INPUT>();

    public static void Text(string text)
    {
        var inputs = new List<Native.INPUT>();
        foreach (char ch in text)
        {
            inputs.Add(Key(0, ch, Native.KEYEVENTF_UNICODE));
            inputs.Add(Key(0, ch, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP));
        }
        SendList(inputs);
    }

    /// <summary>Presses and lets go of a key (a virtual-key code, like 0x0D for Enter).</summary>
    public static void Press(ushort vk) => Send([Key(vk, 0, 0), Key(vk, 0, Native.KEYEVENTF_KEYUP)]);

    public static void Click(bool right = false) => Send(
    [
        Mouse(right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN, 0),
        Mouse(right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP, 0),
    ]);

    /// <summary>Mouse wheel: positive scrolls up.</summary>
    public static void Wheel(int notches) => Send([Mouse(Native.MOUSEEVENTF_WHEEL, notches * 120)]);

    private static void SendList(List<Native.INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        if (Native.SendInput((uint)inputs.Count, inputs.ToArray(), Size) != inputs.Count)
            Log.Write($"Steam chat: Windows blocked the input (error {Marshal.GetLastWin32Error()}).");
    }

    private static void Send(params Native.INPUT[] inputs) => SendList(new List<Native.INPUT>(inputs));

    private static Native.INPUT Key(ushort vk, ushort ch, uint flags) => new()
    {
        type = Native.INPUT_KEYBOARD,
        u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, wScan = ch, dwFlags = flags } },
    };

    private static Native.INPUT Mouse(uint flags, int data) => new()
    {
        type = Native.INPUT_MOUSE,
        u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = flags, mouseData = data } },
    };
}
