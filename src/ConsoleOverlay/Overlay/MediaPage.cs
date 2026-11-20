using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Media;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Media page: what's playing (from Windows' media controls), with its cover, a big title, a progress bar and
/// previous / play-pause / next. Other apps that are playing sit below as cards; A on one shows it instead.
/// LT / RT jump 10 seconds back or forward.
/// </summary>
internal sealed class MediaPage : IOverlayPage
{
    private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    private readonly FrameworkElement _resources;
    private readonly MediaService _media = new();
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _tick;
    private readonly Grid _root = new();

    // Now playing
    private readonly Border _cover = new() { Width = 340, Height = 340, CornerRadius = new CornerRadius(28) };
    private readonly Border _coverFallback = new() { Width = 340, Height = 340, CornerRadius = new CornerRadius(28) };
    private readonly TextBlock _app = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _appChip = new();
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _artist = new() { FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ColumnDefinition _played = new();
    private readonly ColumnDefinition _left = new();
    private readonly Grid _progress = new() { Height = 18, Margin = new Thickness(0, 34, 0, 0) };
    private readonly TextBlock _elapsed = new() { FontSize = 13 };
    private readonly TextBlock _remaining = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Button _previous;
    private readonly Button _playPause;
    private readonly Button _next;
    private readonly TextBlock _playIcon;
    private readonly StackPanel _nowPlaying = new();
    private readonly StackPanel _nothing = new() { VerticalAlignment = VerticalAlignment.Center };

    // Also playing
    private readonly TextBlock _alsoLabel;
    private readonly StackPanel _others = new() { Orientation = Orientation.Horizontal };

    private string? _shownId;   // the player shown big; null = Windows' current one
    private string _othersSignature = "";
    private bool _active;
    private bool _refreshing;

    public event Action? HintChanged;

    /// <summary>Go to the Shortcuts page (from "Open a music app").</summary>
    public event Action? OpenShortcuts;

    public MediaPage(FrameworkElement resources)
    {
        _resources = resources;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) =>
        {
            if (_refreshing) return;
            _refreshing = true;
            try
            {
                await _media.RefreshAsync();
            }
            finally
            {
                _refreshing = false;
            }
            if (_active) Show();
        };
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _tick.Tick += (_, _) => ShowProgress();

        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Cover: the album art, or a soft gradient with a ring when the app gives none.
        _coverFallback.Background = new LinearGradientBrush(Color.FromRgb(0xFF, 0xB0, 0x6A), Color.FromRgb(0x7A, 0x2D, 0xD6), new Point(0, 0), new Point(1, 1));
        _coverFallback.Child = new Ellipse { Width = 240, Height = 240, Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), StrokeThickness = 2 };
        var coverStack = new Grid { VerticalAlignment = VerticalAlignment.Top };
        coverStack.Children.Add(_coverFallback);
        coverStack.Children.Add(_cover);
        _root.Children.Add(coverStack);

