using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ConsoleOverlay.Capture;
using ConsoleOverlay.Input;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Capture page: three big tiles (Screenshot, Save last clip, Record) and a row of the newest captures.
/// Screenshots are taken by Console Overlay once the overlay is gone; clips and recording go to the NVIDIA app or
/// Xbox Game Bar (Settings › Capture).
/// </summary>
internal sealed class CapturePage : IOverlayPage
{
    private const int RecentCount = 4;

    private readonly FrameworkElement _resources;
    private readonly StackPanel _root = new();
    private readonly Grid _tiles = new();
    private readonly Grid _recent = new();
    private readonly TextBlock _recordTitle = new();
    private readonly TextBlock _recordHint = new();
    private readonly TextBlock _clipHint = new();
    private readonly TextBlock _note = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 16, 0, 0) };
    private readonly Button _screenshot;
    private readonly Button _clip;
    private readonly Button _record;
    private ISettingsHost? _host;
    private IntPtr _gameWindow;
    private string? _gameName;
    private bool _recording;
    private bool _active;
    private int _loadToken;

    public event Action? HintChanged;

    /// <summary>Close the overlay, give the game its focus back, then run this.</summary>
    public event Action<Action>? BackToGameThen;

    /// <summary>Close the overlay (without going back to the game) and run this, like opening a file.</summary>
    public event Action<Action>? CloseThen;

    public CapturePage(FrameworkElement resources)
    {
        _resources = resources;
        _note.Foreground = (Brush)resources.FindResource("SoftTextBrush");

        for (int i = 0; i < 3; i++)
            _tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tiles.Margin = new Thickness(-9, 0, -9, 0);

        _screenshot = Tile(0, Ui.Icon(resources, "", 32), new TextBlock { Text = "Screenshot" },
            new TextBlock { Text = "The game, without the overlay" }, TakeScreenshot);
        _clipHint.Text = "";
        _clip = Tile(1, Ui.Icon(resources, "", 32), new TextBlock { Text = "Save last clip" }, _clipHint, SaveClip);
        _recordTitle.Text = "Record";
        _record = Tile(2, RecordMark(), _recordTitle, _recordHint, ToggleRecording);
        _root.Children.Add(_tiles);
        _root.Children.Add(_note);

        _root.Children.Add(new TextBlock
        {
            Text = "Recent",
            FontFamily = (FontFamily)resources.FindResource("DisplayFont"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(2, 30, 0, 14),
        });
        for (int i = 0; i < RecentCount + 1; i++)
            _recent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _recent.Margin = new Thickness(-8, 0, -8, 0);
        _root.Children.Add(_recent);
    }

    public void SetHost(ISettingsHost host) => _host = host;

    public Panel Content => _root;

    public string Hint => "B back to game · LB / RB other pages";

    public string? Subtitle => $"Clips and recording by {CaptureService.Name(Recorder)}";

    private string Recorder => CaptureService.Effective(_host?.Settings.Recorder ?? CaptureService.Nvidia);

    // ---- IOverlayPage -----------------------------------------------------------------------

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        _gameWindow = gameWindow;
        _gameName = WindowTitle(gameWindow);
        ShowRecorder();
        _ = LoadRecentAsync();
        HintChanged?.Invoke();
    }

    public void Deactivate()
    {
        _active = false;
        _loadToken++;
        _recent.Children.Clear(); // let go of the thumbnails while hidden
    }

    public UIElement? FirstFocus() => _screenshot;

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (focused is not DependencyObject d) return false;
        switch (command)
        {
            case NavCommand.Down when Ui.IsInside(d, _tiles):
                int column = _tiles.Children.OfType<Button>().ToList().FindIndex(b => Ui.IsInside(d, b));
                var below = _recent.Children.OfType<Button>().ToList();
                if (below.Count > 0) Focus(below[Math.Clamp(column, 0, below.Count - 1)]);
                return true;
            case NavCommand.Up when Ui.IsInside(d, _recent):
                int index = _recent.Children.OfType<Button>().ToList().FindIndex(b => Ui.IsInside(d, b));
                Focus(_tiles.Children.OfType<Button>().ElementAt(Math.Clamp(index, 0, 2)));
                return true;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) => false;

    // ---- Actions ----------------------------------------------------------------------------

    private void TakeScreenshot()
    {
        var window = _gameWindow;
        var name = _gameName;
        BackToGameThen?.Invoke(async () =>
        {
            await Task.Delay(350); // the overlay's fade is over and the game has drawn a frame again
            var path = CaptureService.Screenshot(window, name);
            if (path is null)
            {
                ToastWindow.Show("Capture", "Screenshot didn't work. The log has the reason.");
                return;
            }
            var file = new CaptureFile(path, false, DateTime.Now);
            await CaptureService.LoadThumbnailsAsync([file]);
            ToastWindow.Show("Capture", "Screenshot saved", file.Thumbnail);
        });
    }

    private void SaveClip()
    {
        string recorder = Recorder;
        BackToGameThen?.Invoke(async () =>
        {
            await Task.Delay(300);
            CaptureService.SaveReplay(recorder);
        });
    }

    private void ToggleRecording()
    {
        string recorder = Recorder;
        _recording = !_recording;
        ShowRecorder();
        BackToGameThen?.Invoke(async () =>
        {
            await Task.Delay(300);
            CaptureService.ToggleRecording(recorder);
        });
    }

    // ---- Showing ----------------------------------------------------------------------------

    private void ShowRecorder()
    {
        string recorder = Recorder;
        bool nvidia = recorder == CaptureService.Nvidia;
        _clipHint.Text = nvidia ? "Instant Replay in NVIDIA app" : "Record that, in Game Bar";
        _recordTitle.Text = _recording ? "Stop recording" : "Record";
        _recordHint.Text = nvidia ? "Starts and stops · NVIDIA app" : "Starts and stops · Game Bar";

        if (nvidia && !CaptureService.NvidiaOverlayRunning)
            _note.Text = "The NVIDIA app's in-game overlay isn't running. Turn it on in the NVIDIA app (and Instant Replay for clips), or pick Game Bar in Settings › Capture.";
        else if (!nvidia && _host?.Settings.Recorder == CaptureService.Nvidia)
            _note.Text = "NVIDIA app: not supported on this PC (no NVIDIA graphics card), so Xbox Game Bar is used. Turn on \"Record what happened\" in Windows Settings › Gaming › Captures for clips.";
        else if (!nvidia)
            _note.Text = "For clips, turn on \"Record what happened\" in Windows Settings › Gaming › Captures.";
        else
            _note.Text = "";
        _note.Visibility = _note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadRecentAsync()
    {
        int token = ++_loadToken;
        _recent.Children.Clear();
        var files = await CaptureService.RecentAsync(RecentCount);
        if (token != _loadToken || !_active) return;

        bool hadFocus = Keyboard.FocusedElement is DependencyObject d && Ui.IsInside(d, _recent);
        _recent.Children.Clear();
        for (int i = 0; i < files.Count; i++)
            AddRecent(i, files[i]);
        AddAllCaptures(files.Count == 0 ? 0 : RecentCount, files.Count == 0);
        if (hadFocus) Focus(_recent.Children.OfType<Button>().FirstOrDefault());

        await CaptureService.LoadThumbnailsAsync(files);
        if (token != _loadToken || !_active) return;
        foreach (var button in _recent.Children.OfType<Button>())
            if (button.Tag is CaptureFile file && button.Content is Grid grid)
                ShowThumbnail(grid, file);
    }

    private void AddRecent(int column, CaptureFile file)
    {
        var grid = new Grid { Height = 124 };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = new LinearGradientBrush(Color.FromRgb(0x5A, 0x21, 0x60), Color.FromRgb(0x2A, 0x16, 0x30), 60),
        });
        var button = new Button
        {
            Style = (Style)_resources.FindResource("CoverButton"),
            Content = grid,
            Tag = file,
            Margin = new Thickness(8, 0, 8, 0),
        };
        string path = file.Path;
        button.Click += (_, _) => CloseThen?.Invoke(() => CaptureService.Open(path));
        Grid.SetColumn(button, column);
        _recent.Children.Add(button);
        ShowThumbnail(grid, file);
    }

    private static void ShowThumbnail(Grid grid, CaptureFile file)
    {
        while (grid.Children.Count > 1) grid.Children.RemoveAt(1);
        if (file.Thumbnail is not null)
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(20), Background = new ImageBrush(file.Thumbnail) { Stretch = Stretch.UniformToFill } });
        if (file.IsVideo)
        {
            grid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10),
                Padding = new Thickness(8, 3, 8, 3),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
                Child = new TextBlock
                {
                    Text = file.Duration is TimeSpan d ? Duration(d) : "Clip",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                },
            });
        }
    }

    private void AddAllCaptures(int column, bool empty)
    {
        var text = new TextBlock
        {
            Text = empty ? "Nothing yet. Open the captures folder" : "All captures",
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
        };
        var grid = new Grid { Height = 124 };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)) });
        grid.Children.Add(text);
        var button = new Button
        {
            Style = (Style)_resources.FindResource("CoverButton"),
            Content = grid,
            Margin = new Thickness(8, 0, 8, 0),
        };
        button.Click += (_, _) => CloseThen?.Invoke(CaptureService.OpenFolder);
        Grid.SetColumn(button, column);
        if (empty) Grid.SetColumnSpan(button, 2);
        _recent.Children.Add(button);
    }

    // ---- Building blocks --------------------------------------------------------------------

    /// <summary>A big tile: icon at the top, title and a hint at the bottom. Focused, it turns white.</summary>
    private Button Tile(int column, UIElement icon, TextBlock title, TextBlock hint, Action press)
    {
        title.FontFamily = (FontFamily)_resources.FindResource("DisplayFont");
        title.FontSize = 26;
        title.FontWeight = FontWeights.Bold;
        hint.FontSize = 14;
        hint.Opacity = 0.75;
        hint.Margin = new Thickness(0, 4, 0, 0);
        hint.TextTrimming = TextTrimming.CharacterEllipsis;

        var layout = new DockPanel { Height = 142, LastChildFill = false };
        DockPanel.SetDock(icon, Dock.Top);
        if (icon is FrameworkElement f) f.HorizontalAlignment = HorizontalAlignment.Left;
        layout.Children.Add(icon);
        DockPanel.SetDock(hint, Dock.Bottom);
        layout.Children.Add(hint);
        DockPanel.SetDock(title, Dock.Bottom);
        layout.Children.Add(title);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = layout,
            Padding = new Thickness(24),
            Margin = new Thickness(9, 0, 9, 0),
        };
        // The focused tile is the white one, like the mockup.
        button.GotKeyboardFocus += (_, _) => button.Uid = "selected";
        button.LostKeyboardFocus += (_, _) => button.Uid = "";
        button.Click += (_, _) => press();
        Grid.SetColumn(button, column);
        _tiles.Children.Add(button);
        return button;
    }

    /// <summary>A white ring with a red dot: the record button.</summary>
    private static Grid RecordMark()
    {
        var grid = new Grid { Width = 36, Height = 36 };
        grid.Children.Add(new Ellipse { StrokeThickness = 2.5, Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)) });
        grid.Children.Add(new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x6A)) });
        // The ring follows the tile's text color (dark when the tile is white).
        ((Ellipse)grid.Children[0]).SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        return grid;
    }

    private static void Focus(Button? button)
    {
        if (button is null) return;
        button.Focus();
        Keyboard.Focus(button);
    }

    private static string? WindowTitle(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        int length = Interop.Native.GetWindowTextLength(window);
        if (length <= 0) return null;
        var text = new System.Text.StringBuilder(length + 1);
        Interop.Native.GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    private static string Duration(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
