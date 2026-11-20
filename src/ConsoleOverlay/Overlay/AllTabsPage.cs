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
/// its own color), the focused one bigger and showing the window live, and its name in large type underneath with
/// what A and X do. A switches to the window (or back to the game you came from), X closes it.
///
/// The live picture is a DWM thumbnail: Windows itself draws it straight from the window's surface onto ours, so it
/// costs almost nothing. Windows draws it on top of our content at fixed pixel positions, so it is only turned on
/// once the page has stopped moving and the tile has finished growing.
/// </summary>
internal sealed class AllTabsPage : IOverlayPage
{
    private const double TileWidth = 150, TileHeight = 150;
    private const double FocusedWidth = 212, FocusedHeight = 212;
    private const double TileGap = 18;   // the space between tiles
    private const double PreviewInset = 8; // the live picture sits inside a thin frame of the tile's color
    private static readonly TimeSpan ShowPreviewsAfter = TimeSpan.FromMilliseconds(400); // open / slide animations
    private static readonly TimeSpan GrowTime = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private sealed class Tile
    {
        public required OpenWindow Window { get; init; }
        public required Button Button { get; init; }
        public required Border Preview { get; init; }
        public required UIElement Placeholder { get; init; }
        public IntPtr Thumbnail { get; set; }
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
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly DispatcherTimer _followTimer;
    private DateTime _followUntil;
    private IReadOnlyList<OpenWindow> _windows = [];
    private IntPtr _currentWindow;
    private bool _active;
    private bool _previewsOn;
    private byte _previewOpacity;
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

        _showTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ShowPreviewsAfter };
        _showTimer.Tick += (_, _) =>
        {
            _showTimer.Stop();
            ShowPreviews();
        };
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _fadeTimer.Tick += (_, _) => FadeStep();
        // While tiles grow or shrink, keep moving the previews with them, frame by frame.
        _followTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _followTimer.Tick += (_, _) =>
        {
            if (_previewsOn) PlacePreviews();
            if (DateTime.UtcNow > _followUntil) _followTimer.Stop();
        };
        _strip.ScrollChanged += (_, _) =>
        {
            if (_previewsOn) PlacePreviews();
        };
    }

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
        _previewsOn = false;
        ShowPreviewSoon();
    }

    public void Deactivate()
    {
        _active = false;
        _showTimer.Stop();
        HidePreviews();
    }

    /// <summary>Hides the previews right away (they would otherwise stay put while the overlay fades out).</summary>
    public void HidePreviewsNow() => HidePreviews();

    /// <summary>Lets go of the window list and icons while the overlay is hidden.</summary>
    public void Clear()
    {
        HidePreviews();
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
        HidePreviews();
        _windows = OpenWindows.List();
        Build();
        HintChanged?.Invoke();

        if (_tiles.Count > 0)
        {
            var target = _tiles[Math.Clamp(focusIndex, 0, _tiles.Count - 1)].Button;
            target.Focus();
            Keyboard.Focus(target);
        }
        // Wait for the new tiles to be laid out before placing previews on them.
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_active) ShowPreviews();
        });
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
        // The app's icon on its own color, like a console's tiles. The focused one shows the window live on top.
        var bigIcon = window.Icon is null
            ? (FrameworkElement)Ui.Icon(_window, "", 44) // a generic window
            : new Image { Source = window.Icon, Width = 64, Height = 64 };
        bigIcon.HorizontalAlignment = HorizontalAlignment.Center;
        bigIcon.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(bigIcon, BitmapScalingMode.HighQuality);
        var (light, dark) = TileColors(window.Icon);
        var face = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush(light, dark, new Point(0.2, 0), new Point(0.8, 1)),
            Child = bigIcon,
        };

        var preview = new Border { Margin = new Thickness(PreviewInset) };
        var content = new Grid();
        content.Children.Add(face);
        content.Children.Add(preview);

        var button = new Button
        {
            Style = (Style)_window.FindResource("WindowTile"),
            Content = content,
            Width = TileWidth,
            Height = TileHeight,
            Margin = new Thickness(0, 0, TileGap, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var tile = new Tile { Window = window, Button = button, Preview = preview, Placeholder = bigIcon };

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
            ShowPreviewSoon();
        };
        button.LostKeyboardFocus += (_, _) =>
        {
            Grow(button, false);
            HidePreview(tile);
        };

        _tiles.Add(tile);
        return button;
    }

    /// <summary>The focused tile grows; its preview (and its neighbours', which shift) follows it every frame.</summary>
    private void Grow(Button button, bool focused)
    {
        var duration = new Duration(GrowTime);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        button.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(focused ? FocusedWidth : TileWidth, duration) { EasingFunction = ease });
        button.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(focused ? FocusedHeight : TileHeight, duration) { EasingFunction = ease });
        _followUntil = DateTime.UtcNow + GrowTime + TimeSpan.FromMilliseconds(40);
        if (_previewsOn && !_followTimer.IsEnabled) _followTimer.Start();
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

    // ---- Live preview -----------------------------------------------------------------------

    private IntPtr OwnHandle => new WindowInteropHelper(_window).Handle;

    /// <summary>Turns the focused tile's live picture on once it has finished growing.</summary>
    private void ShowPreviewSoon()
    {
        if (!_active) return;
        _showTimer.Stop();
        _showTimer.Interval = _previewsOn ? GrowTime + TimeSpan.FromMilliseconds(40) : ShowPreviewsAfter;
        _showTimer.Start();
    }

    private void ShowPreviews()
    {
        if (!_active || !_window.IsVisible) return;
        var own = OwnHandle;
        if (own == IntPtr.Zero) return;
        var tile = Focused(Keyboard.FocusedElement);
        _previewsOn = true;
        if (tile is null || tile.Thumbnail != IntPtr.Zero) return;

        // Minimized windows have nothing to show (an exclusive fullscreen game is minimized while we're open).
        if (!Native.IsWindow(tile.Window.Handle) || Native.IsIconic(tile.Window.Handle)) return;
        if (Native.DwmRegisterThumbnail(own, tile.Window.Handle, out var thumbnail) != 0) return;
        tile.Thumbnail = thumbnail;
        _previewOpacity = 0;
        PlacePreviews();
        _fadeTimer.Start();
    }

    private void FadeStep()
    {
        _previewOpacity = (byte)Math.Min(255, _previewOpacity + 40);
        PlacePreviews();
        if (_previewOpacity == 255)
            _fadeTimer.Stop();
    }

    /// <summary>
    /// Puts the live picture over its tile, filling it: the middle of the window, cut to the tile's square shape.
    /// Hidden if the tile isn't fully in view.
    /// </summary>
    private void PlacePreviews()
    {
        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget is null) return;
        var toDevice = source.CompositionTarget.TransformToDevice;

        Rect viewport;
        try
        {
            viewport = _strip.TransformToAncestor(_window).TransformBounds(new Rect(_strip.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return; // not in the visual tree right now
        }

        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail == IntPtr.Zero) continue;

            Rect area;
            try
            {
                area = tile.Preview.TransformToAncestor(_window).TransformBounds(new Rect(tile.Preview.RenderSize));
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            bool inView = area.Width > 0
                && area.Left >= viewport.Left - 1 && area.Right <= viewport.Right + 1
                && area.Top >= viewport.Top - 1 && area.Bottom <= viewport.Bottom + 1;
            var properties = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                fVisible = inView,
                fSourceClientAreaOnly = true,
                opacity = _previewOpacity,
            };

            if (inView)
            {
                var topLeft = toDevice.Transform(area.TopLeft);
                var bottomRight = toDevice.Transform(area.BottomRight);
                properties.rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(topLeft.X),
                    Top = (int)Math.Round(topLeft.Y),
                    Right = (int)Math.Round(bottomRight.X),
                    Bottom = (int)Math.Round(bottomRight.Y),
                };
                if (Crop(area, tile.Window.Handle) is { } crop)
                {
                    properties.dwFlags |= DWM_TNP_RECTSOURCE;
                    properties.rcSource = crop;
                }
            }

            Native.DwmUpdateThumbnailProperties(tile.Thumbnail, ref properties);
            tile.Placeholder.Visibility = inView && _previewOpacity > 120 ? Visibility.Hidden : Visibility.Visible;
        }
    }

    private const uint DWM_TNP_RECTSOURCE = 0x02;

    /// <summary>The middle part of the window with the shape of <paramref name="area"/>, in window pixels.</summary>
    private static Native.RECT? Crop(Rect area, IntPtr window)
    {
        if (!Native.GetClientRect(window, out var client) || client.Width <= 0 || client.Height <= 0 || area.Height <= 0)
            return null;
        double shape = area.Width / area.Height;
        double width = client.Width, height = client.Height;
        if (width / height > shape) width = height * shape;
        else height = width / shape;
        int left = (int)((client.Width - width) / 2), top = (int)((client.Height - height) / 2);
        return new Native.RECT { Left = left, Top = top, Right = left + (int)width, Bottom = top + (int)height };
    }

    private void HidePreview(Tile tile)
    {
        if (tile.Thumbnail != IntPtr.Zero)
        {
            Native.DwmUnregisterThumbnail(tile.Thumbnail);
            tile.Thumbnail = IntPtr.Zero;
        }
        tile.Placeholder.Visibility = Visibility.Visible;
    }

    private void HidePreviews()
    {
        _fadeTimer.Stop();
        _followTimer.Stop();
        _previewsOn = false;
        foreach (var tile in _tiles)
            HidePreview(tile);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    /// <summary>
    /// A tile's two colors: the icon's strongest color, and a darker shade of it, for the gradient behind the icon.
    /// Slate grey when the icon has none (or there is no icon).
    /// </summary>
    private static (Color Light, Color Dark) TileColors(ImageSource? icon)
    {
        var fallback = (Color.FromRgb(0x4A, 0x54, 0x68), Color.FromRgb(0x23, 0x29, 0x3A));
        if (icon is not BitmapSource bitmap) return fallback;
        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = converted.PixelWidth, height = converted.PixelHeight;
            var pixels = new byte[width * height * 4];
            converted.CopyPixels(pixels, width * 4, 0);

            // Average the colorful, mostly opaque pixels, weighting each by how colorful it is.
            double r = 0, g = 0, b = 0, weight = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i + 3] < 160) continue;
                double pb = pixels[i], pg = pixels[i + 1], pr = pixels[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double saturation = max <= 0 ? 0 : (max - min) / max;
                if (saturation < 0.25 || max < 40) continue;
                double w = saturation * saturation;
                r += pr * w; g += pg * w; b += pb * w; weight += w;
            }
            if (weight <= 0) return fallback;
            var light = Color.FromRgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight));
            light = Scale(light, 230.0 / Math.Max(1.0, Math.Max(light.R, Math.Max(light.G, (double)light.B)))); // bright, not neon
            return (light, Scale(light, 0.38));
        }
        catch
        {
            return fallback;
        }
    }

    private static Color Scale(Color color, double factor) => Color.FromRgb(
        (byte)Math.Clamp(color.R * factor, 0, 255), (byte)Math.Clamp(color.G * factor, 0, 255), (byte)Math.Clamp(color.B * factor, 0, 255));

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
