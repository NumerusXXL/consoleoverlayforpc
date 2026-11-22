using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Steam;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// A friend's page, drawn by the overlay like a console profile: their picture, name and status, Steam level, how long
/// you have been friends, and the games they played lately as tiles (the game they're in fills the background).
///
/// A on a game plays it if you have it installed, otherwise opens its game page. X opens your chat with them in the
/// Steam app (with Invite to Game); Y joins their game when Steam allows it, or opens their profile in Steam. Steam
/// has no public API for chat, so the conversation itself is in Steam's chat window.
/// </summary>
internal sealed class SteamFriendPage
{
    private const double AvatarSize = 132;
    private const double TileWidth = 240, TileHeight = 112, FocusedTileWidth = 292, FocusedTileHeight = 136;

    private readonly FrameworkElement _resources;
    private readonly StackPanel _root = new();
    private readonly Border _avatar;
    private readonly TextBlock _initial = new() { FontSize = 54, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _status = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _facts = new() { FontSize = 15, Margin = new Thickness(0, 8, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 22, 0, 0) };
    private readonly TextBlock _gamesLabel;
    private readonly Shelf _games;
    private readonly TextBlock _gameInfo = new() { FontSize = 16, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _note = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 880, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };

    private SteamFriend? _friend;
    private List<RecentGame> _recent = [];
    private RecentGame? _focusedGame;
    private int? _level;
    private CancellationTokenSource _cancel = new();
    private int _token;

    public Func<string> ApiKey { get; set; } = () => "";
    public Func<int, (bool Owned, bool Installed)> Ownership { get; set; } = _ => (false, false);

    /// <summary>A steam:// link to open, and whether to open Big Picture first.</summary>
    public event Action<string, bool>? OpenLink;

    /// <summary>Open a game's page (a game they played that you haven't installed).</summary>
    public event Action<StoreItem>? OpenGame;

    public event Action<BitmapSource?>? BackdropChanged;

    /// <summary>B: back to the friends list.</summary>
    public event Action? Closed;

    public event Action? HintChanged;

    public SteamFriendPage(FrameworkElement resources)
    {
        _resources = resources;
        _avatar = new Border
        {
            Width = AvatarSize,
            Height = AvatarSize,
            CornerRadius = new CornerRadius(30),
            Background = (Brush)resources.FindResource("TileBrush"),
            Child = _initial,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _name.Style = (Style)resources.FindResource("HeroTitle");
        _name.FontSize = 52;
        _facts.Foreground = (Brush)resources.FindResource("SubtleTextBrush");
        _gameInfo.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        _note.Foreground = (Brush)resources.FindResource("SubtleTextBrush");

        var text = new StackPanel { Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_name);
        text.Children.Add(_status);
        text.Children.Add(_facts);
        var header = new DockPanel();
        DockPanel.SetDock(_avatar, Dock.Left);
        header.Children.Add(_avatar);
        header.Children.Add(text);

        _gamesLabel = Ui.Section(resources, "Played lately", first: false);
        _gamesLabel.Margin = new Thickness(2, 28, 0, 0);
        _games = new Shelf(resources, TileWidth, TileHeight, FocusedTileWidth, FocusedTileHeight);

        _root.Children.Add(header);
        _root.Children.Add(_actions);
        _root.Children.Add(_gamesLabel);
        _root.Children.Add(_games.View);
        _root.Children.Add(_gameInfo);
        _root.Children.Add(_note);
        _root.Visibility = Visibility.Collapsed;
    }

    public FrameworkElement View => _root;

    public bool IsOpen => _friend is not null;

    public string Hint => _games.Count > 0
        ? "← → games · B back to friends · LB / RB other pages"
        : "B back to friends · LB / RB other pages";

    public void Open(SteamFriend friend)
    {
        _friend = friend;
        _recent = [];
        _level = null;
        _focusedGame = null;
        _root.Visibility = Visibility.Visible;
        Resume();
    }

    /// <summary>The list refreshed: show their new status without starting over.</summary>
    public void Update(SteamFriend friend)
    {
        if (_friend?.SteamId != friend.SteamId || _friend == friend) return;
        bool gameChanged = _friend.GameId != friend.GameId;
        _friend = friend;
        BuildHeader();
        BuildActions();
        if (gameChanged) _ = BackdropForAsync(friend.GameId > 0 ? friend.GameId : _focusedGame?.AppId, _token);
    }

    public void Close()
    {
        _friend = null;
        Pause();
        _games.Clear();
        _root.Visibility = Visibility.Collapsed;
    }

    public void Pause()
    {
        _cancel.Cancel();
        _token++;
        BackdropChanged?.Invoke(null);
    }

    public void Resume()
    {
        if (_friend is not { } friend) return;
        _cancel = new CancellationTokenSource();
        int token = ++_token;
        BuildHeader();
        BuildActions();
        BuildGames();
        _ = LoadAvatarAsync(friend, token);
        _ = LoadAsync(friend, token);
        if (friend.GameId > 0) _ = BackdropForAsync(friend.GameId, token);
    }

    public void Focus()
    {
        if (_games.Count > 0) _games.Focus(_focusedGame is null ? 0 : Math.Max(0, _recent.IndexOf(_focusedGame)));
        else Keyboard.ClearFocus();
    }

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_friend is not { } friend) return false;
        switch (command)
        {
            case NavCommand.Back:
                Closed?.Invoke();
                return true;
            case NavCommand.Accept:
                if (_focusedGame is { } game && _games.Contains(focused)) PlayOrOpen(game);
                return true;
            case NavCommand.Secondary:
                OpenLink?.Invoke(SteamFriends.ClientChatLink(friend.SteamId), true);
                return true;
            case NavCommand.Mute:
                if (friend.JoinLink is { } join) OpenLink?.Invoke(join, false);
                else OpenLink?.Invoke(SteamFriends.ProfileLink(friend.SteamId), true);
                return true;
            case NavCommand.Up or NavCommand.Down or NavCommand.ColumnLeft or NavCommand.ColumnRight:
                return true;
        }
        return false;
    }

