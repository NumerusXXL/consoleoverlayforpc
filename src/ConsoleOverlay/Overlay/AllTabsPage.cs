using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The All Tabs page: every open window as a tile with a live preview, like Alt+Tab.
/// A switches to the window, X closes it. The previews are DWM thumbnails: Windows itself draws them straight
/// from each window's surface onto ours, so they cost almost nothing and stay live. Windows draws them on top
/// of our content at fixed pixel positions, so they are only turned on once the page has stopped moving, and are
/// hidden for tiles scrolled partly out of view.
/// </summary>
internal sealed class AllTabsPage : IOverlayPage
{
    private const double PreviewWidth = 214;
    private const double PreviewHeight = 120;
    private static readonly TimeSpan ShowPreviewsAfter = TimeSpan.FromMilliseconds(400); // open / slide animations
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);

    private sealed class Tile
    {
        public required OpenWindow Window { get; init; }
        public required Button Button { get; init; }
        public required Border Preview { get; init; }
        public required UIElement Placeholder { get; init; }
        public required TextBlock Subtitle { get; init; }
        public required string SubtitleText { get; init; }
        public IntPtr Thumbnail { get; set; }
    }

    private readonly Window _window;
    private readonly ScrollViewer _scroller;
    private readonly WrapPanel _panel = new();
    private readonly List<Tile> _tiles = [];
    private readonly DispatcherTimer _showTimer;
    private readonly DispatcherTimer _fadeTimer;
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

    public event Action? HintChanged { add { } remove { } }

    public AllTabsPage(Window window, ScrollViewer scroller)
    {
        _window = window;
        _scroller = scroller;
        _showTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ShowPreviewsAfter };
        _showTimer.Tick += (_, _) =>
        {
            _showTimer.Stop();
            ShowPreviews();
        };
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _fadeTimer.Tick += (_, _) => FadeStep();
        _scroller.ScrollChanged += (_, _) =>
        {
            if (_previewsOn) PlacePreviews();
        };
    }

    public Panel Content => _panel;

    public string Hint => "A switch to window · X close window · B back to game · LB / RB other pages";

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
        _showTimer.Stop();
        _showTimer.Start();
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
        _panel.Children.Clear();
        _windows = [];
    }

    /// <summary>Start on the first window that isn't the one you came from, like Alt+Tab.</summary>
    public UIElement? FirstFocus()
    {
        var buttons = _panel.Children.OfType<Button>().ToList();
        return buttons.FirstOrDefault(b => b.Tag is IntPtr h && h != _currentWindow) ?? buttons.FirstOrDefault();
    }

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (command != NavCommand.Secondary) return false;
        if (focused is Button { Tag: IntPtr handle })
            CloseWindow(handle);
        return true;
    }

    public bool HandleKey(Key key, IInputElement? focused) =>
        key is Key.X or Key.Delete && Handle(NavCommand.Secondary, focused);

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
            tile.Subtitle.Text = "Press X again to close this game";
            var reset = new DispatcherTimer { Interval = ConfirmWindow };
            reset.Tick += (_, _) =>
            {
                reset.Stop();
                if (_confirmClose == handle) _confirmClose = IntPtr.Zero;
                tile.Subtitle.Text = tile.SubtitleText;
            };
            reset.Start();
            return;
        }

        _confirmClose = IntPtr.Zero;
        Log.Write($"Closing window \"{tile.Window.Title}\" from All Tabs.");
        int index = _tiles.IndexOf(tile);
        tile.Subtitle.Text = "Closing…";
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

        var buttons = _panel.Children.OfType<Button>().ToList();
        if (buttons.Count > 0)
        {
            var target = buttons[Math.Clamp(focusIndex, 0, buttons.Count - 1)];
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
        _panel.Children.Clear();
        _tiles.Clear();

        if (_windows.Count == 0)
        {
            _panel.Children.Add(Ui.Placeholder(_window, "No other windows open", "Apps and games you open show up here."));
            return;
        }

        foreach (var window in _windows)
            _panel.Children.Add(BuildTile(window));
    }

    private Button BuildTile(OpenWindow window)
    {
        bool isCurrent = window.Handle == _currentWindow;

        // Preview area: the app's icon until the live preview is drawn over it.
        var bigIcon = window.Icon is null
            ? (UIElement)Ui.Icon(_window, "\uE7C4", 34) // a generic window
            : new Image { Source = window.Icon, Width = 44, Height = 44 };
        if (bigIcon is FrameworkElement big)
        {
            big.HorizontalAlignment = HorizontalAlignment.Center;
            big.VerticalAlignment = VerticalAlignment.Center;
            big.Opacity = 0.9;
        }
        RenderOptions.SetBitmapScalingMode(bigIcon, BitmapScalingMode.HighQuality);
        var preview = new Border
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x22)),
            Child = bigIcon,
        };

        // Title line: small icon and the window title.
        var titleRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (window.Icon is not null)
        {
            var small = new Image { Source = window.Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.HighQuality);
            titleRow.Children.Add(small);
        }
        var title = new TextBlock
        {
            Text = window.Title,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(title, 1);
        titleRow.Children.Add(title);

        string subtitleText = isCurrent
            ? (window.AppName is null ? "Current" : $"Current  ·  {window.AppName}")
            : window.AppName ?? " ";
        var subtitle = new TextBlock
        {
            Text = subtitleText,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = (Brush)_window.FindResource("SubtleTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Width = PreviewWidth };
        content.Children.Add(preview);
        content.Children.Add(titleRow);
        content.Children.Add(subtitle);

        var button = new Button
        {
            Style = (Style)_window.FindResource("TileButton"),
            Content = content,
            Tag = window.Handle,
            ToolTip = window.AppName is null ? window.Title : $"{window.Title}\n{window.AppName}",
        };
        var handle = window.Handle;
        button.Click += (_, _) =>
        {
            if (handle == _currentWindow)
                BackToGame?.Invoke();
            else
                SwitchTo?.Invoke(handle);
        };

        _tiles.Add(new Tile
        {
            Window = window,
            Button = button,
            Preview = preview,
            Placeholder = bigIcon,
            Subtitle = subtitle,
            SubtitleText = subtitleText,
        });
        return button;
    }

    // ---- Live previews ----------------------------------------------------------------------

    private IntPtr OwnHandle => new WindowInteropHelper(_window).Handle;

    private void ShowPreviews()
    {
        if (!_active || !_window.IsVisible) return;
        var own = OwnHandle;
        if (own == IntPtr.Zero) return;

        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail != IntPtr.Zero) continue;
            // Minimized windows have nothing to show (an exclusive fullscreen game is minimized while we're open).
            if (!Native.IsWindow(tile.Window.Handle) || Native.IsIconic(tile.Window.Handle)) continue;
            if (Native.DwmRegisterThumbnail(own, tile.Window.Handle, out var thumbnail) == 0)
                tile.Thumbnail = thumbnail;
        }

        _previewsOn = true;
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

    /// <summary>Puts each preview over its tile, keeping the window's shape, or hides it if the tile isn't fully in view.</summary>
    private void PlacePreviews()
    {
        var source = PresentationSource.FromVisual(_window);
        if (source?.CompositionTarget is null) return;
        var toDevice = source.CompositionTarget.TransformToDevice;

        Rect viewport;
        try
        {
            viewport = _scroller.TransformToAncestor(_window).TransformBounds(new Rect(_scroller.RenderSize));
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

            bool inView = area.Width > 0 && area.Top >= viewport.Top - 1 && area.Bottom <= viewport.Bottom + 1;
            var properties = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_VISIBLE | Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                fVisible = inView,
                fSourceClientAreaOnly = true,
                opacity = _previewOpacity,
            };

            if (inView)
            {
                area = Fit(area, tile.Window.Handle);
                var topLeft = toDevice.Transform(area.TopLeft);
                var bottomRight = toDevice.Transform(area.BottomRight);
                properties.rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(topLeft.X),
                    Top = (int)Math.Round(topLeft.Y),
                    Right = (int)Math.Round(bottomRight.X),
                    Bottom = (int)Math.Round(bottomRight.Y),
                };
            }

            Native.DwmUpdateThumbnailProperties(tile.Thumbnail, ref properties);
            tile.Placeholder.Visibility = inView && _previewOpacity > 120 ? Visibility.Hidden : Visibility.Visible;
        }
    }

    /// <summary>The largest rectangle with the window's shape that fits in <paramref name="area"/>, centered.</summary>
    private static Rect Fit(Rect area, IntPtr window)
    {
        if (!Native.GetClientRect(window, out var client) || client.Width <= 0 || client.Height <= 0)
            return area;

        double scale = Math.Min(area.Width / client.Width, area.Height / client.Height);
        double width = client.Width * scale, height = client.Height * scale;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    private void HidePreviews()
    {
        _fadeTimer.Stop();
        _previewsOn = false;
        foreach (var tile in _tiles)
        {
            if (tile.Thumbnail != IntPtr.Zero)
            {
                Native.DwmUnregisterThumbnail(tile.Thumbnail);
                tile.Thumbnail = IntPtr.Zero;
            }
            tile.Placeholder.Visibility = Visibility.Visible;
        }
    }
}
