using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ConsoleOverlay.Input;
using ConsoleOverlay.Perf;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Performance page: the game's frame rate big, with 1% low, frame time and a graph of the last 60 seconds,
/// then four tiles for CPU, GPU, memory and video memory. Y turns the stats in the corner on or off.
/// GPU temperature, clock and video memory come from the NVIDIA driver; on other GPUs they say "Not supported".
/// </summary>
internal sealed class PerformancePage : IOverlayPage
{
    private static readonly Color Warm = Color.FromRgb(0xFF, 0xC2, 0x7A);

    private readonly FrameworkElement _resources;
    private readonly StackPanel _root = new();
    private ISettingsHost? _host;
    private bool _active;

    // Frame rate
    private readonly TextBlock _fps = new() { FontSize = 96, FontWeight = FontWeights.ExtraBold, LineHeight = 92, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
    private readonly TextBlock _low = Value(17);
    private readonly TextBlock _frameTime = Value(17);
    private readonly StackPanel _frameDetails = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _frameNote = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
    private readonly Canvas _graph = new() { ClipToBounds = true };
    private readonly Polyline _line = new() { Stroke = Brushes.White, StrokeThickness = 2.5, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polygon _area = new() { Fill = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)) };
    private readonly Line _topLine = Dashed();
    private readonly Line _lowLine = Dashed();
    private readonly TextBlock _topLabel = GraphLabel();
    private readonly TextBlock _lowLabel = GraphLabel();

    // Tiles
    private readonly Gauge _cpu;
    private readonly TextBlock _cpuLeft = Small();
    private readonly TextBlock _cpuRight = Small(soft: true);
    private readonly Gauge _gpu;
    private readonly TextBlock _gpuLeft = Small();
    private readonly TextBlock _gpuRight = Small(soft: true);
    private readonly Amount _memory;
    private readonly Amount _videoMemory;