    // ---- Loading ----------------------------------------------------------------------------

    private async Task LoadAsync(SteamFriend friend, int token)
    {
        string key = ApiKey().Trim();
        if (key.Length == 0)
        {
            _note.Text = "Add your Steam Web API key in Settings › Steam to see what they play, their level and when you became friends.";
            _note.Visibility = Visibility.Visible;
            return;
        }
        var cancel = _cancel.Token;
        try
        {
            var recentTask = Task.Run(() => SteamFriends.RecentGamesAsync(key, friend.SteamId, cancel), cancel);
            var levelTask = Task.Run(() => SteamFriends.LevelAsync(key, friend.SteamId, cancel), cancel);
            List<RecentGame> recent = [];
            try { recent = await recentTask; } catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write($"Steam friends: no recent games: {ex.Message}"); }
            try { _level = await levelTask; } catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write($"Steam friends: no level: {ex.Message}"); }
            if (token != _token) return;
            _recent = recent;
            BuildHeader();
            BuildGames();
            if (_recent.Count == 0)
            {
                _note.Text = "They haven't played anything in the last two weeks, or their game details are private.";
                _note.Visibility = Visibility.Visible;
            }
            if (_root.IsVisible && _games.Count > 0) Focus();
            HintChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
    }

    private async Task LoadAvatarAsync(SteamFriend friend, int token)
    {
        if (friend.AvatarUrl is null) return;
        var image = await WebImages.LoadAsync(friend.AvatarUrl, 300, _cancel.Token);
        if (image is null || token != _token) return;
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        _avatar.Background = brush;
        _initial.Visibility = Visibility.Collapsed;
    }

    /// <summary>A screenshot of the game they're in (or the focused one) behind the page.</summary>
    private async Task BackdropForAsync(int? appId, int token)
    {
        if (appId is not > 0) return;
        try
        {
            var details = await SteamStore.DetailsAsync(appId.Value, _cancel.Token);
            var url = details?.Screenshots.FirstOrDefault().Full ?? details?.Background;
            if (url is null || token != _token) return;
            var image = await WebImages.LoadAsync(url, 1280, _cancel.Token, cache: false);
            if (image is null || token != _token) return;
            BackdropChanged?.Invoke(ScreenCapture.SoftBlur(image));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam friends: no background picture for {appId}: {ex.Message}");
        }
    }

    // ---- Building ---------------------------------------------------------------------------

    private void BuildHeader()
    {
        if (_friend is not { } f) return;
        _name.Text = f.Name;
        _initial.Text = f.Name.Length > 0 ? char.ToUpper(f.Name[0], CultureInfo.CurrentCulture).ToString() : "?";
        if (_avatar.Background is not ImageBrush) _initial.Visibility = Visibility.Visible;

        _status.Children.Clear();
        var (text, brush) = SteamFriendsView.Status(f);
        _status.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        _status.Children.Add(new TextBlock { Text = text, FontSize = 19, Foreground = (Brush)_resources.FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center });

        var facts = new List<string>();
        if (_level is { } level) facts.Add($"Steam level {level}");
        if (f.FriendSince is { } since) facts.Add("Friends since " + since.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        if (f.RealName is { Length: > 0 } real) facts.Add(real);
        if (f.Country is { Length: 2 } country)
        {
            try { facts.Add(new RegionInfo(country).DisplayName); } catch { }
        }
        _facts.Text = string.Join("  ·  ", facts);
        _facts.Visibility = facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildActions()
    {
        if (_friend is not { } f) return;
        _actions.Children.Clear();
        if (_focusedGame is { } game && _games.Count > 0)
        {
            var (_, installed) = Ownership(game.AppId);
            _actions.Children.Add(Ui.ActionPill(_resources, "A", installed ? "Play " + Short(game.Name) : "Game page", primary: true, () => PlayOrOpen(game)));
        }
        _actions.Children.Add(Ui.ActionPill(_resources, "X", "Chat and invite in Steam", primary: _games.Count == 0,
            () => OpenLink?.Invoke(SteamFriends.ClientChatLink(f.SteamId), true)));
        if (f.JoinLink is { } join)
            _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Join their game", primary: false, () => OpenLink?.Invoke(join, false)));
        else
            _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Profile in Steam", primary: false,
                () => OpenLink?.Invoke(SteamFriends.ProfileLink(f.SteamId), true)));
    }

    private void BuildGames()
    {
        int? keep = _focusedGame?.AppId;
        _games.Clear();
        _note.Visibility = Visibility.Collapsed;
        bool any = _recent.Count > 0;
        _gamesLabel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _games.View.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _gameInfo.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            _focusedGame = null;
            BuildActions();
            return;
        }

        int token = _token;
        foreach (var game in _recent)
        {
            var current = game;
            var layers = new Grid();
            layers.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = (Brush)_resources.FindResource("TileBrush"),
                Child = new TextBlock { Text = game.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12), VerticalAlignment = VerticalAlignment.Bottom },
            });
            var art = new Border { CornerRadius = new CornerRadius(18) };
            layers.Children.Add(art);
            if (Ownership(game.AppId).Installed)
                layers.Children.Add(new Border
                {
                    Background = Brushes.White,
                    CornerRadius = new CornerRadius(11),
                    Padding = new Thickness(9, 3, 9, 4),
                    Margin = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = "Installed", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)_resources.FindResource("SelectedTextBrush") },
                });
            _games.Add(layers, index =>
            {
                _focusedGame = current;
                ShowGameInfo(current);
                BuildActions();
                if (_friend?.GameId is not > 0) _ = BackdropForAsync(current.AppId, _token);
            }, () => PlayOrOpen(current));

            var cancel = _cancel.Token;
            _ = Task.Run(async () =>
            {
                var image = await WebImages.LoadAsync(StoreItem.HeaderUrl(current.AppId), 600, cancel);
                if (image is null) return;
                await _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (token != _token) return;
                    var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    brush.Freeze();
                    art.Background = brush;
                });
            });
        }
        _focusedGame = _recent.FirstOrDefault(g => g.AppId == keep) ?? _recent[0];
        ShowGameInfo(_focusedGame);
        BuildActions();
    }

    private void ShowGameInfo(RecentGame game)
    {
        var (owned, installed) = Ownership(game.AppId);
        var parts = new List<string> { game.Name, Hours(game.MinutesTwoWeeks) + " in the last two weeks" };
        if (game.MinutesTotal > game.MinutesTwoWeeks) parts.Add(Hours(game.MinutesTotal) + " in total");
        parts.Add(installed ? "Installed" : owned ? "In your library" : "Not in your library");
        _gameInfo.Text = string.Join("  ·  ", parts);
    }

    private void PlayOrOpen(RecentGame game)
    {
        if (Ownership(game.AppId).Installed) OpenLink?.Invoke(SteamLibrary.RunLink(game.AppId), false);
        else OpenGame?.Invoke(new StoreItem(game.AppId, game.Name, StoreItem.HeaderUrl(game.AppId), null, null, 0, null));
    }

    private static string Hours(int minutes) => minutes < 60
        ? $"{minutes} min"
        : $"{(minutes / 60.0).ToString(minutes < 600 ? "0.#" : "0", CultureInfo.CurrentCulture)} h";

    private static string Short(string name) => name.Length > 22 ? name[..20].TrimEnd() + "…" : name;
}