        // Right side
        _appChip.Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        _appChip.CornerRadius = new CornerRadius(14);
        _appChip.Padding = new Thickness(12, 5, 14, 6);
        _appChip.HorizontalAlignment = HorizontalAlignment.Left;
        _appChip.Margin = new Thickness(0, 20, 0, 18);
        var chip = new StackPanel { Orientation = Orientation.Horizontal };
        chip.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(0x3E, 0xE0, 0x8F)) });
        chip.Children.Add(_app);
        _appChip.Child = chip;

        _title.Style = (Style)resources.FindResource("HeroTitle");
        _title.FontSize = 48;
        _artist.Foreground = (Brush)resources.FindResource("SoftTextBrush");

        _progress.ColumnDefinitions.Add(_played);
        _progress.ColumnDefinitions.Add(_left);
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = (Brush)resources.FindResource("TrackBrush") };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Brushes.White };
        var knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, -9, 0) };
        _progress.Children.Add(track);
        _progress.Children.Add(fill);
        _progress.Children.Add(knob);

        var times = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        _elapsed.Foreground = _remaining.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        times.Children.Add(_elapsed);
        times.Children.Add(_remaining);

        _previous = Round("", 64, 22, "Previous", () => Do(p => _media.PreviousAsync(p)));
        _playIcon = Ui.Icon(resources, "", 34);
        _playPause = Round(null, 96, 0, "Play / pause", () => Do(p => _media.PlayPauseAsync(p)));
        _playPause.Content = _playIcon;
        _playPause.Uid = "selected"; // white
        _next = Round("", 64, 22, "Next", () => Do(p => _media.NextAsync(p)));
        _playPause.Margin = new Thickness(18, 0, 18, 0);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 26, 0, 0) };
        controls.Children.Add(_previous);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);
        KeyboardNavigation.SetDirectionalNavigation(controls, KeyboardNavigationMode.Continue);

        _nowPlaying.Children.Add(_appChip);
        _nowPlaying.Children.Add(_title);
        _nowPlaying.Children.Add(_artist);
        _nowPlaying.Children.Add(_progress);
        _nowPlaying.Children.Add(times);
        _nowPlaying.Children.Add(controls);
        Grid.SetColumn(_nowPlaying, 2);
        _root.Children.Add(_nowPlaying);

        _nothing.Children.Add(new TextBlock { Text = "Nothing is playing", Style = (Style)resources.FindResource("HeroTitle") });
        _nothing.Children.Add(new TextBlock
        {
            Text = "Play something in Spotify, a browser or any app, and it shows up here with its controls.",
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = (Brush)resources.FindResource("SoftTextBrush"),
        });
        Grid.SetColumn(_nothing, 2);
        _root.Children.Add(_nothing);

        // Also playing
        var also = new StackPanel { Margin = new Thickness(0, 36, 0, 0) };
        _alsoLabel = new TextBlock { Text = "ALSO PLAYING", Style = (Style)resources.FindResource("SectionLabel"), FontWeight = FontWeights.Bold };
        also.Children.Add(_alsoLabel);
        also.Children.Add(_others);
        Grid.SetRow(also, 1);
        Grid.SetColumnSpan(also, 3);
        _root.Children.Add(also);
    }

    public Panel Content => _root;

    public string Hint => "LT / RT back or forward 10 s · B back to game · LB / RB other pages";

    public string? Subtitle => _media.Players.Count switch
    {
        0 => null,
        1 => "1 player",
        int n => $"{n} players",
    };

    // ---- IOverlayPage -----------------------------------------------------------------------

    public async void Activate(IntPtr gameWindow)
    {
        _active = true;
        _shownId = null;
        _othersSignature = "";
        Show();
        _poll.Start();
        _tick.Start();
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await _media.RefreshAsync();
        }
        finally
        {
            _refreshing = false;
        }
        if (!_active) return;
        Show();
        if (Keyboard.FocusedElement is not DependencyObject d || !Ui.IsInside(d, _root))
            FocusButton(FirstFocus() as Button);
    }

    public void Deactivate()
    {
        _active = false;
        _poll.Stop();
        _tick.Stop();
        _cover.Background = null; // let go of the cover art while hidden
        _others.Children.Clear();
        _othersSignature = "";
    }

    public UIElement? FirstFocus() =>
        Shown is not null ? _playPause : _others.Children.OfType<Button>().FirstOrDefault();

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        switch (command)
        {
            case NavCommand.ColumnLeft or NavCommand.ColumnRight:
                if (Shown is { } player)
                {
                    _ = _media.SeekAsync(player, command == NavCommand.ColumnRight ? SeekStep : -SeekStep);
                    ShowProgress();
                }
                return true;
            case NavCommand.Down when focused is DependencyObject d && Ui.IsInside(d, _nowPlaying):
                FocusButton(_others.Children.OfType<Button>().FirstOrDefault());
                return true;
            case NavCommand.Up when focused is DependencyObject d && Ui.IsInside(d, _others):
                FocusButton(Shown is not null ? _playPause : null);
                return true;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) => key switch
    {
        Key.Tab => Handle((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? NavCommand.ColumnLeft : NavCommand.ColumnRight, focused),
        Key.MediaPlayPause => Do(p => _media.PlayPauseAsync(p)),
        _ => false,
    };

    // ---- Showing ----------------------------------------------------------------------------

    private PlayingApp? Shown =>
        _media.Players.FirstOrDefault(p => p.Id == _shownId) ?? _media.Players.FirstOrDefault();

    private void Show()
    {
        var player = Shown;
        _nowPlaying.Visibility = player is null ? Visibility.Collapsed : Visibility.Visible;
        _nothing.Visibility = player is null ? Visibility.Visible : Visibility.Collapsed;

        if (player is not null)
        {
            _app.Text = player.AppName;
            _title.Text = player.Title.Length > 0 ? player.Title : player.AppName;
            _artist.Text = string.Join(" · ", new[] { player.Artist, player.Album }.Where(t => t.Length > 0));
            _cover.Background = player.Cover is null ? null : new ImageBrush(player.Cover) { Stretch = Stretch.UniformToFill };
            _playIcon.Text = player.Playing ? "" : ""; // pause / play
            _previous.IsEnabled = player.CanPrevious;
            _next.IsEnabled = player.CanNext;
            _previous.Opacity = player.CanPrevious ? 1 : 0.4;
            _next.Opacity = player.CanNext ? 1 : 0.4;
        }
        else
        {
            _cover.Background = null;
        }
        ShowProgress();
        ShowOthers(player);
        HintChanged?.Invoke(); // the subtitle (number of players) may have changed
    }

    private void ShowProgress()
    {
        var player = Shown;
        bool known = player is not null && player.Duration > TimeSpan.Zero;
        _progress.Visibility = known ? Visibility.Visible : Visibility.Hidden;
        _elapsed.Visibility = _remaining.Visibility = _progress.Visibility;
        if (!known) return;

        var now = player!.PositionNow;
        double fraction = Math.Clamp(now.TotalSeconds / player.Duration.TotalSeconds, 0, 1);
        _played.Width = new GridLength(Math.Max(fraction, 0.0001), GridUnitType.Star);
        _left.Width = new GridLength(Math.Max(1 - fraction, 0.0001), GridUnitType.Star);
        _elapsed.Text = Time(now);
        _remaining.Text = "−" + Time(player.Duration - now);
    }

    /// <summary>The other players as cards, plus "Open a music app". Rebuilt only when the list changes.</summary>
    private void ShowOthers(PlayingApp? shown)
    {
        var others = _media.Players.Where(p => p != shown).ToList();
        string signature = string.Join("|", others.Select(p => $"{p.Id}:{p.Title}:{p.Playing}:{p.Cover is not null}")) + "|" + shown?.Id;
        if (signature == _othersSignature) return;
        _othersSignature = signature;

        string? focusedId = (Keyboard.FocusedElement as Button)?.Tag as string;
        bool hadFocus = Keyboard.FocusedElement is DependencyObject d && Ui.IsInside(d, _others);
        _others.Children.Clear();
        _alsoLabel.Text = others.Count > 0 ? "ALSO PLAYING" : "PLAY SOMETHING";

        foreach (var player in others)
            _others.Children.Add(OtherCard(player));
        _others.Children.Add(OpenAppCard());

        if (hadFocus)
            FocusButton(_others.Children.OfType<Button>().FirstOrDefault(b => b.Tag as string == focusedId)
                        ?? _others.Children.OfType<Button>().FirstOrDefault());
    }

    private Button OtherCard(PlayingApp player)
    {
        var art = new Border
        {
            Width = 84,
            Height = 48,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 0, 16, 0),
            Background = player.Cover is null
                ? new LinearGradientBrush(Color.FromRgb(0x2F, 0x6B, 0xFF), Color.FromRgb(0x1B, 0x2C, 0x6B), 0)
                : new ImageBrush(player.Cover) { Stretch = Stretch.UniformToFill },
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = player.Title.Length > 0 ? player.Title : player.AppName, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{player.AppName} · {(player.Playing ? "Playing" : "Paused")}", Style = (Style)_resources.FindResource("RowHint"), TextTrimming = TextTrimming.CharacterEllipsis });
        var layout = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        layout.Children.Add(art);
        layout.Children.Add(text);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = layout,
            Tag = player.Id,
            Width = 372,
            Padding = new Thickness(12, 12, 18, 12),
            Margin = new Thickness(0, 0, 16, 0),
        };
        string id = player.Id;
        button.Click += (_, _) =>
        {
            _shownId = id;
            _othersSignature = "";
            Show();
            FocusButton(_playPause);
        };
        return button;
    }

    private Button OpenAppCard()
    {
        var plus = Ui.Icon(_resources, "", 18);
        plus.Margin = new Thickness(6, 0, 20, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = "Open a music app", FontSize = 15, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = "From your Shortcuts", Style = (Style)_resources.FindResource("RowHint") });
        var layout = new DockPanel { MinHeight = 48 };
        DockPanel.SetDock(plus, Dock.Left);
        layout.Children.Add(plus);
        layout.Children.Add(text);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = layout,
            Tag = "open-app",
            Uid = "plain",
            Width = 372,
            Padding = new Thickness(12, 12, 18, 12),
        };
        // A dashed outline instead of a pane, like an empty slot.
        button.Loaded += (_, _) =>
        {
            if (button.Template.FindName("Pane", button) is Border pane)
            {
                pane.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
                pane.BorderThickness = new Thickness(1.5);
            }
        };
        button.Click += (_, _) => OpenShortcuts?.Invoke();
        return button;
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private Button Round(string? glyph, double size, double iconSize, string tip, Action press)
    {
        var button = new Button
        {
            Style = (Style)_resources.FindResource("RoundButton"),
            Width = size,
            Height = size,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (glyph is not null)
            button.Content = Ui.Icon(_resources, glyph, iconSize);
        button.Click += (_, _) => press();
        return button;
    }

    private bool Do(Func<PlayingApp, System.Threading.Tasks.Task> action)
    {
        if (Shown is not { } player) return false;
        _ = RunAndRefresh(action(player));
        return true;
    }

    private async System.Threading.Tasks.Task RunAndRefresh(System.Threading.Tasks.Task command)
    {
        await command;
        await System.Threading.Tasks.Task.Delay(250); // let the app update what it reports
        if (!_active || _refreshing) return;
        _refreshing = true;
        try
        {
            await _media.RefreshAsync();
        }
        finally
        {
            _refreshing = false;
        }
        if (_active) Show();
    }

    private static void FocusButton(Button? button)
    {
        if (button is null) return;
        button.Focus();
        Keyboard.Focus(button);
    }

    private static string Time(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }
}
