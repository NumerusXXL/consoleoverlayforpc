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
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Steam;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Friends view on the Steam page: your Steam friends as big avatar tiles, in a game first, then online, then
/// offline. A opens their page (<see cref="SteamFriendPage"/>); X opens your chat with them in the Steam app, where
/// "Invite to Game" is; Y joins their game when Steam allows joining from outside, otherwise opens their profile.
/// </summary>
internal sealed class SteamFriendsView
{
    private const double TileSize = 150, FocusedSize = 190;

    private static readonly Brush InGameBrush = Frozen(Color.FromRgb(0x9B, 0xD8, 0x4A));
    private static readonly Brush OnlineBrush = Frozen(Color.FromRgb(0x5C, 0xD2, 0xF0));
    private static readonly Brush AwayBrush = Frozen(Color.FromRgb(0x8F, 0xB8, 0xC9));
    private static readonly Brush OfflineBrush = Frozen(Color.FromRgb(0x8A, 0x8F, 0x99));

    private readonly FrameworkElement _resources;
    private readonly StackPanel _root = new();
    private readonly Shelf _shelf;
    private readonly StackPanel _info = new() { Margin = new Thickness(0, 22, 0, 0) };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _meta = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _note = new() { FontSize = 15, TextWrapping = TextWrapping.Wrap, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
    private readonly DispatcherTimer _refresh;
    private readonly StackPanel _list = new();
    private readonly SteamFriendPage _page;

    private List<SteamFriend> _friends = [];
    private DateTime _loadedAt;
    private string? _steamFolder;
    private string? _keyUsed;
    private SteamFriend? _focused;
    private bool _active;
    private bool _loading;
    private string? _problem;
    private CancellationTokenSource _cancel = new();
    private int _artToken;

    /// <summary>The Steam Web API key from Settings › Steam (empty: no online status).</summary>
    public Func<string> ApiKey { get; set; } = () => "";

    /// <summary>Close the overlay and open a steam:// link; true: in Big Picture.</summary>
    public event Action<string, bool>? OpenLink;

    /// <summary>Open a game's page (from a friend's recent games).</summary>
    public event Action<StoreItem>? OpenGame;

    public event Action<System.Windows.Media.Imaging.BitmapSource?>? BackdropChanged;

    /// <summary>Is the game yours, and is it installed?</summary>
    public Func<int, (bool Owned, bool Installed)> Ownership { get; set; } = _ => (false, false);

    /// <summary>Open Settings › Steam (to add the Web API key).</summary>
    public event Action? OpenSettings;

    public event Action? HintChanged;

    public SteamFriendsView(FrameworkElement resources)
    {
        _resources = resources;
        _shelf = new Shelf(resources, TileSize, TileSize, FocusedSize, FocusedSize);
        _title.Style = (Style)resources.FindResource("HeroTitle");
        _note.Foreground = (Brush)resources.FindResource("SubtleTextBrush");
        _info.Children.Add(_title);
        _info.Children.Add(_meta);
        _info.Children.Add(_note);
        _info.Children.Add(_actions);
        _list.Children.Add(_shelf.View);
        _list.Children.Add(_info);
        _root.Children.Add(_list);

        _page = new SteamFriendPage(resources)
        {
            ApiKey = () => ApiKey(),
            Ownership = id => Ownership(id),
        };
        _page.OpenLink += (link, big) => OpenLink?.Invoke(link, big);
        _page.OpenGame += item => OpenGame?.Invoke(item);
        _page.BackdropChanged += picture => BackdropChanged?.Invoke(picture);
        _page.HintChanged += () => HintChanged?.Invoke();
        _page.Closed += ClosePage;
        _root.Children.Add(_page.View);

        // Who is online changes: read it again every half minute while you look at it.
        _refresh = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
        _refresh.Tick += (_, _) => _ = LoadAsync(force: true);
    }

    public FrameworkElement View => _root;

    public string Hint => _page.IsOpen ? _page.Hint : "LT / RT store or library · B back to game · LB / RB other pages";

    /// <summary>A friend's page is open (LT / RT don't switch views then).</summary>
    public bool InPage => _page.IsOpen;

    public string? Subtitle => _page.IsOpen ? "Friends" : _friends.Count == 0 ? "Friends"
        : HasKey ? $"Friends · {_friends.Count(f => f.IsOnline)} online" : $"Friends · {_friends.Count}";

    private bool HasKey => !string.IsNullOrWhiteSpace(ApiKey());

    public void Activate(string? steamFolder)
    {
        _active = true;
        _steamFolder = steamFolder;
        _cancel = new CancellationTokenSource();
        if (_page.IsOpen)
        {
            _page.Resume();
            _page.Focus();
        }
        else if (_friends.Count == 0) ShowMessage("Loading your friends…", null, null);
        else Rebuild(keepFocus: false);
        _ = LoadAsync(force: false);
        _refresh.Start();
    }

    /// <summary>Puts focus back where it was (coming back from a game page).</summary>
    public void FocusCurrent()
    {
        if (_page.IsOpen) _page.Focus();
        else if (_focused is { } f) _shelf.Focus(Math.Max(0, _friends.IndexOf(f)));
        else if (_shelf.Count > 0) _shelf.Focus(0);
    }

    public void Deactivate()
    {
        _active = false;
        _refresh.Stop();
        _cancel.Cancel();
        _page.Pause();
        _artToken++;
        foreach (var tile in _shelf.Tiles)
            if (tile.Content is Grid layers && layers.Children.Count > 1 && layers.Children[1] is Border art)
            {
                art.Background = null;
                art.Tag = null;
            }
    }

    public UIElement? FirstFocus() => _page.IsOpen ? null : _shelf.Count > 0 ? _shelf.Tiles[0] : null;

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_page.IsOpen) return _page.Handle(command, focused);
        var friend = _focused;
        switch (command)
        {
            case NavCommand.Up or NavCommand.Down:
                return true; // one row
            case NavCommand.Accept when _shelf.Count == 0:
                if (_steamFolder is not null && !HasKey) OpenSettings?.Invoke();
                return true;
            case NavCommand.Secondary:
                if (friend is not null) OpenLink?.Invoke(SteamFriends.ClientChatLink(friend.SteamId), true);
                return true;
            case NavCommand.Mute:
                if (!HasKey) OpenSettings?.Invoke();
                else if (friend?.JoinLink is { } join) OpenLink?.Invoke(join, false);
                else if (friend is not null) OpenLink?.Invoke(SteamFriends.ProfileLink(friend.SteamId), true);
                return true;
        }
        return false;
    }

    // ---- Loading ----------------------------------------------------------------------------

    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        if (_steamFolder is null)
        {
            ShowMessage("Steam isn't installed", "Install Steam and sign in, and your friends show up here.", null);
            return;
        }
        string key = ApiKey().Trim();
        if (!force && _friends.Count > 0 && key == _keyUsed && DateTime.Now - _loadedAt < TimeSpan.FromSeconds(30)) return;

        var steamId = SteamFriends.CurrentSteamId(_steamFolder);
        if (steamId is null)
        {
            ShowMessage("Sign in to Steam first", "Your friends show up here once you have signed in to the Steam app on this PC.", null);
            return;
        }

        _loading = true;
        var cancel = _cancel.Token;
        try
        {
            List<SteamFriend> friends;
            _problem = null;
            try
            {
                friends = await Task.Run(() => SteamFriends.LoadAsync(_steamFolder, steamId, key, cancel), cancel);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && key.Length > 0)
            {
                // Bad key or no network: still show the friends Steam keeps on this PC.
                Log.Write($"Steam friends: the Web API didn't answer: {ex.Message}");
                _problem = ex is UnauthorizedAccessException
                    ? "Steam didn't accept your Web API key, so online status is missing. Check it in Settings › Steam."
                    : "Couldn't reach Steam to see who is online. Showing the friends saved on this PC.";
                friends = await Task.Run(() => SteamFriends.LoadAsync(_steamFolder, steamId, null, cancel), cancel);
            }
            if (cancel.IsCancellationRequested || !_active) return;

            bool changed = key != _keyUsed || !friends.SequenceEqual(_friends);
            _friends = friends;
            _keyUsed = key;
            _loadedAt = DateTime.Now;
            HintChanged?.Invoke();
            if (_page.IsOpen)
            {
                if (friends.FirstOrDefault(f => f.SteamId == _focused?.SteamId) is { } fresh) _page.Update(fresh);
            }
            else if (changed) Rebuild(keepFocus: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam friends: could not load: {ex.Message}");
            if (_active && _friends.Count == 0) ShowMessage("Your friends didn't load", "The log has the reason.", null);
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Building ---------------------------------------------------------------------------

    private void Rebuild(bool keepFocus)
    {
        // Same friend keeps focus when the list refreshes, or when you come back from a chat.
        string? focusedId = _focused?.SteamId;
        bool hadFocus = keepFocus && _shelf.Contains(Keyboard.FocusedElement);
        _shelf.Clear();
        _artToken++;

        if (_friends.Count == 0)
        {
            ShowMessage("No friends to show",
                HasKey ? "Your friends list is empty, or private to the Web API."
                       : "Steam hasn't saved any friends on this PC yet. Add your Steam Web API key to load your full friends list.",
                HasKey ? null : "Add a Web API key");
            return;
        }

        _shelf.View.Visibility = Visibility.Visible;
        foreach (var friend in _friends)
        {
            var current = friend;
            _shelf.Add(MakeTile(friend), index =>
            {
                _focused = current;
                ShowInfo(current);
                RequestArt(index);
            }, () => OpenPage(current));
        }
        RequestArt(0);

        int focusIndex = focusedId is null ? 0 : Math.Max(0, _friends.FindIndex(f => f.SteamId == focusedId));
        _focused = _friends[focusIndex];
        ShowInfo(_focused);
        if (!hadFocus) _shelf.Rise();
        if (_active) _shelf.Focus(focusIndex);
    }

    private Grid MakeTile(SteamFriend friend)
    {
        var layers = new Grid();
        layers.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = (Brush)_resources.FindResource("TileBrush"),
            Child = new TextBlock
            {
                Text = friend.Name.Length > 0 ? char.ToUpper(friend.Name[0], CultureInfo.CurrentCulture).ToString() : "?",
                FontSize = 56,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(22) }); // the avatar, once downloaded
        if (!friend.IsOnline) layers.Opacity = friend.State == FriendState.Unknown ? 1 : 0.62;

        if (friend.State != FriendState.Unknown)
        {
            layers.Children.Add(new Border
            {
                Background = friend.InGame ? InGameBrush : friend.IsOnline ? (friend.State is FriendState.Away or FriendState.Snooze ? AwayBrush : OnlineBrush) : OfflineBrush,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 4, 10, 5),
                Margin = new Thickness(10),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = friend.InGame ? "In game" : StateName(friend.State),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = (Brush)_resources.FindResource("SelectedTextBrush"),
                },
            });
        }
        return layers;
    }

    private void ShowInfo(SteamFriend friend)
    {
        _info.Visibility = Visibility.Visible;
        _title.Text = friend.Name;

        _meta.Children.Clear();
        var (status, dot) = Status(friend);
        _meta.Children.Add(new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = dot, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        _meta.Children.Add(new TextBlock { Text = status, FontSize = 17, Foreground = (Brush)_resources.FindResource("SoftTextBrush"), VerticalAlignment = VerticalAlignment.Center });

        _note.Text = _problem ?? (HasKey ? "" : "Who is online and what they play shows once you add your Steam Web API key (Y).");
        _note.Visibility = _note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _actions.Children.Clear();
        _actions.Children.Add(Ui.ActionPill(_resources, "A", "View", primary: true, () => OpenPage(friend)));
        _actions.Children.Add(Ui.ActionPill(_resources, "X", "Chat and invite in Steam", primary: false,
            () => OpenLink?.Invoke(SteamFriends.ClientChatLink(friend.SteamId), true)));
        if (!HasKey)
            _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Show who's online", primary: false, () => OpenSettings?.Invoke()));
        else if (friend.JoinLink is { } join)
            _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Join their game", primary: false, () => OpenLink?.Invoke(join, false)));
        else
            _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Profile in Steam", primary: false,
                () => OpenLink?.Invoke(SteamFriends.ProfileLink(friend.SteamId), true)));
    }

    /// <summary>"Playing Portal 2", "Online", "Offline · last online 3 days ago", with its color.</summary>
    public static (string Text, Brush Dot) Status(SteamFriend friend)
    {
        if (friend.State == FriendState.Unknown) return ("Steam friend", OfflineBrush);
        if (friend.InGame) return ("Playing " + friend.Game, InGameBrush);
        if (friend.IsOnline) return (StateName(friend.State), friend.State is FriendState.Away or FriendState.Snooze ? AwayBrush : OnlineBrush);
        return (friend.LastOnline is { } last ? "Offline · last online " + Ago(last) : "Offline", OfflineBrush);
    }

    private void OpenPage(SteamFriend friend)
    {
        _list.Visibility = Visibility.Collapsed;
        _page.Open(friend);
        _page.Focus();
        HintChanged?.Invoke();
    }

    private void ClosePage()
    {
        _page.Close();
        _list.Visibility = Visibility.Visible;
        FocusCurrent();
        HintChanged?.Invoke();
    }

    private void ShowMessage(string title, string? detail, string? settingsAction)
    {
        _shelf.Clear();
        _focused = null;
        _shelf.View.Visibility = Visibility.Hidden;
        _title.Text = title;
        _meta.Children.Clear();
        _note.Text = detail ?? "";
        _note.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        _actions.Children.Clear();
        if (settingsAction is not null)
            _actions.Children.Add(Ui.ActionPill(_resources, "A", settingsAction, primary: true, () => OpenSettings?.Invoke()));
    }

    // ---- Avatars ----------------------------------------------------------------------------

    private void RequestArt(int around)
    {
        int token = _artToken;
        double pixelsPerUnit = VisualTreeHelper.GetDpi(_root).DpiScaleX * 2;
        int width = (int)Math.Clamp(FocusedSize * pixelsPerUnit, 184, 400);
        for (int i = Math.Max(0, around - 2); i <= Math.Min(_friends.Count - 1, around + 8); i++)
        {
            if (_friends[i].AvatarUrl is not { } url) continue;
            var tile = _shelf.Tiles[i];
            if (tile.Content is not Grid layers || layers.Children[1] is not Border art || art.Tag is int t && t == token) continue;
            art.Tag = token;
            var cancel = _cancel.Token;
            _ = Task.Run(async () =>
            {
                var image = await WebImages.LoadAsync(url, width, cancel);
                if (image is null) return;
                await _root.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (token != _artToken) return;
                    var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    brush.Freeze();
                    art.Background = brush;
                });
            });
        }
    }

    // ---- Text -------------------------------------------------------------------------------

    private static string StateName(FriendState state) => state switch
    {
        FriendState.Online => "Online",
        FriendState.Busy => "Busy",
        FriendState.Away => "Away",
        FriendState.Snooze => "Snooze",
        FriendState.LookingToTrade => "Looking to trade",
        FriendState.LookingToPlay => "Looking to play",
        _ => "Offline",
    };

    private static string Ago(DateTime when)
    {
        var age = DateTime.Now - when;
        if (age.TotalHours < 1) return "just now";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} h ago";
        if (age.TotalDays < 2) return "yesterday";
        if (age.TotalDays < 30) return $"{(int)age.TotalDays} days ago";
        return when.ToString("d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
