using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

public partial class OverlayWindow : Window
{
    private sealed record Tab(string Name, TextBlock Header, StackPanel Content);

    private readonly List<Tab> _tabs = [];
    private int _selected;

    public event Action? CloseRequested;
    public event Action? EditSettingsRequested;
    public event Action? ReloadSettingsRequested;
    public event Action? ExitRequested;

    public OverlayWindow()
    {
        InitializeComponent();
        BuildTabs();
        SourceInitialized += (_, _) => HideFromAltTab();
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => SelectTab(0, focusFirst: false);
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    // ---- Tabs -------------------------------------------------------------------------------

    private void BuildTabs()
    {
        AddTab("Home",
            Card("Resume", "Close the overlay and go back to what you were doing.", () => CloseRequested?.Invoke()));

        AddTab("Sound",
            Card("Volume and devices", "Master volume, per-app volume and output switching arrive in step 2.", null));

        AddTab("Discord",
            Card("Voice controls", "Mute, deafen and who's talking arrive in step 3.", null));

        AddTab("All Tabs",
            Card("Open windows", "Jump between your open apps and games. Arrives in step 3.", null));

        AddTab("Settings",
            Card("Edit settings file", "Change the shortcut, controller button combo and fullscreen behavior.", () => EditSettingsRequested?.Invoke()),
            Card("Reload settings", "Apply changes from the settings file without restarting.", () => ReloadSettingsRequested?.Invoke()),
            Card("Quit Console Overlay", "Stop the overlay app. Start it again from the Start menu or the exe.", () => ExitRequested?.Invoke()));
    }

    private void AddTab(string name, params Button[] cards)
    {
        var index = _tabs.Count;
        var header = new TextBlock { Text = name, Style = (Style)FindResource("TabHeader") };
        header.MouseLeftButtonDown += (_, _) => SelectTab(index, focusFirst: true);
        TabStrip.Children.Add(header);

        var content = new StackPanel { Visibility = Visibility.Collapsed };
        KeyboardNavigation.SetDirectionalNavigation(content, KeyboardNavigationMode.Contained);
        foreach (var card in cards)
            content.Children.Add(card);
        TabContent.Children.Add(content);

        _tabs.Add(new Tab(name, header, content));
    }

    private Button Card(string title, string subtitle, Action? onClick)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("CardTitle") });
        body.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("CardSubtitle") });

        var button = new Button { Content = body, Style = (Style)FindResource("CardButton") };
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        else
            button.Opacity = 0.6; // placeholder: focusable so navigation can be tried, but does nothing
        return button;
    }

    private void SelectTab(int index, bool focusFirst)
    {
        if (_tabs.Count == 0) return;
        _selected = (index % _tabs.Count + _tabs.Count) % _tabs.Count;

        for (int i = 0; i < _tabs.Count; i++)
        {
            bool selected = i == _selected;
            _tabs[i].Header.Tag = selected ? "Selected" : null;
            _tabs[i].Content.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        ContentScroller.ScrollToTop();
        UpdateLayout();
        MoveUnderline(_tabs[_selected].Header);

        if (focusFirst)
            FocusFirstCard();
    }

    private void MoveUnderline(FrameworkElement header)
    {
        var x = header.TranslatePoint(new Point(0, 0), TabStrip).X;
        var duration = new Duration(TimeSpan.FromMilliseconds(160));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        UnderlineOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, duration) { EasingFunction = ease });
        TabUnderline.BeginAnimation(WidthProperty, new DoubleAnimation(header.ActualWidth, duration) { EasingFunction = ease });
    }

    private void FocusFirstCard()
    {
        var first = _tabs[_selected].Content.Children.OfType<Button>().FirstOrDefault();
        if (first is null) return;
        first.Focus();
        Keyboard.Focus(first);
    }

    // ---- Input ------------------------------------------------------------------------------

    /// <summary>Handles a controller command while the overlay is open.</summary>
    public void Navigate(NavCommand command)
    {
        switch (command)
        {
            case NavCommand.Back:
                CloseRequested?.Invoke();
                return;
            case NavCommand.PreviousTab:
                SelectTab(_selected - 1, focusFirst: true);
                return;
            case NavCommand.NextTab:
                SelectTab(_selected + 1, focusFirst: true);
                return;
            case NavCommand.Accept:
                if (Keyboard.FocusedElement is Button button
                    && new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is IInvokeProvider invoker)
                    invoker.Invoke();
                return;
        }

        if (Keyboard.FocusedElement is not UIElement focused || !IsInCurrentTab(focused))
        {
            FocusFirstCard();
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

    private bool IsInCurrentTab(DependencyObject element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == _tabs[_selected].Content)
                return true;
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseRequested?.Invoke();
                e.Handled = true;
                break;
            case Key.Q or Key.PageUp:
                SelectTab(_selected - 1, focusFirst: true);
                e.Handled = true;
                break;
            case Key.E or Key.PageDown:
                SelectTab(_selected + 1, focusFirst: true);
                e.Handled = true;
                break;
        }
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

    // ---- Showing ----------------------------------------------------------------------------

    /// <summary>Shows the overlay covering one monitor (rectangle in physical pixels).</summary>
    internal void ShowOn(Native.RECT monitor, bool exclusiveMode)
    {
        Backdrop.Fill = (Brush)FindResource(exclusiveMode ? "BackdropOpaqueBrush" : "BackdropBrush");
        ModeText.Text = exclusiveMode
            ? "Game minimized (exclusive fullscreen). It comes back when you close the overlay."
            : DateTime.Now.ToString("dddd, d MMMM");

        if (!IsVisible)
            Show();

        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, monitor.Left, monitor.Top, monitor.Width, monitor.Height, Native.SWP_SHOWWINDOW);
        Activate();
        SelectTab(_selected, focusFirst: true);
        AnimateIn();
    }

    private void AnimateIn()
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(180));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PanelOffset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-40, 0, duration) { EasingFunction = ease });
        OverlayPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
    }

    private void HideFromAltTab()
    {
        var style = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE).ToInt64();
        style = (style | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(style));
    }
}