    // Buttons
    private readonly StackPanel _pills = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };

    public event Action? HintChanged;

    public PerformancePage(FrameworkElement resources)
    {
        _resources = resources;
        _fps.FontFamily = (FontFamily)resources.FindResource("DisplayFont");
        var soft = (Brush)resources.FindResource("SoftTextBrush");
        _frameNote.Foreground = soft;

        // ---- Frame rate card ----
        var left = new DockPanel { LastChildFill = false, Width = 260 };
        var label = Label("FRAME RATE");
        DockPanel.SetDock(label, Dock.Top);
        left.Children.Add(label);
        _frameDetails.Children.Add(Detail(_low, "1% low", soft));
        _frameDetails.Children.Add(Detail(_frameTime, "Frame time", soft));
        DockPanel.SetDock(_frameDetails, Dock.Bottom);
        left.Children.Add(_frameDetails);
        DockPanel.SetDock(_frameNote, Dock.Bottom);
        left.Children.Add(_frameNote);
        _fps.Margin = new Thickness(-4, 14, 0, 0);
        DockPanel.SetDock(_fps, Dock.Top);
        left.Children.Add(_fps);

        _graph.Children.Add(_topLine);
        _graph.Children.Add(_lowLine);
        _graph.Children.Add(_area);
        _graph.Children.Add(_line);
        _graph.Children.Add(_topLabel);
        _graph.Children.Add(_lowLabel);
        _graph.SizeChanged += (_, _) => DrawGraph();

        var hero = new Grid();
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(left);
        Grid.SetColumn(_graph, 2);
        hero.Children.Add(_graph);
        _root.Children.Add(Card(hero, height: 250, radius: 26, padding: new Thickness(24, 22, 24, 22)));

        // ---- Tiles ----
        _cpu = new Gauge(Colors.White);
        _gpu = new Gauge(Warm);
        _memory = new Amount(resources, Colors.White);
        _videoMemory = new Amount(resources, Warm);

        var tiles = new Grid { Margin = new Thickness(-7, 20, -7, 0) };
        for (int i = 0; i < 4; i++)
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddTile(tiles, 0, "CPU", _cpu.Element, Footer(_cpuLeft, _cpuRight));
        AddTile(tiles, 1, "GPU", _gpu.Element, Footer(_gpuLeft, _gpuRight));
        AddTile(tiles, 2, "MEMORY", _memory.Text, _memory.Bar);
        AddTile(tiles, 3, "VIDEO MEMORY", _videoMemory.Text, _videoMemory.Bar);
        _root.Children.Add(tiles);

        _root.Children.Add(_pills);
    }

    public void SetHost(ISettingsHost host) => _host = host;

    /// <summary>Raised when "Stats in the corner" is turned on or off.</summary>
    public event Action? CornerStatsChanged;

    public Panel Content => _root;

    public string Hint => "Y stats in the corner · B back to game · LB / RB other pages";

    public string? Subtitle => PerfMonitor.Instance.Last?.GameName is { Length: > 0 } game
        ? $"{Trim(game, 48)} · last 60 seconds"
        : "Last 60 seconds";

    // ---- IOverlayPage -----------------------------------------------------------------------

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        var monitor = PerfMonitor.Instance;
        monitor.SetGameWindow(gameWindow);
        monitor.Updated += OnUpdated;
        monitor.Watch(this);
        ShowPills();
        if (monitor.Last is { } last) Show(last);
        else ShowEmpty();
    }

    public void Deactivate()
    {
        _active = false;
        PerfMonitor.Instance.Updated -= OnUpdated;
        PerfMonitor.Instance.Unwatch(this);
    }

    public UIElement? FirstFocus() => null;

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        switch (command)
        {
            case NavCommand.Mute:
                ToggleCornerStats();
                return true;
            case NavCommand.Secondary when PerfMonitor.Instance.FrameProblem == "permission":
                _host?.RestartAsAdmin();
                return true;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) => false;

    // ---- Showing ----------------------------------------------------------------------------

    private void OnUpdated(PerfSnapshot snapshot)
    {
        if (_active) Show(snapshot);
    }

    private void ShowEmpty()
    {
        _fps.Text = "—";
        _frameDetails.Visibility = Visibility.Collapsed;
        _frameNote.Text = "Reading…";
        _cpu.Set(null, "—");
        _gpu.Set(null, "—");
        _cpuLeft.Text = _cpuRight.Text = _gpuLeft.Text = _gpuRight.Text = "";
        _memory.Clear();
        _videoMemory.Clear();
        DrawGraph();
    }

    private void Show(PerfSnapshot snapshot)
    {
        var stats = snapshot.Stats;
        var frames = snapshot.Frames;
        string? problem = PerfMonitor.Instance.FrameProblem;

        if (frames is { Fps: > 0 })
        {
            _fps.Text = frames.Fps.ToString(CultureInfo.CurrentCulture);
            _low.Text = Math.Round(frames.OnePercentLow).ToString(CultureInfo.CurrentCulture);
            _frameTime.Text = frames.FrameTimeMs.ToString("0.0", CultureInfo.CurrentCulture) + " ms";
            _frameDetails.Visibility = Visibility.Visible;
            _frameNote.Visibility = Visibility.Collapsed;
        }
        else
        {
            _fps.Text = "—";
            _frameDetails.Visibility = Visibility.Collapsed;
            _frameNote.Visibility = Visibility.Visible;
            _frameNote.Text = problem switch
            {
                "permission" => "Counting frames needs administrator rights. Press X to restart Console Overlay as administrator.",
                "error" => "Windows wouldn't let Console Overlay count frames. The log has the reason.",
                _ => "Nothing is drawing frames right now. Go back to a game to see its frame rate.",
            };
        }

        _cpu.Set(stats.CpuLoad, $"{stats.CpuLoad}%");
        _cpuLeft.Text = $"{Environment.ProcessorCount} threads";
        _cpuRight.Text = stats.CpuGhz is double ghz ? $"{ghz:0.0} GHz" : "";

        _gpu.Set(stats.GpuLoad, stats.GpuLoad is int gpu ? $"{gpu}%" : "—");
        if (stats.GpuTemperature is int temperature)
        {
            _gpuLeft.Text = $"{temperature} °C";
            _gpuRight.Text = stats.GpuGhz is double gpuGhz ? $"{gpuGhz:0.0} GHz" : "";
        }
        else
        {
            _gpuLeft.Text = PerfMonitor.Instance.NvidiaGpu ? "—" : "Temperature: not supported";
            _gpuRight.Text = "";
        }

        _memory.Set(stats.MemoryUsedGb, stats.MemoryTotalGb);
        _videoMemory.Set(stats.VideoMemoryUsedGb, stats.VideoMemoryTotalGb);

        DrawGraph();
        ShowPills();
        HintChanged?.Invoke(); // the game's name in the subtitle
    }

    private void ShowPills()
    {
        _pills.Children.Clear();
        bool on = _host?.Settings.CornerStats == true;
        _pills.Children.Add(Ui.ActionPill(_resources, "Y", $"Stats in the corner: {(on ? "On" : "Off")}", false, ToggleCornerStats));
        if (PerfMonitor.Instance.FrameProblem == "permission")
            _pills.Children.Add(Ui.ActionPill(_resources, "X", "Allow frame rate (restart as administrator)", false, () => _host?.RestartAsAdmin()));
    }

    private void ToggleCornerStats()
    {
        if (_host is null) return;
        _host.Settings.CornerStats = !_host.Settings.CornerStats;
        _host.Save();
        ShowPills();
        CornerStatsChanged?.Invoke();
    }

    /// <summary>
    /// The last 60 seconds of frame rate, newest on the right, with dashed lines at the highest rate and at 60.
    /// </summary>
    private void DrawGraph()
    {
        double width = _graph.ActualWidth, height = _graph.ActualHeight;
        if (width <= 0 || height <= 0) return;
        var history = PerfMonitor.Instance.History.ToArray();

        int top = Math.Max(60, history.Length > 0 ? history.Max() : 60);
        double scale = top * 1.25; // a little room above the highest point
        double Y(double fps) => height - fps / scale * height;

        PlaceLine(_topLine, _topLabel, Y(top), width, top.ToString(CultureInfo.CurrentCulture));
        bool showSixty = top > 75;
        _lowLine.Visibility = _lowLabel.Visibility = showSixty ? Visibility.Visible : Visibility.Collapsed;
        if (showSixty) PlaceLine(_lowLine, _lowLabel, Y(60), width, "60");

        var points = new PointCollection();
        double step = width / 59.0;
        double x0 = width - (history.Length - 1) * step;
        for (int i = 0; i < history.Length; i++)
            points.Add(new Point(x0 + i * step, Y(history[i])));
        _line.Points = points;

        var area = new PointCollection(points);
        if (points.Count > 0)
        {
            area.Add(new Point(points[^1].X, height));
            area.Add(new Point(points[0].X, height));
        }
        _area.Points = area;
    }

    private static void PlaceLine(Line line, TextBlock label, double y, double width, string text)
    {
        line.X1 = 0;
        line.X2 = width;
        line.Y1 = line.Y2 = Math.Round(y) + 0.5;
        label.Text = text;
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, width - label.DesiredSize.Width - 4);
        Canvas.SetTop(label, y - label.DesiredSize.Height - 3);
    }

    // ---- Building blocks --------------------------------------------------------------------

    private Border Card(UIElement child, double height, double radius, Thickness padding) => new()
    {
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Padding = padding,
        Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private void AddTile(Grid tiles, int column, string title, UIElement middle, UIElement bottom)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(title));
        if (middle is FrameworkElement m) m.Margin = new Thickness(0, 12, 0, 12);
        stack.Children.Add(middle);
        stack.Children.Add(bottom);
        var card = Card(stack, height: double.NaN, radius: 22, padding: new Thickness(16));
        card.Margin = new Thickness(7, 0, 7, 0);
        Grid.SetColumn(card, column);
        tiles.Children.Add(card);
    }

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)_resources.FindResource("SubtleTextBrush"),
    };

    private static StackPanel Detail(TextBlock value, string caption, Brush soft)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 22, 0) };
        stack.Children.Add(value);
        stack.Children.Add(new TextBlock { Text = caption, FontSize = 13, Foreground = soft });
        return stack;
    }

    private static Grid Footer(TextBlock left, TextBlock right)
    {
        var grid = new Grid();
        right.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static TextBlock Value(double size) => new() { FontSize = size, FontWeight = FontWeights.Bold };

    private static TextBlock Small(bool soft = false) => new()
    {
        FontSize = 13,
        Foreground = soft ? new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)) : Brushes.White,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static Line Dashed() => new()
    {
        Stroke = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
        StrokeThickness = 1,
        StrokeDashArray = [3, 5],
    };

    private static TextBlock GraphLabel() => new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
    };

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>A ring that fills up to a percentage, with the number in the middle.</summary>
    private sealed class Gauge
    {
        private const double Size = 80, Thickness = 8;
        private readonly Path _arc = new() { StrokeThickness = Thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        private readonly TextBlock _text = new() { FontSize = 18, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        public Gauge(Color color)
        {
            _arc.Stroke = new SolidColorBrush(color);
            var grid = new Grid { Width = Size, Height = Size, HorizontalAlignment = HorizontalAlignment.Left };
            // WPF draws an ellipse's stroke inside its bounds, so this ring and the arc share the same radius.
            grid.Children.Add(new Ellipse
            {
                Stroke = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = Thickness,
            });
            grid.Children.Add(_arc);
            grid.Children.Add(_text);
            Element = grid;
        }

        public FrameworkElement Element { get; }

        public void Set(int? percent, string text)
        {
            _text.Text = text;
            double fraction = Math.Clamp((percent ?? 0) / 100.0, 0, 0.9999);
            if (fraction <= 0.001)
            {
                _arc.Data = null;
                return;
            }
            double radius = (Size - Thickness) / 2, center = Size / 2;
            double angle = fraction * 2 * Math.PI;
            var start = new Point(center, center - radius);
            var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
            _arc.Data = new PathGeometry([figure]);
        }
    }

    /// <summary>"14.2 / 32 GB" with a bar under it.</summary>
    private sealed class Amount
    {
        private readonly Run _used = new();
        private readonly Run _total = new();
        private readonly ColumnDefinition _filled = new();
        private readonly ColumnDefinition _empty = new();

        public Amount(FrameworkElement resources, Color color)
        {
            _total.FontSize = 15;
            _total.FontWeight = FontWeights.Medium;
            _total.Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF));
            Text = new TextBlock
            {
                FontFamily = (FontFamily)resources.FindResource("DisplayFont"),
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                Height = 80,
                Padding = new System.Windows.Thickness(0, 20, 0, 0),
            };
            Text.Inlines.Add(_used);
            Text.Inlines.Add(_total);

            Bar = new Grid { Height = 8 };
            Bar.ColumnDefinitions.Add(_filled);
            Bar.ColumnDefinitions.Add(_empty);
            var track = new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x29, 0xFF, 0xFF, 0xFF)) };
            Grid.SetColumnSpan(track, 2);
            Bar.Children.Add(track);
            Bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color) });
        }

        public TextBlock Text { get; }

        public Grid Bar { get; }

        public void Clear()
        {
            Set(null, null);
            _used.Text = "—";
            _used.FontSize = 30;
        }

        public void Set(double? used, double? total)
        {
            if (used is not double u || total is not double t || t <= 0)
            {
                _used.Text = used is null && total is null ? "Not supported" : "—";
                _used.FontSize = used is null && total is null ? 20 : 30;
                _total.Text = "";
                _filled.Width = new GridLength(0.0001, GridUnitType.Star);
                _empty.Width = new GridLength(1, GridUnitType.Star);
                return;
            }
            _used.FontSize = 30;
            _used.Text = u.ToString("0.0", CultureInfo.CurrentCulture);
            _total.Text = $" / {t:0} GB";
            double fraction = Math.Clamp(u / t, 0.0001, 1);
            _filled.Width = new GridLength(fraction, GridUnitType.Star);
            _empty.Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star);
        }
    }
}
