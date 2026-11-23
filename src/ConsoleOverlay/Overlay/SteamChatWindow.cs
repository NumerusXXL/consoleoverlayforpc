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
/// This only uses ordinary Windows window management: the chat is opened with the official steam://friends/message
/// link, then its window is made a child window of a frame on the page (<see cref="ChatHost"/>), the way apps embed
/// another app's window. As a child it has no frame, shadow or border of its own, moves with the page, and is clipped
/// to the frame's rounded shape; Steam's title bar and tab row sit above the frame's top edge, cut off. If it moves or
/// resizes anyway it is put straight back. Typing and clicks are ordinary keyboard and mouse input (like Windows' touch
/// keyboard). Nothing is injected into Steam and nothing reads Steam's memory, so it is as safe for the account as
/// using the chat window by hand. When you leave, the window gets its own frame and place back (or closes, if the
/// overlay opened it).
/// </summary>
internal sealed class SteamChatWindow
{
    private readonly bool _openedByUs;
    private readonly bool _wasTopmost;
    private Native.WINDOWPLACEMENT _placement;
    private IntPtr _container;
    private bool _released;
    private readonly long _style;
    private readonly List<IntPtr> _hooks = [];
    private Native.RECT _expected; // where the window really is after the last Place (Steam may keep a minimum size)
    private Native.WinEventProc? _onEvent; // kept alive while the hook is set
    private bool _snapping;
    private long _snapWindowStart;
    private int _snaps;

    private SteamChatWindow(IntPtr hwnd, bool openedByUs)
    {
        Hwnd = hwnd;
        _openedByUs = openedByUs;
        _placement = new Native.WINDOWPLACEMENT { length = Marshal.SizeOf<Native.WINDOWPLACEMENT>() };
        Native.GetWindowPlacement(hwnd, ref _placement);
        _wasTopmost = (Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() & Native.WS_EX_TOPMOST) != 0;
        _style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
    }

    /// <summary>
    /// Steam's own strips at the top of the chat window, cut away so the conversation starts right under the overlay's
    /// header: the title bar (window buttons) and the row of chat tabs with the friend's picture, 80 px together at
    /// 100 % scaling (measured on Misha's screen), more on a scaled screen.
    /// </summary>
    private int TitleStrip => Scaled(80);

    /// <summary>Steam draws a resize grip in its bottom-right corner; the window reaches this far past the frame so it's cut off.</summary>
    private int GripMargin => Scaled(14);

    private int Scaled(int pixels) => (int)Math.Round(pixels * Math.Max(96, Native.GetDpiForWindow(Hwnd)) / 96.0);

    /// <summary>The chat window placed in the overlay right now (the overlay stays open while it has focus).</summary>
    public static SteamChatWindow? Active { get; private set; }

    /// <summary>
    /// A chat is being opened: Steam brings its chat window to the front by itself, which takes focus from the overlay
    /// before the window is placed. The overlay must not close then.
    /// </summary>
    public static bool Opening => Environment.TickCount64 < _openingUntil;

    private static long _openingUntil;

    /// <summary>Arms <see cref="Opening"/> before anything is sent to Steam; it lasts until the window is placed (at most 20 s).</summary>
    public static void BeginOpening() => _openingUntil = Environment.TickCount64 + 20000;

    public static void EndOpening() => _openingUntil = 0;

    public IntPtr Hwnd { get; }

    /// <summary>The window still exists (Steam can close or re-create it, for example after an update).</summary>
    public bool Alive => Native.IsWindow(Hwnd) && Native.IsWindowVisible(Hwnd);

    /// <summary>
    /// Opens your chat with <paramref name="steamId"/> in the Steam app and finds its window: the window titled with
    /// their name, or a new Steam window that appeared. Null if Steam didn't open one within a few seconds.
    /// </summary>
    public static async Task<SteamChatWindow?> OpenAsync(string steamId, string name, CancellationToken cancel)
    {
        BeginOpening();
        try
        {
            var found = await FindAsync(steamId, name, cancel);
            if (found is null) EndOpening();
            return found; // stays "opening" until it is placed (Attach)
        }
        catch (OperationCanceledException)
        {
            EndOpening();
            throw;
        }
        catch (Exception ex)
        {
            EndOpening();
            Log.Write($"Steam chat: opening the chat failed: {ex}");
            return null;
        }
    }

    /// <summary>Big Picture is open: Steam then opens chats inside it instead of in a chat window.</summary>
    public static bool BigPictureOpen() =>
        SteamWindows().Exists(w => w.Title.Contains("Big Picture", StringComparison.OrdinalIgnoreCase));

