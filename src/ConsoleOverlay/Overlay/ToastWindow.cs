using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ConsoleOverlay.Interop;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Small frosted notes in the top right corner while playing ("Screenshot saved"). Clicks go through them, they
/// never take focus, and each fades out after a few seconds. Up to three show at once.
/// </summary>
internal sealed class ToastWindow : Window
{
    private static ToastWindow? _instance;
    private readonly StackPanel _stack = new() { Width = 340 };

    private ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Foreground = Brushes.White;
        Title = "Console Overlay notifications";
        Content = _stack;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            long style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            style |= 0x20 /* transparent */ | 0x80000 /* layered */ | 0x08000000 /* no activate */ | Native.WS_EX_TOOLWINDOW;
            style &= ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style));
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Shows a note: a small label ("CAPTURE"), a line of text, and an optional picture on the left.</summary>
    public static void Show(string label, string text, ImageSource? picture = null)
    {
        try
        {
            _instance ??= new ToastWindow();
            _instance.Add(label, text, picture);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not show a notification: {ex.Message}");
        }
    }

    private void Add(string label, string text, ImageSource? picture)
    {
        var art = new Border
        {
            Width = picture is null ? 0 : 64,
            Height = 42,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, picture is null ? 0 : 12, 0),
            Background = picture is null ? null : new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
        };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC7, 0xFF, 0xFF, 0xFF)),
        });
        words.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var now = new TextBlock { Text = "now", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)) };

        var layout = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        DockPanel.SetDock(now, Dock.Right);
        layout.Children.Add(art);
        layout.Children.Add(now);
        layout.Children.Add(words);

        var toast = new Border
        {
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(22),
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x24, 0x1A, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = layout,
            Opacity = 0,
            RenderTransform = new TranslateTransform(24, 0),
        };
        _stack.Children.Add(toast);
        while (_stack.Children.Count > 3) _stack.Children.RemoveAt(0);

        if (!IsVisible) base.Show();
        Place();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        ((TranslateTransform)toast.RenderTransform).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) =>
            {
                _stack.Children.Remove(toast);
                if (_stack.Children.Count == 0) Hide();
            };
            toast.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    /// <summary>The top right corner of the screen the game is on.</summary>
    private void Place()
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        var foreground = Native.GetForegroundWindow();
        var monitor = Native.MonitorFromWindow(foreground != IntPtr.Zero ? foreground : handle, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Ceiling(ActualWidth * dpi);
        int margin = (int)(24 * dpi);
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, info.rcMonitor.Right - width - margin, info.rcMonitor.Top + margin, 0, 0,
            Native.SWP_NOACTIVATE | 0x0001 /* no size */);
    }
}
