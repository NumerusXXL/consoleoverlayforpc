using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ConsoleOverlay.Interop;
using ConsoleOverlay.Perf;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The small stats box in a corner of the screen while playing: frame rate and frame time, CPU and GPU load and
/// temperature, and a line of the last 60 seconds. Clicks go through it, it never takes focus, and it is hidden
/// while the overlay is open. It shows over borderless and windowed games; exclusive fullscreen ones draw over it.
/// </summary>
internal sealed class CornerStatsWindow : Window
{
    private const int EdgeMargin = 20;
    private static readonly Color Green = Color.FromRgb(0x7C, 0xF2, 0xB0);

    private readonly TextBlock _fps = new() { FontSize = 22, FontWeight = FontWeights.Bold };
    private readonly TextBlock _frameTime = Soft();
    private readonly TextBlock _cpu = new();
    private readonly TextBlock _gpu = new();
    private readonly Polyline _line = new() { Stroke = new SolidColorBrush(Green), StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
    private string _position = "TopLeft";
    private bool _watching;

    public CornerStatsWindow()
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
        FontSize = 12;
        Foreground = Brushes.White;
        Title = "Console Overlay stats";

        var top = new Grid();
        top.Children.Add(_fps);
        _frameTime.HorizontalAlignment = HorizontalAlignment.Right;
        _frameTime.VerticalAlignment = VerticalAlignment.Bottom;
        _frameTime.Margin = new Thickness(0, 0, 0, 4);
        top.Children.Add(_frameTime);

        var stack = new StackPanel { Width = 150 };
        stack.Children.Add(top);
        stack.Children.Add(Row("CPU", _cpu));
        stack.Children.Add(Row("GPU", _gpu));
        var graph = new Canvas { Height = 20, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };
        graph.Children.Add(_line);
        stack.Children.Add(graph);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x08, 0x05, 0x0E)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 12, 10),
            Child = stack,
        };

        SourceInitialized += (_, _) =>
        {
            // Click-through, no focus, not in Alt+Tab.
            var handle = new WindowInteropHelper(this).Handle;
            long style = Native.GetWindowLongPtr(handle, Native.GWL_EXSTYLE).ToInt64();
            style |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            style &= ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(handle, Native.GWL_EXSTYLE, new IntPtr(style));
        };
        SizeChanged += (_, _) => Place();
    }

    /// <summary>Shows or hides the box. Reads the stats only while it is shown.</summary>
    public void SetShown(bool shown, string position)
    {
        _position = position;
        if (shown)
        {
            if (!_watching)
            {
                _watching = true;
                PerfMonitor.Instance.SetGameWindow(IntPtr.Zero); // follow whatever is in front
                PerfMonitor.Instance.Updated += Show;
                PerfMonitor.Instance.Watch(this);
                if (PerfMonitor.Instance.Last is { } last) Show(last);
            }
            if (!IsVisible) base.Show();
            Place();
        }
        else
        {
            if (_watching)
            {
                _watching = false;
                PerfMonitor.Instance.Updated -= Show;
                PerfMonitor.Instance.Unwatch(this);
            }
            if (IsVisible) Hide();
        }
    }

    private void Show(PerfSnapshot snapshot)
    {
        var frames = snapshot.Frames;
        var stats = snapshot.Stats;
        if (frames is { Fps: > 0 })
        {
            _fps.Text = frames.Fps.ToString(CultureInfo.CurrentCulture);
            _frameTime.Text = $"FPS · {frames.FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture)} ms";
        }
        else
        {
            _fps.Text = "—";
            _frameTime.Text = PerfMonitor.Instance.FrameProblem == "permission" ? "FPS needs admin" : "FPS";
        }
        _cpu.Text = $"{stats.CpuLoad}%";
        _gpu.Text = stats.GpuLoad is int gpu
            ? stats.GpuTemperature is int temperature ? $"{gpu}% · {temperature}°" : $"{gpu}%"
            : "—";

        var history = PerfMonitor.Instance.History.ToArray();
        double width = 150, height = 20;
        double top = Math.Max(60, history.Length > 0 ? history.Max() : 60) * 1.2;
        var points = new PointCollection();
        double step = width / 59.0, x0 = width - (history.Length - 1) * step;
        for (int i = 0; i < history.Length; i++)
            points.Add(new Point(x0 + i * step, height - 2 - history[i] / top * (height - 4)));
        _line.Points = points;

        Place(); // the game may have moved to another screen
    }

    /// <summary>Puts the box in the chosen corner of the screen the game is on.</summary>
    private void Place()
    {
        if (!IsVisible) return;
        var handle = new WindowInteropHelper(this).Handle;
        var foreground = Native.GetForegroundWindow();
        var monitor = Native.MonitorFromWindow(foreground != IntPtr.Zero ? foreground : handle, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int width = (int)Math.Ceiling(ActualWidth * dpi), height = (int)Math.Ceiling(ActualHeight * dpi);
        int margin = (int)(EdgeMargin * dpi);
        var area = info.rcMonitor;
        bool right = _position.EndsWith("Right", StringComparison.Ordinal);
        bool bottom = _position.StartsWith("Bottom", StringComparison.Ordinal);
        int x = right ? area.Right - width - margin : area.Left + margin;
        int y = bottom ? area.Bottom - height - margin : area.Top + margin;
        Native.SetWindowPos(handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOACTIVATE | SWP_NOSIZE);
    }

    private static Grid Row(string name, TextBlock value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        grid.Children.Add(Soft(name));
        value.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(value);
        return grid;
    }

    private static TextBlock Soft(string text = "") => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(Color.FromArgb(0xBF, 0xFF, 0xFF, 0xFF)),
    };

    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOSIZE = 0x0001;
}
