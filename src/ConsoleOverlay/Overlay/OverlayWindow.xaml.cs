using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The overlay: a blurred snapshot of the game, one large page card, and the dock of pages at the bottom.
/// Pages: All Tabs (opens first), Sound, Discord, Settings.
/// </summary>
public partial class OverlayWindow : Window
{
    private sealed class Page
    {
        public required string Name { get; init; }
        public required string Icon { get; init; }
        public required string Hint { get; init; }
        public required Panel Content { get; init; }
        public Button DockButton { get; set; } = null!;
        public TextBlock DockLabel { get; set; } = null!;
        public SolidColorBrush DockFill { get; set; } = null!;
        public SolidColorBrush DockInk { get; set; } = null!;
    }

    private static readonly Color PillColor = Color.FromRgb(0xF2, 0xF4, 0xF8);
    private static readonly Color PillInkColor = Color.FromRgb(0x0B, 0x0E, 0x16);
    private static readonly Color IconColor = Color.FromRgb(0x8A, 0x93, 0xA6);
    private const double SnapshotBlurRadius = 26;
    private int _visibilityToken;

    private readonly List<Page> _pages = [];
    private readonly DispatcherTimer _clock;
    private Page _tabsPage = null!;
    private Page _settingsPage = null!;
    private Page _soundPage = null!;
    private SoundPage _sound = null!;
    private bool _soundActive;
    private int _selected;
    private IntPtr _currentWindow;

    public event Action? CloseRequested;
    public event Action<IntPtr>? SwitchToWindowRequested;
    public event Action? EditSettingsRequested;
    public event Action? ReloadSettingsRequested;
    public event Action? ExitRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        BuildPages();

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _clock.Tick += (_, _) => UpdateClock();

