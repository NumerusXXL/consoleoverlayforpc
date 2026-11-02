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
        public IOverlayPage? Logic { get; init; }
        public Button DockButton { get; set; } = null!;
        public TextBlock DockLabel { get; set; } = null!;
        public SolidColorBrush DockFill { get; set; } = null!;
        public SolidColorBrush DockInk { get; set; } = null!;
    }

    private Color PillColor = Color.FromRgb(0xF2, 0xF4, 0xF8);
    private static readonly Color PillInkColor = Color.FromRgb(0x0B, 0x0E, 0x16);
    private static readonly Color IconColor = Color.FromRgb(0x8A, 0x93, 0xA6);
    private int _visibilityToken;

    private readonly List<Page> _pages = [];
    private readonly DispatcherTimer _clock;
    private Page _settingsPage = null!;
    private SettingsPage _settings = null!;
    private AllTabsPage _tabs = null!;
    private DiscordPage _discord = null!;
    private IOverlayPage? _activeLogic;
    private int _selected;
    private IntPtr _currentWindow;
    private string _background = "Blur";
    private string _clockFormat = "System";

    public event Action? CloseRequested;
    public event Action<IntPtr>? SwitchToWindowRequested;
    /// <summary>Close the overlay without giving focus back to the game, then run the action.</summary>
    public event Action<Action>? CloseThenRequested;

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
            // Free the snapshot, window previews and icons while hidden so the idle footprint stays small.
            _clock.Stop();
            Snapshot.Source = null;
            SnapshotBlurred.Source = null;
            DeactivateLogic();
            _tabs.Clear();
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ---- Pages ------------------------------------------------------------------------------

    private void BuildPages()
    {
        _tabs = new AllTabsPage(this, PageScroller);
        _tabs.BackToGame += () => CloseRequested?.Invoke();
        _tabs.SwitchTo += hwnd => SwitchToWindowRequested?.Invoke(hwnd);
        AddPage("All Tabs", "\uE7C4", _tabs);

        AddPage("Sound", "\uE767", new SoundPage(this));

        _discord = new DiscordPage(this);
        _discord.CloseThen += action => CloseThenRequested?.Invoke(action);
        _discord.OpenSettings += () => OpenSettingsSection("discord");
        AddPage("Discord", "\uE8BD", _discord);

        _settings = new SettingsPage(this);
        _settings.CloseThen += action => CloseThenRequested?.Invoke(action);
        _settingsPage = AddPage("Settings", "\uE713", _settings);
    }

    /// <summary>The Settings page needs the app to save, rebind and quit.</summary>
    public void SetSettingsHost(ISettingsHost host) => _settings.SetHost(host);

    public void SignOutOfDiscord() => _discord.SignOut();

    /// <summary>Opens the Settings page (from the tray menu).</summary>
    public void ShowSettings()
    {
        if (IsVisible) SelectPage(_pages.IndexOf(_settingsPage), focusFirst: true);
    }

    private void OpenSettingsSection(string section)
    {
        SelectPage(_pages.IndexOf(_settingsPage), focusFirst: true);
        _settings.ShowSection(section, focusDetail: true);
    }

    private Page AddPage(string name, string icon, IOverlayPage logic)
    {
        var page = AddPage(name, icon, logic.Hint, logic.Content, logic);
        logic.HintChanged += () =>
        {
            if (_pages[_selected] == page) ShowHint();
        };
        return page;
    }

    private Page AddPage(string name, string icon, string hint, Panel content, IOverlayPage? logic)
    {
        var page = new Page { Name = name, Icon = icon, Hint = hint, Content = content, Logic = logic };
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
        if (current.Logic != _activeLogic)
        {
            DeactivateLogic();
            _activeLogic = current.Logic;
            if (_activeLogic is { } logic)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                logic.Activate(_currentWindow);
                if (timer.ElapsedMilliseconds > 40)
                    Log.Write($"Opening the {current.Name} page took {timer.ElapsedMilliseconds} ms.");
            }
        }

        PageTitleText.Text = current.Name;
        ShowHint();
        PageScroller.ScrollToTop();

        if (previous != _selected)
        {
            // Slide the page in from the side it came from, decelerating smoothly; the title follows a beat later.
            double direction = _selected > previous ? 1 : -1;
            var duration = new Duration(TimeSpan.FromMilliseconds(340));
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            var slide = new TranslateTransform();
            PageHost.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 48, 0, duration) { EasingFunction = ease });
            PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220))));

            TitleOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 16, 0, duration) { EasingFunction = ease });
            PageTitleText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260))));
        }

        if (focusFirst)
            FocusFirst();
    }

    private void DeactivateLogic()
    {
        var logic = _activeLogic;
        _activeLogic = null;
        logic?.Deactivate();
    }

    /// <summary>The current page's dock icon widens into a light pill and its name slides in.</summary>
    private void AnimateDock(Page page, bool selected)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(selected ? 360 : 240));
        // The pill widens with a small overshoot, like it has a bit of weight; closing ones just settle.
        IEasingFunction ease = selected
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            : new QuinticEase { EasingMode = EasingMode.EaseOut };

        page.DockFill.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? PillColor : Color.FromArgb(0, PillColor.R, PillColor.G, PillColor.B), duration) { EasingFunction = ease });
        page.DockInk.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(selected ? PillInkColor : IconColor, duration) { EasingFunction = ease });
        page.DockButton.BeginAnimation(Control.PaddingProperty,
            new ThicknessAnimation(selected ? new Thickness(14, 0, 18, 0) : new Thickness(13, 0, 13, 0), duration) { EasingFunction = ease });
        page.DockLabel.BeginAnimation(FrameworkElement.MaxWidthProperty,
            new DoubleAnimation(selected ? 160 : 0, duration) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        page.DockLabel.BeginAnimation(FrameworkElement.MarginProperty,
            new ThicknessAnimation(selected ? new Thickness(8, 0, 0, 0) : new Thickness(0), duration) { EasingFunction = ease });
        page.DockLabel.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(selected ? 1 : 0, new Duration(TimeSpan.FromMilliseconds(selected ? 260 : 120))));
    }

    private void FocusFirst()
    {
        var page = _pages[_selected];
        var target = page.Logic is not null
            ? page.Logic.FirstFocus()
            : page.Content.Children.OfType<Button>().FirstOrDefault();
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
    }

    // ---- Settings ---------------------------------------------------------------------------

    /// <summary>Applies settings that change how the overlay looks, and refreshes the pages that show them.</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _discord.UpdateSettings(settings);

        PillColor = Appearance.AccentColor(settings.AccentColor);
        Resources["AccentBrush"] = new SolidColorBrush(PillColor);
        foreach (var page in _pages)
            AnimateDock(page, page == _pages[_selected]);

        _background = settings.Background;
        _clockFormat = settings.ClockFormat;
        if (IsVisible)
        {
            UpdateClock();
            ApplyBackground();
        }

        _settings.UpdateSettings();
    }

    /// <summary>Whether to capture the screen at all before opening (not needed for the dark background).</summary>
    public bool WantsSnapshot => !Appearance.Is(_background, "Dark");

    private double BlurredOpacity => Appearance.Is(_background, "Clear") ? 0 : 1;

    private void ApplyBackground()
    {
        bool useSnapshot = Snapshot.Source is not null && WantsSnapshot;
        Snapshot.Visibility = useSnapshot ? Visibility.Visible : Visibility.Collapsed;
        OpaqueBackdrop.Visibility = useSnapshot ? Visibility.Collapsed : Visibility.Visible;
        OpaqueBackdrop.BeginAnimation(OpacityProperty, null);
        OpaqueBackdrop.Opacity = 1;
        SnapshotBlurred.Visibility = Snapshot.Visibility;
        SnapshotBlurred.BeginAnimation(OpacityProperty, new DoubleAnimation(BlurredOpacity, TimeSpan.FromMilliseconds(220)));
    }

    // ---- Input ------------------------------------------------------------------------------

    private bool _keyboardHints;

    /// <summary>Handles a controller command while the overlay is open.</summary>
    public void Navigate(NavCommand command)
    {
        if (_keyboardHints)
        {
            _keyboardHints = false;
            ShowHint();
        }
        Run(command);
    }

    /// <summary>
    /// Shows the current page's button hints, written for whatever was used last: the controller, or the keyboard
    /// (Enter for A, Esc for B, Q / E for LB / RB, Tab for LT / RT).
    /// </summary>
    private void ShowHint()
    {
        var page = _pages[_selected];
        string hint = page.Logic?.Hint ?? page.Hint;
        if (_keyboardHints)
        {
            hint = hint.Replace("LB / RB", "Q / E").Replace("LT / RT", "Tab").Replace("X paste", "Ctrl+V paste");
            hint = System.Text.RegularExpressions.Regex.Replace(hint, @"\bA\b", "Enter");
            hint = System.Text.RegularExpressions.Regex.Replace(hint, @"\bB\b", "Esc");
        }
        if (PageHint.Text == hint) return;
        PageHint.Text = hint;
        PageHint.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, new Duration(TimeSpan.FromMilliseconds(200))));
    }

    /// <summary>The keyboard does everything the controller does, through the same commands.</summary>
    private static NavCommand? KeyToCommand(Key key) => key switch
    {
        Key.Up => NavCommand.Up,
        Key.Down => NavCommand.Down,
        Key.Left => NavCommand.Left,
        Key.Right => NavCommand.Right,
        Key.Enter or Key.Space => NavCommand.Accept,
        Key.Escape or Key.Back => NavCommand.Back,
        Key.Q or Key.PageUp => NavCommand.PreviousTab,
        Key.E or Key.PageDown => NavCommand.NextTab,
        Key.X => NavCommand.Secondary,
        Key.Y => NavCommand.Mute,
        _ => null,
    };

    private void Run(NavCommand command)
    {
        if (_pages[_selected].Logic?.Handle(command, Keyboard.FocusedElement) == true)
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
            case NavCommand.Mute or NavCommand.Secondary or NavCommand.ColumnLeft or NavCommand.ColumnRight:
                return; // only some pages use these
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

    private bool IsInCurrentPage(DependencyObject element) => Ui.IsInside(element, _pages[_selected].Content);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A page recording a new shortcut sees every key, Ctrl / Alt / Win included.
        if (_pages[_selected].Logic?.HandleRawKey(e) == true)
        {
            e.Handled = true;
            return;
        }

        // Leave shortcuts with Ctrl / Alt / Windows alone (Shift is fine: Shift+Tab).
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return;

        if (!_keyboardHints)
        {
            _keyboardHints = true;
            ShowHint();
        }

        // In a text field, keys type. Only Enter (save), Esc (cancel) and up / down (leave the field) still navigate.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)
        {
            if (e.Key is Key.Enter or Key.Escape or Key.Up or Key.Down)
            {
                Run(KeyToCommand(e.Key)!.Value);
                e.Handled = true;
            }
            return;
        }

        if (KeyToCommand(e.Key) is { } command)
        {
            Run(command);
            e.Handled = true;
            return;
        }

        // Extra keys a page understands (M to mute, Tab to switch column, Delete to close a window).
        if (_pages[_selected].Logic?.HandleKey(e.Key, Keyboard.FocusedElement) == true)
            e.Handled = true;
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    // ---- Showing ----------------------------------------------------------------------------

    /// <summary>Shows the overlay covering one monitor (rectangle in physical pixels), always on All Tabs.</summary>
    internal void ShowOn(Native.RECT monitor, bool exclusiveMode, BitmapSource? snapshot, IReadOnlyList<OpenWindow> windows, IntPtr currentWindow)
    {
        bool useSnapshot = !exclusiveMode && snapshot is not null && WantsSnapshot;
        Snapshot.Source = useSnapshot ? snapshot : null;
        SnapshotBlurred.Source = useSnapshot ? ScreenCapture.Blur(snapshot!) : null;
        Snapshot.Visibility = useSnapshot ? Visibility.Visible : Visibility.Collapsed;
        SnapshotBlurred.Visibility = Snapshot.Visibility;
        OpaqueBackdrop.Visibility = useSnapshot ? Visibility.Collapsed : Visibility.Visible;

        _currentWindow = currentWindow;
        DeactivateLogic(); // reopened during the close animation: start fresh
        _tabs.SetWindows(windows, currentWindow);
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
        string time = _clockFormat switch
        {
            "24h" => now.ToString("HH:mm", CultureInfo.CurrentCulture),
            "12h" => now.ToString("h:mm tt", CultureInfo.InvariantCulture),
            _ => now.ToString("t", CultureInfo.CurrentCulture),
        };
        ClockText.Text = $"{time}  ·  {date}";
    }

    /// <summary>
    /// Opening: the snapshot starts sharp (it looks exactly like the screen), then blurs and dims while the
    /// page card rises in and the dock slides up.
    /// </summary>
    private void AnimateIn()
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var settle = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
        var medium = new Duration(TimeSpan.FromMilliseconds(280));
        var slow = new Duration(TimeSpan.FromMilliseconds(420));
        var delayed = TimeSpan.FromMilliseconds(50);

        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 1;
        SnapshotBlurred.BeginAnimation(OpacityProperty, new DoubleAnimation(0, BlurredOpacity, slow) { EasingFunction = ease });
        Dim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium));
        OpaqueBackdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium));

        // The glass card rises and settles with a hint of overshoot.
        PageCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium) { BeginTime = delayed });
        PageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, slow) { EasingFunction = settle, BeginTime = delayed });
        PageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, slow) { EasingFunction = settle, BeginTime = delayed });
        PageOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(36, 0, slow) { EasingFunction = ease, BeginTime = delayed });

        Dock.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, medium) { BeginTime = delayed + delayed });
        DockOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(48, 0, slow) { EasingFunction = settle, BeginTime = delayed + delayed });

        // Dock icons pop in one after another.
        for (int i = 0; i < _pages.Count; i++)
        {
            var button = _pages[i].DockButton;
            var pop = new ScaleTransform(0.6, 0.6);
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.RenderTransform = pop;
            var begin = TimeSpan.FromMilliseconds(130 + i * 45);
            var popDuration = new Duration(TimeSpan.FromMilliseconds(360));
            var popEase = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
            pop.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.6, 1, popDuration) { BeginTime = begin, EasingFunction = popEase });
            pop.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.6, 1, popDuration) { BeginTime = begin, EasingFunction = popEase });
        }
    }

    /// <summary>
    /// Closing: the reverse. The blur clears back to the sharp snapshot, so when the window disappears the
    /// real screen underneath looks the same and nothing jumps. Calls <paramref name="after"/> once hidden.
    /// </summary>
    public void HideAnimated(Action? after)
    {
        int token = ++_visibilityToken;
        _tabs.HidePreviewsNow(); // Windows draws them on top, so they wouldn't fade with the rest
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fast = new Duration(TimeSpan.FromMilliseconds(180));
        PageScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, fast) { EasingFunction = ease });
        PageScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, fast) { EasingFunction = ease });

        SnapshotBlurred.BeginAnimation(OpacityProperty, new DoubleAnimation(0, fast) { EasingFunction = ease });
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
