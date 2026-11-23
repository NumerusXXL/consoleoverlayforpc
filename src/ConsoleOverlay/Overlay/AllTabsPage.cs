using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The All Tabs page, laid out like a console home screen: every open window as a square tile in one row (its icon on
/// its own color), the focused one bigger, and its name in large type underneath with what A and X do. A switches to
/// the window (or back to the game you came from), X closes it.
///
/// Like a console showing the focused game's art behind everything, the page background becomes a blurred picture of
/// the focused window. Pictures are taken once per window each time the overlay opens, off the UI thread.
/// </summary>
internal sealed class AllTabsPage : IOverlayPage
{
    private const double TileWidth = 150, TileHeight = 150;
    private const double FocusedWidth = 212, FocusedHeight = 212;
    private const double TileGap = 18;   // the space between tiles
    private static readonly TimeSpan BackdropAfter = TimeSpan.FromMilliseconds(220); // focus has settled
    private static readonly TimeSpan GrowTime = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private sealed class Tile
    {
        public required OpenWindow Window { get; init; }
        public required Button Button { get; init; }
    }

    private readonly Window _window;
    private readonly Grid _root = new();
    private readonly ScrollViewer _strip = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        CanContentScroll = false,
    };
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 48, FontWeight = FontWeights.ExtraBold };
    private readonly TextBlock _meta = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
    private readonly List<Tile> _tiles = [];
    private readonly DispatcherTimer _backdropTimer;
    private readonly Dictionary<IntPtr, BitmapSource?> _pictures = [];
    private int _backdropToken;
    private IReadOnlyList<OpenWindow> _windows = [];
    private IntPtr _currentWindow;
    private bool _active;
    private IntPtr _confirmClose;
    private DateTime _confirmUntil;

    /// <summary>Close the overlay and give focus back to the window it opened over.</summary>
    public event Action? BackToGame;

    /// <summary>Close the overlay and switch to this window.</summary>
    public event Action<IntPtr>? SwitchTo;

    /// <summary>The hint never changes; raised when the window count (the subtitle) does.</summary>
    public event Action? HintChanged;

    public AllTabsPage(Window window)
    {
        _window = window;

        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FocusedHeight + 52) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Padding so the focus ring and halo (drawn just outside a tile) aren't cut off by the scrolling strip.
        _row.Margin = new Thickness(12, 30, 12, 12);
        _strip.Content = _row;
        _strip.Margin = new Thickness(-12, 0, -12, 0);
        KeyboardNavigation.SetDirectionalNavigation(_row, KeyboardNavigationMode.Contained);
        _root.Children.Add(_strip);

        _title.FontFamily = (FontFamily)window.FindResource("DisplayFont");
        _title.Foreground = (Brush)window.FindResource("TextBrush");
        _meta.FontSize = 16;
        _meta.Margin = new Thickness(0, 10, 0, 0);
        _meta.TextTrimming = TextTrimming.CharacterEllipsis;
        _meta.Foreground = (Brush)window.FindResource("SoftTextBrush");
        var info = new StackPanel { Margin = new Thickness(0, 16, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        info.Children.Add(_title);
        info.Children.Add(_meta);
        info.Children.Add(_actions);
        Grid.SetRow(info, 1);
        _root.Children.Add(info);

        _backdropTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = BackdropAfter };
        _backdropTimer.Tick += (_, _) =>
        {
            _backdropTimer.Stop();
            ShowBackdrop();
        };
    }

    /// <summary>The sharp snapshot of the game the overlay opened over (null in exclusive fullscreen).</summary>
    public Func<BitmapSource?>? GameSnapshot { get; set; }

    /// <summary>The page background should show this picture (null: the usual one, the game behind the overlay).</summary>
    public event Action<BitmapSource?>? BackdropChanged;

    public Panel Content => _root;

    public string? Subtitle => _windows.Count == 1 ? "1 open" : $"{_windows.Count} open";

    public string Hint => "B back to game · LB / RB other pages";

    /// <summary>Called each time the overlay opens, with the windows open right now.</summary>
    public void SetWindows(IReadOnlyList<OpenWindow> windows, IntPtr currentWindow)
    {
        _windows = windows;
        _currentWindow = currentWindow;
        _confirmClose = IntPtr.Zero;
        Build();
    }

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        ScheduleBackdrop();
    }

    public void Deactivate()
    {
        _active = false;
        _backdropTimer.Stop();
        _backdropToken++;
        BackdropChanged?.Invoke(null);
    }

    /// <summary>Lets go of the window list and icons while the overlay is hidden.</summary>
    public void Clear()
    {
        _pictures.Clear();
        _tiles.Clear();
        _row.Children.Clear();
        _windows = [];
    }

    /// <summary>Start on the first window that isn't the one you came from, like Alt+Tab.</summary>
    public UIElement? FirstFocus() =>
        _tiles.FirstOrDefault(t => t.Window.Handle != _currentWindow)?.Button ?? _tiles.FirstOrDefault()?.Button;

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        switch (command)
        {
            case NavCommand.Secondary:
                if (Focused(focused) is { } tile) CloseWindow(tile.Window.Handle);
                return true;
            case NavCommand.Up or NavCommand.Down:
                return true; // one row: nothing above or below
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) =>
        key is Key.X or Key.Delete && Handle(NavCommand.Secondary, focused);

    private Tile? Focused(IInputElement? focused) => _tiles.FirstOrDefault(t => t.Button == focused);

    // ---- Closing windows --------------------------------------------------------------------

    private void CloseWindow(IntPtr handle)
    {
        var tile = _tiles.FirstOrDefault(t => t.Window.Handle == handle);
        if (tile is null) return;

        // Closing the game you came from could lose progress, so that one needs a second press.
        if (handle == _currentWindow && (_confirmClose != handle || DateTime.UtcNow > _confirmUntil))
        {
            _confirmClose = handle;
            _confirmUntil = DateTime.UtcNow + ConfirmWindow;
            ShowInfo(tile);
            var reset = new DispatcherTimer { Interval = ConfirmWindow };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                if (_confirmClose != handle) return;
                _confirmClose = IntPtr.Zero;
                if (Keyboard.FocusedElement == tile.Button) ShowInfo(tile);
            };
            reset.Start();
            return;
        }

        _confirmClose = IntPtr.Zero;
        Log.Write($"Closing window \"{tile.Window.Title}\" from All Tabs.");
        int index = _tiles.IndexOf(tile);
        _meta.Text = "Closing…";
        tile.Button.Opacity = 0.5;
        OpenWindows.Close(handle);

        // Most apps are gone (or hidden to the tray) within a moment. One that is still open is probably asking
        // something, like whether to save, so bring it to the front where that question can be answered.
        var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (!_active) return;
            if (OpenWindows.Exists(handle))
            {
                Log.Write("The window is still open after closing; switching to it so its question can be answered.");
                SwitchTo?.Invoke(handle);
                return;
            }
            Reload(index);
        };
        check.Start();
    }

    private void Reload(int focusIndex)
    {
        _windows = OpenWindows.List();
        Build();
        HintChanged?.Invoke();

        if (_tiles.Count > 0)
        {
            var target = _tiles[Math.Clamp(focusIndex, 0, _tiles.Count - 1)].Button;
            target.Focus();
            Keyboard.Focus(target);
        }
    }

    // ---- Building ---------------------------------------------------------------------------

    private void Build()
    {
        _row.Children.Clear();
        _tiles.Clear();
        _strip.ScrollToHorizontalOffset(0);

        if (_windows.Count == 0)
        {
            _strip.Visibility = Visibility.Hidden;
            _title.Text = "No other windows open";
            _meta.Text = "Apps and games you open show up here.";
            _actions.Children.Clear();
            return;
        }

        _strip.Visibility = Visibility.Visible;
        foreach (var window in _windows)
            _row.Children.Add(BuildTile(window));
        ShowInfo(Focused(FirstFocus()) ?? _tiles[0]);
    }

    private Button BuildTile(OpenWindow window)
    {
        // The app's icon on its own color, like a console's tiles.
        var bigIcon = window.Icon is null
            ? (FrameworkElement)Ui.Icon(_window, "", 44) // a generic window
            : new Image { Source = window.Icon, Width = 64, Height = 64 };
        bigIcon.HorizontalAlignment = HorizontalAlignment.Center;
        bigIcon.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(bigIcon, BitmapScalingMode.HighQuality);
        var (light, dark) = Ui.TileColors(window.Icon);
        var face = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush(light, dark, new Point(0.2, 0), new Point(0.8, 1)),
            Child = bigIcon,
        };

        var button = new Button
        {
            Style = (Style)_window.FindResource("WindowTile"),
            Content = face,
            Width = TileWidth,
            Height = TileHeight,
            Margin = new Thickness(0, 0, TileGap, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var tile = new Tile { Window = window, Button = button };

        button.Click += (_, _) => Primary(tile);
        // With a mouse, pointing at a tile picks it, so the X pill below closes the one you point at.
        button.MouseEnter += (_, _) =>
        {
            if (!button.IsKeyboardFocused) Keyboard.Focus(button);
        };
        button.GotKeyboardFocus += (_, _) =>
        {
            int index = _tiles.IndexOf(tile);
            // Keep the focused tile near the left with one tile before it. Every tile before it is the small size,
            // so its position is known before the grow animation runs.
            _strip.ScrollToHorizontalOffset(Math.Max(0, (index - 1) * (TileWidth + TileGap)));
            Grow(button, true);
            ShowInfo(tile);
            ScheduleBackdrop();
        };
        button.LostKeyboardFocus += (_, _) => Grow(button, false);

        _tiles.Add(tile);
        return button;
    }

    /// <summary>The focused tile grows, the one losing focus shrinks back.</summary>
    private void Grow(Button button, bool focused)
    {
        var duration = new Duration(GrowTime);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        button.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(focused ? FocusedWidth : TileWidth, duration) { EasingFunction = ease });
        button.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(focused ? FocusedHeight : TileHeight, duration) { EasingFunction = ease });
    }

    private void ShowInfo(Tile tile)
    {
        var window = tile.Window;
        bool current = window.Handle == _currentWindow;
        bool confirming = current && _confirmClose == window.Handle && DateTime.UtcNow <= _confirmUntil;

        // The app's name big (a game's name, "Google Chrome"), what's in the window underneath.
        string name = window.AppName is { Length: > 0 } app ? app : window.Title;
        string detail = window.AppName is { Length: > 0 } && window.Title != name ? window.Title : "";
        _title.Text = name;
        _meta.Text = confirming ? "Press X again to close this game"
            : current ? string.Join(" · ", new[] { "Current", RunningFor(window.Handle), detail }.Where(t => t.Length > 0))
            : detail;

        _actions.Children.Clear();
        _actions.Children.Add(Ui.ActionPill(_window, "A", current ? "Back to game" : "Switch to", primary: true, () => Primary(tile)));
        _actions.Children.Add(Ui.ActionPill(_window, "X", confirming ? "Close (press again)" : "Close", primary: false,
            () => CloseWindow(window.Handle)));
    }

    private void Primary(Tile tile)
    {
        if (tile.Window.Handle == _currentWindow)
            BackToGame?.Invoke();
        else
            SwitchTo?.Invoke(tile.Window.Handle);
    }

    // ---- Background ---------------------------------------------------------------------------

    private void ScheduleBackdrop()
    {
        if (!_active) return;
        _backdropTimer.Stop();
        _backdropTimer.Start();
    }

    /// <summary>
    /// The focused window's picture, only slightly blurred, behind the page, so it's easy to recognize. For the game
    /// you came from it uses the snapshot taken as the overlay opened. Windows that can't be pictured (minimized
    /// ones) get the usual background.
    /// </summary>
    private async void ShowBackdrop()
    {
        if (!_active || Focused(Keyboard.FocusedElement) is not { } tile) return;
        var handle = tile.Window.Handle;
        int token = ++_backdropToken;
        if (!_pictures.TryGetValue(handle, out var picture))
        {
            var raw = handle == _currentWindow
                ? GameSnapshot?.Invoke()
                : await System.Threading.Tasks.Task.Run(() => WindowPicture.Capture(handle, 1280));
            if (token != _backdropToken || !_active) return;
            picture = raw is null ? null : ScreenCapture.SoftBlur(raw);
            _pictures[handle] = picture;
        }
        if (token == _backdropToken && _active)
            BackdropChanged?.Invoke(picture);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>"playing for 1 h 24 min": how long the window's program has been running.</summary>
    private static string RunningFor(IntPtr window)
    {
        try
        {
            Native.GetWindowThreadProcessId(window, out uint pid);
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var time = DateTime.Now - process.StartTime;
            if (time.TotalMinutes < 1) return "just started";
            return time.TotalHours >= 1
                ? $"playing for {(int)time.TotalHours} h {time.Minutes} min"
                : $"playing for {(int)time.TotalMinutes} min";
        }
        catch
        {
            return ""; // a program running as administrator won't tell
        }
    }
}