        SourceInitialized += (_, _) => HideFromAltTab();
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) return;
            // Free the snapshot and window icons while hidden so the idle footprint stays small.
            _clock.Stop();
            Snapshot.Source = null;
            _tabsPage.Content.Children.Clear();
            DeactivateSound();
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ---- Pages ------------------------------------------------------------------------------

    private void BuildPages()
    {
        _tabsPage = AddPage("All Tabs", "", "A switch to window · B back to game · LB / RB other pages",
            new WrapPanel());

        _sound = new SoundPage(this);
        _soundPage = AddPage("Sound", "", SoundPage.Hint, _sound.Content);

        AddPage("Discord", "", "B back to game · LB / RB other pages",
            Stack(Placeholder("Voice controls", "Your voice channel, who's talking, and mute, deafen and leave arrive in step 3.")));

        _settingsPage = AddPage("Settings", "", "A select · B back to game · LB / RB other pages",
            new StackPanel());
        UpdateSettings(new AppSettings());
    }

    private Page AddPage(string name, string icon, string hint, Panel content)
    {
        var page = new Page { Name = name, Icon = icon, Hint = hint, Content = content };
        int index = _pages.Count;

        // Brushes made here (not from resources) so their colors can be animated.
        page.DockFill = new SolidColorBrush(Colors.Transparent);
        page.DockInk = new SolidColorBrush(IconColor);
        page.DockLabel = new TextBlock
        {
            Text = name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 0,
            Opacity = 0,
        };
        var dockContent = new StackPanel { Orientation = Orientation.Horizontal };
        dockContent.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
        });
        dockContent.Children.Add(page.DockLabel);

        page.DockButton = new Button
        {
            Style = (Style)FindResource("DockButton"),
            ToolTip = name,
            Content = dockContent,
            Background = page.DockFill,
            Foreground = page.DockInk,
            Padding = new Thickness(13, 0, 13, 0),
        };
        page.DockButton.Click += (_, _) => SelectPage(index, focusFirst: true);
        DockItems.Children.Add(page.DockButton);

        content.Visibility = Visibility.Collapsed;
        KeyboardNavigation.SetDirectionalNavigation(content, KeyboardNavigationMode.Contained);
        PageHost.Children.Add(content);

        _pages.Add(page);
        return page;
    }

    private void SelectPage(int index, bool focusFirst)
    {
        int previous = _selected;
        _selected = (index % _pages.Count + _pages.Count) % _pages.Count;

        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            bool selected = i == _selected;
            page.Content.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AnimateDock(page, selected);
        }

        var current = _pages[_selected];
        if (current == _soundPage)
        {
            if (!_soundActive)
            {
                _soundActive = true;
                _sound.Activate(_currentWindow);
            }
        }
        else
        {
            DeactivateSound();
        }

        PageTitleText.Text = current.Name;
        PageHint.Text = current.Hint;
        PageScroller.ScrollToTop();

        if (previous != _selected)
        {
            // Slide the page in from the side it came from.
            double from = (_selected > previous ? 1 : -1) * 40;
            var duration = new Duration(TimeSpan.FromMilliseconds(240));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slide = new TranslateTransform();
            PageHost.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, duration) { EasingFunction = ease });
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        }

        if (focusFirst)
            FocusFirst();
    }

    private void DeactivateSound()
    {
        if (!_soundActive) return;
        _soundActive = false;
        _sound.Deactivate();
    }

    /// <summary>The current page's dock icon widens into a light pill and its name slides in.</summary>
    private static void AnimateDock(Page page, bool selected)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        page.DockFill.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? PillColor : Color.FromArgb(0, PillColor.R, PillColor.G, PillColor.B), duration) { EasingFunction = ease });
        page.DockInk.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? PillInkColor : IconColor, duration) { EasingFunction = ease });
        page.DockButton.BeginAnimation(Control.PaddingProperty,
            new ThicknessAnimation(selected ? new Thickness(14, 0, 18, 0) : new Thickness(13, 0, 13, 0), duration) { EasingFunction = ease });
        page.DockLabel.BeginAnimation(FrameworkElement.MaxWidthProperty,
            new DoubleAnimation(selected ? 160 : 0, duration) { EasingFunction = ease });
        page.DockLabel.BeginAnimation(FrameworkElement.MarginProperty,
            new ThicknessAnimation(selected ? new Thickness(8, 0, 0, 0) : new Thickness(0), duration) { EasingFunction = ease });
        page.DockLabel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(selected ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(selected ? 260 : 120))));
    }

    private void FocusFirst()
    {
        if (_pages[_selected] == _soundPage)
        {
            var first = _sound.FirstFocus();
            if (first is null) return;
            first.Focus();
            Keyboard.Focus(first);
            return;
        }

        var buttons = _pages[_selected].Content.Children.OfType<Button>().ToList();
        // On All Tabs, start on the first window that isn't the one you came from, like Alt+Tab.
        var target = buttons.FirstOrDefault(b => b.Tag is IntPtr h && h != _currentWindow) ?? buttons.FirstOrDefault();
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
    }

    // ---- All Tabs ---------------------------------------------------------------------------

    private void FillTabs(IReadOnlyList<OpenWindow> windows)
    {
        var panel = _tabsPage.Content;
        panel.Children.Clear();

        if (windows.Count == 0)
        {
            panel.Children.Add(Placeholder("No other windows open", "Apps and games you open show up here."));
            return;
        }

        foreach (var window in windows)
        {
            bool isCurrent = window.Handle == _currentWindow;
            // Three tiles per row fit the 880px page card (tile + outline + margin ≈ 268px each).
            var tile = new Grid { Width = 214, Height = 128 };
            tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tile.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (window.Icon is not null)
            {
                var icon = new Image { Source = window.Icon, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                tile.Children.Add(icon);
            }

            var title = new TextBlock
            {
                Text = isCurrent ? $"{window.Title}  ·  current" : window.Title,
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetRow(title, 1);
            tile.Children.Add(title);

            var button = new Button { Style = (Style)FindResource("TileButton"), Content = tile, Tag = window.Handle, ToolTip = window.Title };
            var handle = window.Handle;
            button.Click += (_, _) =>
            {
                if (handle == _currentWindow)
                    CloseRequested?.Invoke();
                else
                    SwitchToWindowRequested?.Invoke(handle);
            };
            panel.Children.Add(button);
        }
    }

    // ---- Settings ---------------------------------------------------------------------------

    /// <summary>Shows the current settings. Editing in the overlay itself arrives in step 4.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        var panel = _settingsPage.Content;
        panel.Children.Clear();

        panel.Children.Add(new TextBlock { Text = "Controls", Style = (Style)FindResource("SectionLabel") });
        panel.Children.Add(Row("Open with controller", "Hold these buttons together", string.Join(" + ", settings.ControllerCombo), () => EditSettingsRequested?.Invoke()));
        panel.Children.Add(Row("Open with keyboard", "Works from any app", settings.Hotkey, () => EditSettingsRequested?.Invoke()));

        panel.Children.Add(new TextBlock { Text = "Games", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(2, 18, 0, 10) });
        panel.Children.Add(Row("Exclusive fullscreen games", "What happens when a game doesn't allow overlays",
            settings.ExclusiveFullscreenFallback ? "Minimize and open" : "Don't open", () => EditSettingsRequested?.Invoke()));

        panel.Children.Add(new TextBlock { Text = "App", Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(2, 18, 0, 10) });
        panel.Children.Add(Row("Edit settings file", "Changing settings right here arrives in step 4", null, () => EditSettingsRequested?.Invoke()));
        panel.Children.Add(Row("Reload settings", "Apply changes from the settings file", null, () => ReloadSettingsRequested?.Invoke()));
        var quit = Row("Quit Console Overlay", null, null, () => ExitRequested?.Invoke());
        quit.Foreground = (Brush)FindResource("DangerTextBrush");
        panel.Children.Add(quit);
    }

    // ---- Building blocks --------------------------------------------------------------------

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    private Button Row(string title, string? hint, string? value, Action? onClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("RowTitle") });
        if (hint is not null)
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("RowHint") });
        grid.Children.Add(text);

        if (value is not null)
        {
            var valueText = new TextBlock { Text = value + "  ›", Style = (Style)FindResource("RowValue"), Margin = new Thickness(16, 0, 0, 0) };
            Grid.SetColumn(valueText, 1);
            grid.Children.Add(valueText);
        }

        var button = new Button { Style = (Style)FindResource("RowButton"), Content = grid };
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        return button;
    }

    private Button Placeholder(string title, string hint)
    {
        var row = Row(title, hint, null, null);
        row.Opacity = 0.75;
        return row;
    }

    // ---- Input ------------------------------------------------------------------------------

    /// <summary>Handles a controller command while the overlay is open.</summary>
    public void Navigate(NavCommand command)
    {
        if (_pages[_selected] == _soundPage && _sound.Handle(command, Keyboard.FocusedElement))
            return;

        switch (command)
        {
            case NavCommand.Back:
                CloseRequested?.Invoke();
                return;
            case NavCommand.PreviousTab:
                SelectPage(_selected - 1, focusFirst: true);
                return;
            case NavCommand.NextTab:
                SelectPage(_selected + 1, focusFirst: true);
                return;
            case NavCommand.Accept:
                if (Keyboard.FocusedElement is Button button
                    && new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
                    invoker.Invoke();
                return;
            case NavCommand.Mute or NavCommand.ColumnLeft or NavCommand.ColumnRight:
                return; // only the Sound page uses these
        }

        if (Keyboard.FocusedElement is not UIElement focused || !IsInCurrentPage(focused))
        {
            FocusFirst();
            return;
        }

        var direction = command switch
        {
            NavCommand.Up => FocusNavigationDirection.Up,
            NavCommand.Down => FocusNavigationDirection.Down,
            NavCommand.Left => FocusNavigationDirection.Left,
            _ => FocusNavigationDirection.Right,
        };
        focused.MoveFocus(new TraversalRequest(direction));
        (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
    }

    private bool IsInCurrentPage(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == _pages[_selected].Content)
                return true;
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_pages[_selected] == _soundPage && _sound.HandleKey(e.Key, Keyboard.FocusedElement))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.Q or Key.PageUp:
                SelectPage(_selected - 1, focusFirst: true);
                e.Handled = true;
                break;
            case Key.E or Key.PageDown:
                SelectPage(_selected + 1, focusFirst: true);
                e.Handled = true;
                break;
        }
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    // ---- Showing ----------------------------------------------------------------------------

    /// <summary>Shows the overlay covering one monitor (rectangle in physical pixels), always on All Tabs.</summary>
    internal void ShowOn(Native.RECT monitor, bool exclusiveMode, BitmapSource? snapshot, IReadOnlyList<OpenWindow> windows, IntPtr currentWindow)
    {
        bool useSnapshot = !exclusiveMode && snapshot is not null;
        Snapshot.Source = useSnapshot ? snapshot : null;
        Snapshot.Visibility = useSnapshot ? Visibility.Visible : Visibility.Collapsed;
        OpaqueBackdrop.Visibility = useSnapshot ? Visibility.Collapsed : Visibility.Visible;

        _currentWindow = currentWindow;
        FillTabs(windows);
        UpdateClock();
        _clock.Start();

        _visibilityToken++; // cancels a close animation that is still running
        if (!IsVisible)
            Show();

        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, Native.SWP_SHOWWINDOW);
        Activate();
        _selected = 0;
        SelectPage(0, focusFirst: true);
        AnimateIn();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var date = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
        if (date.Length > 0)
            date = char.ToUpper(date[0], CultureInfo.CurrentCulture) + date[1..];
        ClockText.Text = $"{now.ToString("t", CultureInfo.CurrentCulture)}  ·  {date}";
    }

    /// <summary>
    /// Opening: the snapshot starts sharp (it looks exactly like the screen), then blurs and dims while the
    /// page card rises in and the dock slides up.
    /// </summary>
    private void AnimateIn()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var medium = new Duration(TimeSpan.FromMilliseconds(260));
        var slow = new Duration(TimeSpan.FromMilliseconds(320));
        var delayed = TimeSpan.FromMilliseconds(60);

        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
        SnapshotBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, SnapshotBlurRadius, slow) { EasingFunction = ease });
        Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium));
        OpaqueBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium));

        PageCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium) { BeginTime = delayed });
        PageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, slow) { EasingFunction = ease, BeginTime = delayed });
        PageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, slow) { EasingFunction = ease, BeginTime = delayed });
        PageOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(28, 0, slow) { EasingFunction = ease, BeginTime = delayed });

        Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium) { BeginTime = delayed + delayed });
        DockOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, 0, slow) { EasingFunction = ease, BeginTime = delayed + delayed });
    }

    /// <summary>
    /// Closing: the reverse. The blur clears back to the sharp snapshot, so when the window disappears the
    /// real screen underneath looks the same and nothing jumps. Calls <paramref name="after"/> once hidden.
    /// </summary>
    public void HideAnimated(Action? after)
    {
        int token = ++_visibilityToken;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fast = new Duration(TimeSpan.FromMilliseconds(170));

        SnapshotBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, fast) { EasingFunction = ease });
        Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fast));
        PageCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fast));
        PageOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(20, fast) { EasingFunction = ease });
        DockOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(40, fast) { EasingFunction = ease });
        Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fast));

        // Without a snapshot (exclusive fullscreen) the whole window fades instead.
        var fade = new DoubleAnimation(OpaqueBackdrop.Visibility == Visibility.Visible ? 0 : 1, fast);
        fade.Completed += (_, _) =>
        {
            if (token != _visibilityToken) return; // reopened meanwhile
            Hide();
            after?.Invoke();
        };
        Root.BeginAnimation(OpacityProperty, fade);
    }

    private void HideFromAltTab()
    {
        var style = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
        style = (style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(style));
    }
}