    private static async Task<SteamChatWindow?> FindAsync(string steamId, string name, CancellationToken cancel)
    {
        if (BigPictureOpen())
        {
            // Close Big Picture first (the official steam:// link for it), so the chat opens as a window.
            Log.Write("Steam chat: Big Picture is open; closing it so the chat opens as a window.");
            try { Process.Start(new ProcessStartInfo("steam://close/bigpicture") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Write($"Steam chat: could not close Big Picture: {ex.Message}"); }
            for (int i = 0; i < 20 && BigPictureOpen(); i++) await Task.Delay(250, cancel);
            await Task.Delay(400, cancel);
        }
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

    /// <summary>Makes the window a child of <paramref name="container"/> (the frame on the page) and gives it focus.</summary>
    public void Attach(IntPtr container)
    {
        Active = this;
        EndOpening();
        _container = container;
        if (Native.IsIconic(Hwnd)) Native.ShowWindow(Hwnd, Native.SW_RESTORE);
        // A child window: no frame, title bar or window buttons, nothing to grab, resize or maximize.
        long child = (_style & ~(Native.WS_POPUP | Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU
            | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX)) | Native.WS_CHILD;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_STYLE, new IntPtr(child));
        Native.SetParent(Hwnd, container);
        Fit();
        Lock();
        Focus();
    }

    /// <summary>
    /// Fills the frame: the window starts Steam's title strip above the frame's top edge (cut off there), and reaches a
    /// little past its right and bottom edges so Steam's resize grip is cut off too.
    /// </summary>
    public void Fit()
    {
        if (!Alive || _container == IntPtr.Zero || !Native.GetClientRect(_container, out var area)) return;
        int strip = TitleStrip, grip = GripMargin;
        _snapping = true;
        try
        {
            Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, -strip, area.Width + grip, area.Height + strip + grip,
                Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
            Native.GetWindowRect(Hwnd, out _expected);
        }
        finally
        {
            _snapping = false;
        }
    }

    /// <summary>Puts it straight back when it moves, resizes or minimizes, and cancels a drag as soon as it starts.</summary>
    private void Lock()
    {
        if (_hooks.Count > 0) return;
        Native.GetWindowThreadProcessId(Hwnd, out uint pid);
        _onEvent = OnWindowEvent;
        // Only these three events, only from Steam's process.
        foreach (uint e in new[] { Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_OBJECT_LOCATIONCHANGE })
        {
            var hook = Native.SetWinEventHook(e, e, IntPtr.Zero, _onEvent, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
            else Log.Write($"Steam chat: could not watch the chat window (event {e:X}).");
        }
    }

    private void Unlock()
    {
        foreach (var hook in _hooks) Native.UnhookWinEvent(hook);
        _hooks.Clear();
        _onEvent = null;
    }

    private void OnWindowEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != Hwnd || idObject != 0 || _released || _snapping) return;
        try
        {
            switch (eventType)
            {
                case Native.EVENT_SYSTEM_MOVESIZESTART:
                    Native.PostMessage(Hwnd, Native.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero); // ends the drag
                    break;
                case Native.EVENT_SYSTEM_MINIMIZESTART:
                    Native.ShowWindow(Hwnd, Native.SW_SHOWNORMAL);
                    SnapBack();
                    break;
                case Native.EVENT_OBJECT_LOCATIONCHANGE:
                    SnapBack();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Steam chat: keeping the window in place failed: {ex.Message}"); // never close the overlay over it
        }
    }

    private void SnapBack()
    {
        if (!Native.GetWindowRect(Hwnd, out var now)) return;
        if (now.Left == _expected.Left && now.Top == _expected.Top && now.Width == _expected.Width && now.Height == _expected.Height) return;
        // If something keeps moving it (Steam itself fighting back), stop for a moment instead of flickering.
        long tick = Environment.TickCount64;
        if (tick - _snapWindowStart > 1000) { _snapWindowStart = tick; _snaps = 0; }
        if (++_snaps > 25)
        {
            if (_snaps == 26) Log.Write("Steam chat: the chat window keeps moving; not putting it back for a second.");
            return;
        }
        // After the event, not inside it, so a burst of moves ends with the window where it belongs.
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input, () => { if (!_released) Fit(); });
    }

    /// <summary>
    /// Gives it keyboard focus, so typing (and the overlay's keyboard) goes into the chat. The overlay stays the window
    /// in front; Steam's window is inside it.
    /// </summary>
    public bool Focus()
    {
        if (!Alive) return false;
        Native.SetFocus(Hwnd); // works across the two apps because a child window shares input with its parent
        return Native.GetFocus() == Hwnd || Native.GetFocus() != IntPtr.Zero;
    }

    public bool HasFocus => Native.GetFocus() == Hwnd;

    /// <summary>
    /// The window, or one of Steam's own pop-ups for it (a right-click menu, the emoji picker): they belong to the same
    /// Steam process, and the overlay stays open while they have focus.
    /// </summary>
    public bool Owns(IntPtr window)
    {
        if (window == Hwnd) return true;
        if (window == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(Hwnd, out uint ours);
        Native.GetWindowThreadProcessId(window, out uint theirs);
        return ours != 0 && ours == theirs;
    }

    /// <summary>The visible part on screen, in pixels: the frame.</summary>
    public Native.RECT Bounds => Native.GetWindowRect(_container, out var r) ? r : default;

    /// <summary>The middle of the conversation, in screen pixels (where the pointer starts, and scrolling happens).</summary>
    public Native.POINT Middle
    {
        get
        {
            var b = Bounds;
            return new Native.POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
        }
    }

    /// <summary>Gives the window back: where it was before, not above other windows; closed if the overlay opened it.</summary>
    public void Release()
    {
        if (Active == this) Active = null;
        EndOpening();
        Unlock();
        if (_released || !Native.IsWindow(Hwnd)) return;
        _released = true;
        // A window of its own again, with its own frame and title bar.
        Native.SetParent(Hwnd, IntPtr.Zero);
        Native.SetWindowLongPtr(Hwnd, Native.GWL_STYLE, new IntPtr(_style));
        _container = IntPtr.Zero;
        Native.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_FRAMECHANGED | 0x0004 /* NOZORDER */);
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
