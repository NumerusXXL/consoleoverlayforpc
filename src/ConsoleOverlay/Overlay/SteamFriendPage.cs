using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;
using ConsoleOverlay.Steam;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// A friend's page, drawn by the overlay like a console profile: their picture, name and status, Steam level, how long
/// you have been friends, and the games they played lately as tiles (the game they're in fills the background).
///
/// A on a game plays it if you have it installed, otherwise opens its game page. X opens your chat with them on this
/// page: the Steam app's own chat window is placed in a frame under their name (<see cref="SteamChatWindow"/>), and the
/// controller drives it with a pointer, the overlay's EN/RU keyboard and scrolling. Invite to Game is in that window's
/// menu. Y joins their game when Steam allows it, or opens their profile in Steam.
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
    private readonly StackPanel _text;

    // Chat: Steam's own chat window sits over _chatFrame; the overlay's keyboard and the line you type go under it.
    private readonly StackPanel _chatPanel = new() { Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Border _chatFrame = new() { CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1D, 0x25)) };
    private readonly TextBlock _chatMessage = new() { FontSize = 20, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
    private readonly Border _draftBox = new() { CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 10, 20, 11), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _draft = new() { FontSize = 20, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly OnScreenKeyboard _keyboard;
    private readonly StringBuilder _typed = new();
    private SteamChatWindow? _chat;
    private CancellationTokenSource? _chatCancel;
    private bool _chatOpen;
    private bool _chatFailed;
    private bool _typing;
    private NavCommand? _lastMove;
    private long _lastMoveAt;
    private int _moveRun;

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

        var text = _text = new StackPanel { Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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

        _chatMessage.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        _chatFrame.Child = _chatMessage;
        _chatFrame.SizeChanged += (_, _) => PlaceChat();
        _draftBox.Background = (Brush)resources.FindResource("TileBrush");
        _draft.Foreground = (Brush)resources.FindResource("TextBrush");
        _draftBox.Child = _draft;
        _keyboard = new OnScreenKeyboard(resources) { EnterLabel = "Send" };
        _keyboard.View.Margin = new Thickness(0, 12, 0, 0);
        _keyboard.View.Visibility = Visibility.Collapsed;
        _keyboard.Typed += t => { _typed.Append(t); ShowDraft(); };
        _keyboard.Backspace += () => { if (_typed.Length > 0) _typed.Length--; ShowDraft(); };
        _keyboard.Enter += SendTyped;
        _keyboard.Closed += () => ShowKeyboard(false);
        _chatPanel.Children.Add(_chatFrame);
        _chatPanel.Children.Add(_draftBox);
        _chatPanel.Children.Add(_keyboard.View);
        _root.Children.Add(_chatPanel);
        _root.Visibility = Visibility.Collapsed;
        _root.SizeChanged += (_, _) => FitChat();
    }

    public FrameworkElement View => _root;

    public bool IsOpen => _friend is not null;

    /// <summary>Your chat with them is open on this page.</summary>
    public bool ChatOpen => _chatOpen;

    public string Hint => _chatOpen
        ? _typing ? _keyboard.Hint
            : _chatFailed ? "X chat in Steam · B back"
            : _chat is null ? "B back"
            : "D-pad pointer · A click · X right-click · Y keyboard · LT / RT scroll · B back"
        : _games.Count > 0
            ? "← → games · X chat · B back to friends · LB / RB other pages"
            : "X chat · B back to friends · LB / RB other pages";

    public void Open(SteamFriend friend, bool chat = false)
    {
        if (_chatOpen) CloseChat(focus: false);
        _friend = friend;
        _recent = [];
        _level = null;
        _focusedGame = null;
        _root.Visibility = Visibility.Visible;
        Resume();
        if (chat) OpenChat();
    }

    /// <summary>The list refreshed: show their new status without starting over.</summary>
    public void Update(SteamFriend friend)
    {
        if (_friend?.SteamId != friend.SteamId || _friend == friend) return;
        bool gameChanged = _friend.GameId != friend.GameId;
        _friend = friend;
        BuildHeader();
        BuildActions();
        if (_chatOpen) Compact(true);
        if (gameChanged) _ = BackdropForAsync(friend.GameId > 0 ? friend.GameId : _focusedGame?.AppId, _token);
    }

    public void Close()
    {
        if (_chatOpen) CloseChat(focus: false);
        _friend = null;
        Pause();
        _games.Clear();
        _root.Visibility = Visibility.Collapsed;
    }

    public void Pause()
    {
        if (_chatOpen) CloseChat(focus: false); // another page, or the overlay closed: Steam gets its window back
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
        if (_chatOpen)
        {
            if (_typing) _keyboard.FocusFirst();
            else Keyboard.ClearFocus();
        }
        else if (_games.Count > 0) _games.Focus(_focusedGame is null ? 0 : Math.Max(0, _recent.IndexOf(_focusedGame)));
        else Keyboard.ClearFocus();
    }

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_friend is not { } friend) return false;
        if (_chatOpen) return HandleChat(command, focused, friend);
        switch (command)
        {
            case NavCommand.Back:
                Closed?.Invoke();
                return true;
            case NavCommand.Accept:
                if (_focusedGame is { } game && _games.Contains(focused)) PlayOrOpen(game);
                return true;
            case NavCommand.Secondary:
                OpenChat();
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

    // ---- Chat -------------------------------------------------------------------------------

    /// <summary>Opens your chat with them in the Steam app and places its window in the frame on this page.</summary>
    private void OpenChat()
    {
        if (_friend is not { } friend) return;
        _chatOpen = true;
        _chatFailed = false;
        _typed.Clear();
        ShowDraft();
        Compact(true);
        _chatPanel.Visibility = Visibility.Visible;
        _chatMessage.Text = "Opening your chat with " + friend.Name + " in Steam…";
        _chatMessage.Visibility = Visibility.Visible;
        Keyboard.ClearFocus();
        FitChat();
        HintChanged?.Invoke();
        _ = AttachChatAsync(friend);
    }

    private async Task AttachChatAsync(SteamFriend friend)
    {
        _chatCancel?.Cancel();
        var cancel = _chatCancel = new CancellationTokenSource();
        SteamChatWindow? chat = null;
        try { chat = await SteamChatWindow.OpenAsync(friend.SteamId, friend.Name, cancel.Token); }
        catch (OperationCanceledException) { return; }
        if (cancel.IsCancellationRequested || !_chatOpen || _friend?.SteamId != friend.SteamId)
        {
            chat?.Release();
            return;
        }
        if (chat is null)
        {
            _chatFailed = true;
            _chatMessage.Text = "Steam didn't open a chat window. Is the Steam app running and signed in? "
                + "Press X to open the chat in Steam instead.";
            HintChanged?.Invoke();
            return;
        }
        _chat = chat;
        _chatMessage.Visibility = Visibility.Collapsed;
        FitChat();
        await _root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { });
        if (ScreenRect(_chatFrame) is { } rect) chat.Attach(rect);
        HintChanged?.Invoke();
    }

    private void CloseChat(bool focus = true)
    {
        _chatCancel?.Cancel();
        _chatOpen = false;
        _chatFailed = false;
        _typing = false;
        _keyboard.View.Visibility = Visibility.Collapsed;
        _draftBox.Visibility = Visibility.Collapsed;
        var chat = _chat;
        _chat = null;
        if (chat is not null)
        {
            chat.Release();
            FocusOverlay();
        }
        _chatPanel.Visibility = Visibility.Collapsed;
        Compact(false);
        if (focus) Focus();
        HintChanged?.Invoke();
    }

    private bool HandleChat(NavCommand command, IInputElement? focused, SteamFriend friend)
    {
        if (_chat is { Alive: false })
        {
            // Closed in Steam (or Steam restarted): back to their page.
            CloseChat();
            return true;
        }
        if (_typing)
        {
            if (!_keyboard.Contains(focused))
            {
                FocusOverlay();
                _chat?.Raise();
                _keyboard.FocusFirst();
                if (command is NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right or NavCommand.Accept)
                    return true;
            }
            if (_keyboard.Handle(command)) return true; // X delete, Y space, LT shift, RT send, B close
            // D-pad and A move between and press keys the overlay's usual way; LB / RB change pages.
            return command is not (NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right
                or NavCommand.Accept or NavCommand.PreviousTab or NavCommand.NextTab);
        }

        switch (command)
        {
            case NavCommand.Back:
                CloseChat();
                return true;
            case NavCommand.Secondary when _chat is null:
                if (_chatFailed) OpenLink?.Invoke(SteamFriends.ClientChatLink(friend.SteamId), true);
                return true;
            case NavCommand.PreviousTab or NavCommand.NextTab:
                return false; // other pages; Pause gives the window back
        }
        if (_chat is not { } chat) return true;
        switch (command)
        {
            case NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right:
                MovePointer(command, chat);
                return true;
            case NavCommand.Accept:
                chat.Focus();
                SyntheticInput.Click();
                return true;
            case NavCommand.Secondary:
                chat.Focus();
                SyntheticInput.Click(right: true);
                return true;
            case NavCommand.ColumnLeft or NavCommand.ColumnRight:
                PointerInside(chat);
                chat.Focus();
                SyntheticInput.Wheel(command == NavCommand.ColumnLeft ? 3 : -3);
                return true;
            case NavCommand.Mute:
                ShowKeyboard(true);
                return true;
        }
        return true;
    }

    /// <summary>The D-pad moves the mouse pointer over the chat, faster the longer you hold it.</summary>
    private void MovePointer(NavCommand direction, SteamChatWindow chat)
    {
        long now = Environment.TickCount64;
        _moveRun = _lastMove == direction && now - _lastMoveAt < 250 ? _moveRun + 1 : 0;
        _lastMove = direction;
        _lastMoveAt = now;
        var bounds = chat.Bounds;
        double scale = Math.Max(1, bounds.Height / 700.0);
        int step = (int)(Math.Min(18 + _moveRun * 14, 110) * scale);
        var at = PointerInside(chat);
        int x = at.X + direction switch { NavCommand.Left => -step, NavCommand.Right => step, _ => 0 };
        int y = at.Y + direction switch { NavCommand.Up => -step, NavCommand.Down => step, _ => 0 };
        Native.SetCursorPos(Math.Clamp(x, bounds.Left + 2, bounds.Right - 3), Math.Clamp(y, bounds.Top + 2, bounds.Bottom - 3));
        chat.Focus();
    }

    /// <summary>Where the pointer is; first moved to the middle of the chat if it is somewhere else.</summary>
    private static Native.POINT PointerInside(SteamChatWindow chat)
    {
        var b = chat.Bounds;
        if (Native.GetCursorPos(out var p) && p.X >= b.Left && p.X < b.Right && p.Y >= b.Top && p.Y < b.Bottom) return p;
        var middle = chat.Middle;
        Native.SetCursorPos(middle.X, middle.Y);
        return middle;
    }

    /// <summary>
    /// The overlay's keyboard opens under the chat window: you type the message here, RT sends it into Steam's message
    /// box (as typing would) and presses Enter.
    /// </summary>
    private void ShowKeyboard(bool show)
    {
        if (_typing == show) return;
        _typing = show;
        _keyboard.View.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ShowDraft();
        FitChat();
        if (show)
        {
            FocusOverlay();
            _chat?.Raise();
            _keyboard.FocusFirst();
        }
        else
        {
            Keyboard.ClearFocus();
            _chat?.Focus();
        }
        _root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PlaceChat);
        HintChanged?.Invoke();
    }

    private void ShowDraft()
    {
        bool shown = _typing || _typed.Length > 0;
        _draftBox.Visibility = shown && _chatOpen ? Visibility.Visible : Visibility.Collapsed;
        _draft.Text = _typed.Length > 0 ? _typed + "▏" : "Type a message…";
        _draft.Opacity = _typed.Length > 0 ? 1 : 0.55;
    }

    private void SendTyped()
    {
        if (_chat is not { } chat || _typed.Length == 0) return;
        string text = _typed.ToString();
        _typed.Clear();
        ShowKeyboard(false); // gives the chat window focus again
        if (!chat.Focus())
        {
            _typed.Append(text);
            ShowDraft();
            Log.Write("Steam chat: could not give the chat window focus to send the message.");
            return;
        }
        // Steam keeps its message box focused in a chat window, so the text lands there.
        SyntheticInput.Text(text);
        SyntheticInput.Press(0x0D); // Enter
    }

    private void FocusOverlay()
    {
        if (Window.GetWindow(_root) is not { } window) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (Native.GetForegroundWindow() == hwnd) return;
        uint ourThread = Native.GetCurrentThreadId();
        uint theirThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        bool attach = theirThread != 0 && theirThread != ourThread;
        if (attach) Native.AttachThreadInput(ourThread, theirThread, true);
        try { Native.SetForegroundWindow(hwnd); }
        finally { if (attach) Native.AttachThreadInput(ourThread, theirThread, false); }
    }

    /// <summary>While you chat, their name and picture shrink to a header so the conversation gets the room.</summary>
    private void Compact(bool chat)
    {
        double avatar = chat ? 72 : AvatarSize;
        _avatar.Width = _avatar.Height = avatar;
        _avatar.CornerRadius = new CornerRadius(chat ? 20 : 30);
        _initial.FontSize = chat ? 30 : 54;
        _name.FontSize = chat ? 32 : 52;
        _text.Margin = new Thickness(chat ? 20 : 28, 0, 0, 0);
        _facts.Visibility = chat || _facts.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _actions.Visibility = chat ? Visibility.Collapsed : Visibility.Visible;
        bool games = !chat && _games.Count > 0;
        _gamesLabel.Visibility = _games.View.Visibility = _gameInfo.Visibility = games ? Visibility.Visible : Visibility.Collapsed;
        _note.Visibility = !chat && _note.Text.Length > 0 && _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The chat frame fills the space left under the header (and above the keyboard, while it's open).</summary>
    private void FitChat()
    {
        if (!_chatOpen) return;
        DependencyObject? node = _root;
        while (node is not null && node is not ScrollViewer)
            node = VisualTreeHelper.GetParent(node);
        double height = 520;
        if (node is ScrollViewer scroller && scroller.ViewportHeight > 0 && scroller.Content is Visual content)
        {
            try
            {
                double top = _chatFrame.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
                double below = (_draftBox.IsVisible || _typing ? 60 : 0) + (_typing ? _keyboard.View.ActualHeight + 12 : 0);
                if (_typing && _keyboard.View.ActualHeight == 0) below += 300; // first layout: about the keyboard's height
                height = scroller.ViewportHeight - top - below - 20;
                scroller.ScrollToTop();
            }
            catch (InvalidOperationException) { } // not laid out yet
        }
        _chatFrame.Height = Math.Max(240, height);
    }

    private void PlaceChat()
    {
        if (_chat is { } chat && ScreenRect(_chatFrame) is { } rect) chat.Place(rect);
    }

    /// <summary>Where an element is on screen, in pixels (the overlay is scaled for TVs, so this goes through WPF).</summary>
    private static Native.RECT? ScreenRect(FrameworkElement element)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null) return null;
        var a = element.PointToScreen(new Point(0, 0));
        var b = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
        const int inset = 6; // inside the rounded corners
        return new Native.RECT
        {
            Left = (int)Math.Round(a.X) + inset,
            Top = (int)Math.Round(a.Y) + inset,
            Right = (int)Math.Round(b.X) - inset,
            Bottom = (int)Math.Round(b.Y) - inset,
        };
    }

    // ---- Loading ----------------------------------------------------------------------------

    private async Task LoadAsync(SteamFriend friend, int token)
    {
        string key = ApiKey().Trim();
        if (key.Length == 0)
        {
            _note.Text = "Add your Steam Web API key in Settings › Steam to see what they play, their level and when you became friends.";
            _note.Visibility = _chatOpen ? Visibility.Collapsed : Visibility.Visible;
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
                _note.Visibility = _chatOpen ? Visibility.Collapsed : Visibility.Visible;
            }
            if (_root.IsVisible && _games.Count > 0 && !_chatOpen) Focus();
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
        _facts.Visibility = facts.Count > 0 && !_chatOpen ? Visibility.Visible : Visibility.Collapsed;
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
        _actions.Children.Add(Ui.ActionPill(_resources, "X", "Chat", primary: _games.Count == 0, OpenChat));
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
            if (_chatOpen) Compact(true);
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
        if (_chatOpen) Compact(true);
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
