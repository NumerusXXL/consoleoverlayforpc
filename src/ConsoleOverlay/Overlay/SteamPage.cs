using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Steam;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Steam page: your installed Steam games as covers, read from Steam's own files on disk.
/// A plays the focused game (or goes back to it when it is the one running), X opens its page in Steam,
/// Y opens Steam friends, LT / RT switch between Recent and Installed.
/// </summary>
internal sealed class SteamPage : IOverlayPage
{
    private const double CoverWidth = 140, CoverHeight = 210;
    private const double FocusedWidth = 180, FocusedHeight = 270;
    private const double CoverGap = 18; // CoverButton's right margin
    private const int RecentCount = 30;

    private enum View { Recent, Installed }

    private sealed class Cover
    {
        public required SteamGame Game { get; init; }
        public required Button Button { get; init; }
        public required Border Art { get; init; }
        public bool ArtRequested { get; set; }
    }

    private readonly FrameworkElement _resources;
    private readonly Grid _root = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _strip = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        CanContentScroll = false,
    };
    private readonly StackPanel _covers = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _meta = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
    private readonly StackPanel _info = new() { Margin = new Thickness(0, 22, 0, 0) };
    private readonly List<Cover> _shown = [];

    private List<SteamGame> _games = [];
    private string? _steamFolder;
    private bool _loaded;
    private bool _active;
    private int _running;
    private View _view = View.Recent;
    private int _loadToken;
    private int _artToken; // changes when the covers on screen are replaced, so late decodes are dropped

    /// <summary>Close the overlay and give focus back to the game.</summary>
    public event Action? BackToGame;

    /// <summary>Close the overlay (without going back to the game), then run this.</summary>
    public event Action<Action>? CloseThen;

    public event Action? HintChanged;

    public SteamPage(FrameworkElement resources)
    {
        _resources = resources;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FocusedHeight + 24) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_tabs, 0);
        _tabs.Margin = new Thickness(0, 0, 0, 6);
        _root.Children.Add(_tabs);

        // Padding so the focus ring (drawn just outside the cover) isn't cut off by the scrolling strip.
        _covers.Margin = new Thickness(10, 10, 10, 12);
        _strip.Content = _covers;
        _strip.Margin = new Thickness(-10, 0, -10, 0);
        KeyboardNavigation.SetDirectionalNavigation(_covers, KeyboardNavigationMode.Contained);
        Grid.SetRow(_strip, 1);
        _root.Children.Add(_strip);

        _title.Style = (Style)resources.FindResource("HeroTitle");
        _meta.FontSize = 16;
        _meta.Margin = new Thickness(0, 8, 0, 0);
        _meta.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        _info.Children.Add(_title);
        _info.Children.Add(_meta);
        _info.Children.Add(_actions);
        Grid.SetRow(_info, 2);
        _root.Children.Add(_info);

        BuildTabs();
    }

    public Panel Content => _root;

    public string Hint => "LT / RT recent or installed · B back to game · LB / RB other pages";

    public string? Subtitle => _steamFolder is null ? null
        : _loaded ? $"Library · {_games.Count} installed" : "Library";

    // ---- IOverlayPage -----------------------------------------------------------------------

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        _running = SteamLibrary.RunningAppId();
        if (!_loaded)
            ShowMessage("Loading your library…", null);
        else
            Rebuild(keepFocus: false);
        _ = LoadAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _loadToken++;
        _artToken++;
        // Covers are decoded again next time (from Windows' file cache, so it's quick); this keeps memory low
        // while you play.
        foreach (var cover in _shown)
        {
            cover.Art.Background = null;
            cover.ArtRequested = false;
        }
    }

    public UIElement? FirstFocus() => _shown.Count > 0 ? _shown[0].Button : null;

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        switch (command)
        {
            case NavCommand.ColumnLeft:
            case NavCommand.ColumnRight:
                SwitchView(_view == View.Recent ? View.Installed : View.Recent);
                return true;
            case NavCommand.Secondary:
                if (Focused(focused) is { } page) OpenLink(SteamLibrary.GamePageLink(page.Game.AppId));
                return true;
            case NavCommand.Mute:
                if (_steamFolder is not null) OpenLink(SteamLibrary.FriendsLink);
                return true;
            case NavCommand.Up or NavCommand.Down:
                return true; // one row: nothing above or below
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused)
    {
        if (key != Key.Tab) return false;
        SwitchView(_view == View.Recent ? View.Installed : View.Recent);
        return true;
    }

    // ---- Loading ----------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        int token = ++_loadToken;
        var timer = Stopwatch.StartNew();
        var (folder, games) = await Task.Run(() =>
        {
            var steam = SteamLibrary.SteamFolder();
            return (steam, steam is null ? [] : SteamLibrary.InstalledGames(steam));
        });
        if (token != _loadToken || !_active) return;

        bool changed = !_loaded || folder != _steamFolder
            || !games.Select(g => (g.AppId, g.LastPlayed)).SequenceEqual(_games.Select(g => (g.AppId, g.LastPlayed)));
        _steamFolder = folder;
        _games = games;
        if (!_loaded && folder is not null)
            Log.Write($"Steam: {games.Count} games installed, {games.Count(g => g.CoverPath is not null)} with a cover, "
                + $"{games.Count(g => g.CoverPath is null && g.HeaderPath is not null)} with only a header picture.");
        _loaded = true;
        if (timer.ElapsedMilliseconds > 200)
            Log.Write($"Reading the Steam library took {timer.ElapsedMilliseconds} ms ({games.Count} games).");

        HintChanged?.Invoke(); // the subtitle (game count) changed
        if (changed) Rebuild(keepFocus: false);
    }

    private IEnumerable<SteamGame> GamesInView() => _view switch
    {
        View.Recent => _games
            .Where(g => g.LastPlayed is not null || g.AppId == _running)
            .OrderByDescending(g => g.AppId == _running)
            .ThenByDescending(g => g.LastPlayed)
            .Take(RecentCount),
        _ => _games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
    };

    // ---- Building ---------------------------------------------------------------------------

    private void BuildTabs()
    {
        _tabs.Children.Clear();
        _tabs.Children.Add(Ui.Bumper("LT", new Thickness(0, 0, 8, 0)));
        foreach (var view in new[] { View.Recent, View.Installed })
        {
            bool selected = view == _view;
            var tab = new Button
            {
                Style = (Style)_resources.FindResource("TabPill"),
                Content = view == View.Recent ? "Recent" : "Installed",
                Tag = selected ? "selected" : null,
            };
            var target = view;
            tab.Click += (_, _) => SwitchView(target);
            _tabs.Children.Add(tab);
        }
        _tabs.Children.Add(Ui.Bumper("RT", new Thickness(8, 0, 0, 0)));
    }

    private void SwitchView(View view)
    {
        if (view == _view) return;
        _view = view;
        BuildTabs();
        Rebuild(keepFocus: false);
    }

    private void Rebuild(bool keepFocus)
    {
        _covers.Children.Clear();
        _shown.Clear();
        _artToken++;
        _strip.ScrollToHorizontalOffset(0);

        if (_steamFolder is null)
        {
            if (_loaded) ShowMessage("Steam isn't installed", "Install Steam and sign in, and your games show up here.");
            return;
        }

        var games = GamesInView().ToList();
        if (games.Count == 0)
        {
            ShowMessage(_view == View.Recent ? "Nothing played yet" : "No games installed",
                _view == View.Recent ? "Press RT to see everything you have installed." : "Games you install in Steam show up here.");
            return;
        }

        _strip.Visibility = Visibility.Visible;
        foreach (var game in games)
            _shown.Add(MakeCover(game));
        foreach (var cover in _shown)
            _covers.Children.Add(cover.Button);

        Ui.Stagger(_shown.Take(8).Select(c => (FrameworkElement)c.Button), 30);
        RequestArt(0);
        ShowInfo(_shown[0]);

        if (_active)
        {
            _shown[0].Button.Focus();
            Keyboard.Focus(_shown[0].Button);
        }
    }

    private void ShowMessage(string title, string? detail)
    {
        _covers.Children.Clear();
        _shown.Clear();
        _strip.Visibility = Visibility.Hidden;
        _title.Text = title;
        _meta.Text = detail ?? "";
        _actions.Children.Clear();
    }

    private Cover MakeCover(SteamGame game)
    {
        var art = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = null,
        };
        // Shown until the cover art is decoded, and for games Steam has no cover cached for.
        var fallback = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = FallbackBrush(game.AppId),
            Child = new TextBlock
            {
                Text = game.Name,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(14),
            },
        };
        var layers = new Grid();
        layers.Children.Add(fallback);
        layers.Children.Add(art);
        if (game.AppId == _running)
        {
            layers.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "Playing", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)_resources.FindResource("SelectedTextBrush") },
            });
        }

        var button = new Button
        {
            Style = (Style)_resources.FindResource("CoverButton"),
            Content = layers,
            Width = CoverWidth,
            Height = CoverHeight,
            VerticalAlignment = VerticalAlignment.Bottom, // no tooltip: the name is shown big right below
        };
        var cover = new Cover { Game = game, Button = button, Art = art };
        button.Click += (_, _) => Primary(cover);
        button.GotKeyboardFocus += (_, _) =>
        {
            int index = _shown.IndexOf(cover);
            // Keep the focused cover near the left with one cover before it, like a console shelf. Every cover
            // before it is the small size, so its position is known before the grow animation runs.
            _strip.ScrollToHorizontalOffset(Math.Max(0, (index - 1) * (CoverWidth + CoverGap)));
            Grow(button, true);
            ShowInfo(cover);
            RequestArt(index);
        };
        button.LostKeyboardFocus += (_, _) => Grow(button, false);
        return cover;
    }

    /// <summary>The focused cover grows, the way a console shelf does. Only one row, so the re-layout is cheap.</summary>
    private static void Grow(Button button, bool focused)
    {
        var duration = new Duration(TimeSpan.FromMilliseconds(focused ? 260 : 200));
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        button.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(focused ? FocusedWidth : CoverWidth, duration) { EasingFunction = ease });
        button.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(focused ? FocusedHeight : CoverHeight, duration) { EasingFunction = ease });
    }

    private void ShowInfo(Cover cover)
    {
        var game = cover.Game;
        bool running = game.AppId == _running;
        _title.Text = game.Name;

        var parts = new List<string>();
        if (running) parts.Add("Playing now");
        else if (game.LastPlayed is { } last) parts.Add("Last played " + Ago(last));
        else parts.Add("Not played yet");
        if (game.SizeOnDisk > 0) parts.Add(Size(game.SizeOnDisk));
        _meta.Text = string.Join(" · ", parts);

        _actions.Children.Clear();
        _actions.Children.Add(Ui.ActionPill(_resources, "A", running ? "Back to game" : "Play", primary: true, () => Primary(cover)));
        _actions.Children.Add(Ui.ActionPill(_resources, "X", "Game page", primary: false, () => OpenLink(SteamLibrary.GamePageLink(game.AppId))));
        _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Steam friends", primary: false, () => OpenLink(SteamLibrary.FriendsLink)));
    }

    private Cover? Focused(IInputElement? focused) => _shown.FirstOrDefault(c => c.Button == focused);

    // ---- Actions ----------------------------------------------------------------------------

    private void Primary(Cover cover)
    {
        if (cover.Game.AppId == _running)
            BackToGame?.Invoke();
        else
            OpenLink(SteamLibrary.RunLink(cover.Game.AppId));
    }

    private void OpenLink(string link) => CloseThen?.Invoke(() =>
    {
        try
        {
            Log.Write($"Steam: opening {link}.");
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: could not open {link}: {ex.Message}");
        }
    });

    // ---- Cover art --------------------------------------------------------------------------

    /// <summary>Decodes the covers around the focused one, off the UI thread, at the size they are drawn.</summary>
    private void RequestArt(int around)
    {
        if (around < 0) return;
        int first = Math.Max(0, around - 4), last = Math.Min(_shown.Count - 1, around + 10);
        var wanted = new List<Cover>();
        for (int i = first; i <= last; i++)
        {
            if (_shown[i].ArtRequested) continue;
            _shown[i].ArtRequested = true;
            wanted.Add(_shown[i]);
        }
        if (wanted.Count == 0) return;

        // Its own token: reading the library again (which happens on every visit) must not throw away covers.
        int token = _artToken;
        double pixelsPerUnit = VisualTreeHelper.GetDpi(_root).DpiScaleX * 2; // the UI is scaled up for TVs
        int decodeWidth = (int)Math.Clamp(FocusedWidth * pixelsPerUnit, 180, 600);
        var dispatcher = _root.Dispatcher;
        Task.Run(() =>
        {
            foreach (var cover in wanted)
            {
                var path = cover.Game.CoverPath ?? cover.Game.HeaderPath;
                if (path is null) continue;
                var image = Decode(path, decodeWidth);
                if (image is null) continue;
                dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (token != _artToken) return;
                    var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    brush.Freeze();
                    cover.Art.Background = brush;
                });
            }
        });
    }

    private static BitmapSource? Decode(string path, int width)
    {
        try
        {
            // Read through a stream rather than a file URI, which breaks on paths with characters like '#'.
            using var file = new System.IO.MemoryStream(System.IO.File.ReadAllBytes(path));
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = file;
            image.DecodePixelWidth = width;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: could not read cover {path}: {ex.Message}");
            return null;
        }
    }

    // ---- Text -------------------------------------------------------------------------------

    private static Brush FallbackBrush(int appId)
    {
        // A steady color per game, so placeholder tiles don't all look the same.
        Color[][] pairs =
        [
            [Color.FromRgb(0xFF, 0xB3, 0x47), Color.FromRgb(0xA8, 0x32, 0x1C)],
            [Color.FromRgb(0x5E, 0xE3, 0xFF), Color.FromRgb(0x1B, 0x4C, 0x8F)],
            [Color.FromRgb(0xB1, 0x8C, 0xFF), Color.FromRgb(0x3A, 0x1F, 0x7A)],
            [Color.FromRgb(0x7C, 0xF2, 0x9A), Color.FromRgb(0x16, 0x60, 0x3A)],
            [Color.FromRgb(0xFF, 0x7A, 0xA8), Color.FromRgb(0x7A, 0x18, 0x40)],
            [Color.FromRgb(0xC9, 0xD2, 0xE3), Color.FromRgb(0x3B, 0x45, 0x58)],
        ];
        var pair = pairs[Math.Abs(appId) % pairs.Length];
        var brush = new LinearGradientBrush(pair[0], pair[1], new Point(0.2, 0), new Point(0.8, 1));
        brush.Freeze();
        return brush;
    }

    private static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;
        if (age.TotalMinutes < 60) return "just now";
        if (when.Date == DateTime.Today) return "today";
        if (when.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        if (age.TotalDays < 7) return $"{(int)age.TotalDays} days ago";
        return when.ToString("d MMMM", CultureInfo.CurrentCulture);
    }

    private static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} GB"
        : $"{bytes / (double)(1L << 20):0} MB";
}
