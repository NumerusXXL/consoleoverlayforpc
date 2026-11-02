using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ConsoleOverlay.Discord;
using ConsoleOverlay.Input;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Discord page, laid out like Discord on Xbox. The left column is you (mic, deafen) and a menu; the right
/// column shows what the menu picked:
///   People    – everyone in your call, a ring around whoever is talking. A opens a person.
///   Person    – their volume (0–200%), mute for you, reset.
///   Channels  – voice channels of a server with who is in them; A joins. "Other servers" lists your servers.
///   Audio     – mic and speaker volume, input mode, noise suppression, echo cancellation, gain, devices.
/// Left / right changes the focused volume, Y mutes the focused thing, X mutes your mic from anywhere,
/// B steps back (person → people → menu → closes the overlay).
/// Before Discord is set up or connected it shows what to do instead.
/// </summary>
internal sealed class DiscordPage : IOverlayPage
{
    public const string SetupGuideUrl = "https://github.com/NumerusXXL/consoleoverlayforpc#discord-voice-controls";
    private const string DeveloperPortalUrl = "https://discord.com/developers/applications";
    private static readonly Color SpeakingColor = Color.FromRgb(0x3B, 0xD6, 0x7F);
    private static readonly Dictionary<string, BitmapImage> Avatars = []; // small, and the same people come back

    private enum View { People, Person, Channels, Servers, Audio, InputDevices, OutputDevices }

    private sealed class MemberRow
    {
        public required VoiceMember Member { get; set; }
        public required Ellipse Ring { get; init; }
        public required TextBlock Value { get; init; }
        public required TextBlock Status { get; init; }
        public Bar? Bar { get; init; }
    }

    private sealed class Bar
    {
        public required ColumnDefinition Filled { get; init; }
        public required ColumnDefinition Empty { get; init; }
        public required Border Fill { get; init; }

        public void Set(int value, int max, bool dim)
        {
            Filled.Width = new GridLength(Math.Max(value, 0.0001), GridUnitType.Star);
            Empty.Width = new GridLength(Math.Max(max - value, 0.0001), GridUnitType.Star);
            Fill.Opacity = dim ? 0.3 : 1;
        }
    }

    /// <summary>A volume row: left / right changes it.</summary>
    private sealed class Slider
    {
        public required Func<int> Get { get; init; }
        public required Action<int> Set { get; init; }
        public required int Max { get; init; }
        public required int Step { get; init; }
        public required TextBlock Value { get; init; }
        public required Bar Bar { get; init; }
        public Func<bool>? Dim { get; init; }
    }

    private readonly FrameworkElement _resources;
    private readonly DiscordClient _client;
    private readonly Grid _root = new();
    private readonly StackPanel _status = new();
    private readonly Grid _controls = new();
    private readonly StackPanel _menu = new();
    private readonly StackPanel _detail = new();
    private readonly Dictionary<string, MemberRow> _members = [];
    private readonly Dictionary<string, Slider> _sliders = [];
    private readonly List<(TextBlock Text, Func<string> Value)> _values = [];
    private readonly Dictionary<string, Action> _yActions = [];
    private string _layoutSignature = "";
    private bool _active;

    private View _view = View.People;
    private string? _personId;
    private string? _browseGuildId;
    private List<JoinableChannel>? _channels;
    private string? _channelsGuildId;
    private List<DiscordServer>? _servers;
    private bool _loading;
    private string? _loadError;
    private string? _joining;

    /// <summary>Close the overlay (without going back to the game), then run this.</summary>
    public event Action<Action>? CloseThen;
    /// <summary>Show Settings › Discord, where the Client ID and Secret are entered.</summary>
    public event Action? OpenSettings;
    public event Action? HintChanged;

    public DiscordPage(FrameworkElement resources)
    {
        _resources = resources;
        _client = new DiscordClient(resources.Dispatcher);
        _client.StateChanged += () =>
        {
            if (!_active) return;
            if (_client.State == DiscordState.Ready) PickDefaultView();
            _layoutSignature = "";
            Refresh();
            HintChanged?.Invoke();
        };
        _client.VoiceChanged += () =>
        {
            if (!_active) return;
            // You just joined a channel from the list: show the call.
            if (_joining is not null && _client.Channel?.Id == _joining)
            {
                _joining = null;
                _channels = null;
                SetView(View.People, focusDetail: true);
                return;
            }
            Refresh();
        };
        _client.SpeakingChanged += (userId, speaking) =>
        {
            if (_active && _members.TryGetValue(userId, out var row)) ShowSpeaking(row, speaking);
        };

        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_detail, 2);
        _controls.Children.Add(_menu);
        _controls.Children.Add(_detail);

        _root.Children.Add(_status);
        _root.Children.Add(_controls);
    }

    public Panel Content => _root;

    public string Hint => _client.State == DiscordState.Ready
        ? "A select · X mute your mic · Y mute · ← → volume · B back · LB / RB other pages"
        : "A select · B back to game · LB / RB other pages";

    public void SignOut() => _client.SignOut();

    public void UpdateSettings(AppSettings settings) => _client.Configure(settings.DiscordClientId, settings.DiscordClientSecret);

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        _layoutSignature = "";
        _channels = null; // who is where changes; read it again
        _servers = null;
        PickDefaultView();
        _client.SetSpeakingWanted(true);
        Refresh();
        HintChanged?.Invoke();
        _ = _client.ConnectAsync(allowPrompt: false);
    }

    public void Deactivate()
    {
        _active = false;
        _client.SetSpeakingWanted(false);
        _status.Children.Clear();
        _menu.Children.Clear();
        _detail.Children.Clear();
        ClearRows();
        _layoutSignature = "";
    }

    private void PickDefaultView()
    {
        _view = _client.Channel is null ? View.Channels : View.People;
        _personId = null;
        _browseGuildId = null;
    }

    public UIElement? FirstFocus() => _client.State == DiscordState.Ready
        ? _menu.Children.OfType<Button>().FirstOrDefault()
        : _status.Children.OfType<Button>().FirstOrDefault(b => b.Tag is "action") ?? _status.Children.OfType<Button>().FirstOrDefault();

    // ---- Input ------------------------------------------------------------------------------

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_client.State != DiscordState.Ready)
            return command is NavCommand.Mute or NavCommand.Secondary or NavCommand.ColumnLeft or NavCommand.ColumnRight;

        string? key = (focused as Button)?.Tag as string;
        bool inDetail = focused is DependencyObject d && Ui.IsInside(d, _detail);

        switch (command)
        {
            case NavCommand.Secondary:
                _client.SetMuted(!_client.Muted);
                return true;
            case NavCommand.Mute:
                if (key is not null && _yActions.TryGetValue(key, out var y)) y();
                else _client.SetMuted(!_client.Muted);
                return true;
            case NavCommand.ColumnLeft:
                FocusMenuItem();
                return true;
            case NavCommand.ColumnRight:
                FocusFirstIn(_detail);
                return true;
            case NavCommand.Back:
                return GoBack(inDetail);
            case NavCommand.Left or NavCommand.Right when key is not null:
                int direction = command == NavCommand.Right ? 1 : -1;
                if (_sliders.TryGetValue(key, out var slider))
                {
                    slider.Set(Math.Clamp(slider.Get() + direction * slider.Step, 0, slider.Max));
                    UpdateInPlace();
                    return true;
                }
                if (_members.TryGetValue(key, out var row) && !row.Member.IsSelf)
                {
                    _client.SetUserVolume(row.Member, row.Member.Volume + direction * 5);
                    UpdateMember(row);
                    return true;
                }
                // Right from the menu goes into the detail column, left from the detail column back to the menu.
                if (direction > 0 && !inDetail) { FocusFirstIn(_detail); return true; }
                if (direction < 0 && inDetail) { FocusMenuItem(); return true; }
                return false;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) => key switch
    {
        Key.X => Handle(NavCommand.Secondary, focused),
        Key.M => Handle(NavCommand.Mute, focused),
        Key.Left => Handle(NavCommand.Left, focused),
        Key.Right => Handle(NavCommand.Right, focused),
        Key.Back or Key.Escape => Handle(NavCommand.Back, focused),
        Key.Tab when _client.State == DiscordState.Ready =>
            Handle(focused is DependencyObject d && Ui.IsInside(d, _detail) ? NavCommand.ColumnLeft : NavCommand.ColumnRight, focused),
        _ => false,
    };

    /// <summary>B: one step back. Returns false at the top, which closes the overlay.</summary>
    private bool GoBack(bool inDetail)
    {
        switch (_view)
        {
            case View.Person:
                SetView(View.People, focusDetail: true, focusKey: _personId);
                return true;
            case View.Servers:
                SetView(View.Channels, focusDetail: true);
                return true;
            case View.Channels when _browseGuildId is not null && _browseGuildId != _client.Channel?.GuildId:
                _browseGuildId = null;
                _channels = null;
                SetView(View.Servers, focusDetail: true, focusKey: "server:" + _channelsGuildId);
                return true;
            case View.InputDevices or View.OutputDevices:
                SetView(View.Audio, focusDetail: true, focusKey: _view == View.InputDevices ? "inputDevice" : "outputDevice");
                return true;
        }
        if (inDetail)
        {
            FocusMenuItem();
            return true;
        }
        return false;
    }

    private void SetView(View view, bool focusDetail, string? focusKey = null)
    {
        _view = view;
        _layoutSignature = "";
        Refresh();
        if (!focusDetail) return;
        var target = _detail.Children.OfType<Button>().FirstOrDefault(b => focusKey is not null && b.Tag as string == focusKey);
        if (target is not null) Focus(target);
        else FocusFirstIn(_detail);
    }

    private void FocusMenuItem()
    {
        string key = _view switch
        {
            View.People or View.Person => "menu:people",
            View.Channels or View.Servers => "menu:channels",
            _ => "menu:audio",
        };
        var target = _menu.Children.OfType<Button>().FirstOrDefault(b => b.Tag as string == key) ?? _menu.Children.OfType<Button>().FirstOrDefault();
        if (target is not null) Focus(target);
    }

    private static void FocusFirstIn(Panel panel)
    {
        var target = panel.Children.OfType<Button>().FirstOrDefault(b => b.Tag is not "back") ?? panel.Children.OfType<Button>().FirstOrDefault();
        if (target is not null) Focus(target);
    }

    private static void Focus(Button target)
    {
        target.Focus();
        Keyboard.Focus(target);
        target.BringIntoView();
    }

    // ---- Building ---------------------------------------------------------------------------

    private void ClearRows()
    {
        _members.Clear();
        _sliders.Clear();
        _values.Clear();
        _yActions.Clear();
    }

    private void Refresh()
    {
        bool ready = _client.State == DiscordState.Ready;
        string signature = ready ? ReadySignature() : $"{_client.State}|{_client.Problem}";

        if (signature == _layoutSignature)
        {
            if (ready) UpdateInPlace();
            return;
        }

        var focusedTag = (Keyboard.FocusedElement as Button)?.Tag as string;
        bool hadFocus = Keyboard.FocusedElement is DependencyObject d && Ui.IsInside(d, _root);
        _layoutSignature = signature;

        _status.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        _controls.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        _status.Children.Clear();
        _menu.Children.Clear();
        _detail.Children.Clear();
        ClearRows();
        if (ready)
        {
            BuildMenu();
            BuildDetail();
            UpdateInPlace();
        }
        else
        {
            BuildStatus();
        }

        if (!hadFocus) return;
        var buttons = (ready ? _menu.Children.OfType<Button>().Concat(_detail.Children.OfType<Button>()) : _status.Children.OfType<Button>()).ToList();
        var target = buttons.FirstOrDefault(b => focusedTag is not null && b.Tag as string == focusedTag);
        if (target is not null) Focus(target);
        else if (FirstFocus() is Button first) Focus(first);
    }

    /// <summary>What the rows depend on. Values that change in place (volumes, mute) are left out.</summary>
    private string ReadySignature()
    {
        var channel = _client.Channel;
        string call = channel is null ? "-" : channel.Id + "|" + channel.GuildName + "|" + string.Join(",", channel.Members.Select(m => m.Id + m.Name));
        string view = _view switch
        {
            View.Person => "person:" + _personId,
            View.Channels => $"channels:{_browseGuildId}:{_loading}:{_loadError}:{_channels?.Count}:" +
                string.Join(",", _channels?.Select(c => c.Id + ":" + string.Join("/", c.People)) ?? []),
            View.Servers => $"servers:{_loading}:{_loadError}:{_servers?.Count}",
            View.InputDevices => "in:" + string.Join(",", _client.Settings.InputDevices.Select(x => x.Id)) + _client.Settings.InputDeviceId,
            View.OutputDevices => "out:" + string.Join(",", _client.Settings.OutputDevices.Select(x => x.Id)) + _client.Settings.OutputDeviceId,
            View.Audio => "audio:" + DeviceName(_client.Settings.InputDevices, _client.Settings.InputDeviceId) + DeviceName(_client.Settings.OutputDevices, _client.Settings.OutputDeviceId),
            _ => _view.ToString(),
        };
        return "ready|" + call + "|" + view;
    }

    private void BuildMenu()
    {
        var channel = _client.Channel;
        _menu.Children.Add(Ui.Section(_resources, "You", first: true));
        _menu.Children.Add(OptionRow(_menu, "mic", "\uE720", "Microphone", null,
            () => _client.Muted ? "Muted" : "On", () => _client.SetMuted(!_client.Muted)));
        _menu.Children.Add(OptionRow(_menu, "deaf", "\uE7F6", "Deafen", null,
            () => _client.Deafened ? "On" : "Off", () => _client.SetDeafened(!_client.Deafened)));

        _menu.Children.Add(Ui.Section(_resources, "Discord", first: false));
        if (channel is not null)
            _menu.Children.Add(MenuRow("menu:people", "\uE716", "In this call", View.People, () => channel.Members.Count.ToString(CultureInfo.CurrentCulture)));
        _menu.Children.Add(MenuRow("menu:channels", "\uE8F2", channel is null ? "Join a channel" : "Switch channel", View.Channels, null));
        _menu.Children.Add(MenuRow("menu:audio", "\uE767", "Voice & audio", View.Audio, null));

        if (channel is not null)
        {
            var leave = OptionRow(_menu, "leave", "\uE778", "Leave call", null, null, () => _client.LeaveChannel());
            leave.Foreground = (Brush)_resources.FindResource("DangerTextBrush");
            leave.Margin = new Thickness(0, 14, 0, 0);
            _menu.Children.Add(leave);
        }
    }

    private Button MenuRow(string key, string glyph, string title, View view, Func<string>? value)
    {
        bool current = view == _view || (view == View.People && _view == View.Person)
                       || (view == View.Channels && _view == View.Servers)
                       || (view == View.Audio && _view is View.InputDevices or View.OutputDevices);
        var row = OptionRow(_menu, key, glyph, title, null, () => (value?.Invoke() ?? "") + "  ›", () =>
        {
            if (view == View.Channels) { _browseGuildId = null; _channels = null; }
            SetView(view, focusDetail: true);
        });
        if (current)
            row.FontWeight = FontWeights.SemiBold;
        return row;
    }

    private void BuildDetail()
    {
        switch (_view)
        {
            case View.People: BuildPeople(); break;
            case View.Person: BuildPerson(); break;
            case View.Channels: BuildChannels(); break;
            case View.Servers: BuildServers(); break;
            case View.Audio: BuildAudio(); break;
            case View.InputDevices: BuildDevices(input: true); break;
            case View.OutputDevices: BuildDevices(input: false); break;
        }
    }

    // People ----------------------------------------------------------------------------------

    private void BuildPeople()
    {
        var channel = _client.Channel;
        if (channel is null)
        {
            _detail.Children.Add(Ui.Section(_resources, "Voice", first: true));
            _detail.Children.Add(Ui.Placeholder(_resources, "You're not in a voice channel", "Pick Join a channel to find one."));
            return;
        }

        string where = channel.GuildName is null ? channel.Name : $"{channel.Name}  ·  {channel.GuildName}";
        _detail.Children.Add(Ui.Section(_resources, where, first: true));
        foreach (var member in channel.Members)
            _detail.Children.Add(BuildMember(member));
        _detail.Children.Add(Ui.Note(_resources, "A on someone for their options. ← → changes how loud you hear them."));
    }

    private Button BuildMember(VoiceMember member)
    {
        var ring = new Ellipse { Width = 44, Height = 44, Stroke = new SolidColorBrush(SpeakingColor), StrokeThickness = 2.5, Opacity = 0 };
        var avatar = Avatar(member, 36, ring);
        avatar.Margin = new Thickness(0, 0, 14, 0);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock { Text = member.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        var value = new TextBlock { FontSize = 14, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(value, 1);
        top.Children.Add(value);

        var status = new TextBlock { Style = (Style)_resources.FindResource("RowHint") };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(top);
        text.Children.Add(status);
        Bar? bar = null;
        if (!member.IsSelf)
            text.Children.Add(MakeBar(out bar, height: 4));

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(avatar);
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = layout,
            Tag = member.Id,
            Padding = new Thickness(12, 10, 16, 10),
            ToolTip = member.Name,
        };
        var row = new MemberRow { Member = member, Ring = ring, Value = value, Status = status, Bar = bar };
        button.Click += (_, _) =>
        {
            if (row.Member.IsSelf)
            {
                _client.SetMuted(!_client.Muted);
                return;
            }
            _personId = member.Id;
            SetView(View.Person, focusDetail: true);
        };
        button.PreviewMouseWheel += (_, e) =>
        {
            if (row.Member.IsSelf) return;
            _client.SetUserVolume(row.Member, row.Member.Volume + (e.Delta > 0 ? 5 : -5));
            UpdateMember(row);
            e.Handled = true;
        };
        _yActions[member.Id] = () =>
        {
            if (row.Member.IsSelf) _client.SetMuted(!_client.Muted);
            else
            {
                _client.SetUserMutedForMe(row.Member, !row.Member.MutedForMe);
                UpdateMember(row);
            }
        };

        _members[member.Id] = row;
        ring.Opacity = _client.IsSpeaking(member.Id) ? 1 : 0;
        return button;
    }

    // Person ----------------------------------------------------------------------------------

    private VoiceMember? Person => _client.Channel?.Members.FirstOrDefault(m => m.Id == _personId);

    private void BuildPerson()
    {
        var member = Person;
        _detail.Children.Add(BackRow("‹  People in this call"));
        if (member is null)
        {
            _detail.Children.Add(Ui.Placeholder(_resources, "They left the call", "B to go back."));
            return;
        }

        // Header: big avatar with the talking ring, name and status.
        var ring = new Ellipse { Width = 72, Height = 72, Stroke = new SolidColorBrush(SpeakingColor), StrokeThickness = 3, Opacity = 0 };
        var avatar = Avatar(member, 62, ring);
        avatar.Margin = new Thickness(0, 0, 18, 0);
        var name = new TextBlock { Text = member.Name, FontSize = 22, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Style = (Style)_resources.FindResource("RowHint"), FontSize = 13 };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(status);
        var header = new Grid { Margin = new Thickness(14, 10, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.Children.Add(avatar);
        Grid.SetColumn(text, 1);
        header.Children.Add(text);
        _detail.Children.Add(header);
        // Registered as a member row so talking and status update live; the value text isn't shown.
        _members[member.Id] = new MemberRow { Member = member, Ring = ring, Value = new TextBlock(), Status = status };

        _detail.Children.Add(SliderRow("person:volume", "Volume", "How loud you hear them", 200, 5,
            () => Person?.Volume ?? 100,
            v => { if (Person is { } p) _client.SetUserVolume(p, v); },
            v => $"{v}%", () => Person?.MutedForMe == true));
        _detail.Children.Add(OptionRow(_detail, "person:mute", "\uE74F", "Mute for me", "Only you stop hearing them",
            () => Person?.MutedForMe == true ? "On" : "Off",
            () => { if (Person is { } p) _client.SetUserMutedForMe(p, !p.MutedForMe); UpdateInPlace(); }));
        _detail.Children.Add(OptionRow(_detail, "person:reset", "\uE72C", "Reset volume", "Back to 100%",
            null, () => { if (Person is { } p) _client.SetUserVolume(p, 100); UpdateInPlace(); }));
    }

    // Channels and servers --------------------------------------------------------------------

    private string? ChannelsGuild => _browseGuildId ?? _client.Channel?.GuildId;

    private void BuildChannels()
    {
        var guildId = ChannelsGuild;
        if (guildId is null)
        {
            // Not in a server channel: start from the server list.
            _view = View.Servers;
            BuildServers();
            return;
        }

        if (_browseGuildId is not null && _browseGuildId != _client.Channel?.GuildId)
            _detail.Children.Add(BackRow("‹  All servers"));
        _detail.Children.Add(Ui.Section(_resources, _client.ServerName(guildId) ?? "Voice channels", first: _detail.Children.Count == 0));

        if (_channels is null || _channelsGuildId != guildId)
        {
            LoadChannels(guildId);
            _detail.Children.Add(Ui.Placeholder(_resources, "Loading voice channels…", "One moment."));
        }
        else if (_loadError is not null)
        {
            _detail.Children.Add(Ui.Placeholder(_resources, "Couldn't load the channels", _loadError));
        }
        else if (_channels.Count == 0)
        {
            _detail.Children.Add(Ui.Placeholder(_resources, "No voice channels you can see", "Pick another server."));
        }
        else
        {
            foreach (var channel in _channels)
            {
                bool here = channel.Id == _client.Channel?.Id;
                string who = channel.People.Count == 0 ? "Nobody here" : string.Join(", ", channel.People.Take(4)) + (channel.People.Count > 4 ? $" and {channel.People.Count - 4} more" : "");
                var id = channel.Id;
                _detail.Children.Add(OptionRow(_detail, "channel:" + id, "\uE767", channel.Name, who,
                    () => here ? "You're here" : _joining == id ? "Joining…" : "Join",
                    () =>
                    {
                        if (here) { SetView(View.People, focusDetail: true); return; }
                        _joining = id;
                        _client.JoinChannel(id);
                        UpdateInPlace();
                    }));
            }
        }

        _detail.Children.Add(OptionRow(_detail, "servers", "\uE80F", "Other servers", null, () => "›", () =>
            SetView(View.Servers, focusDetail: true)));
    }

    private async void LoadChannels(string guildId)
    {
        if (_loading) return;
        _loading = true;
        _loadError = null;
        try
        {
            if (_client.ServerName(guildId) is null)
                await _client.GetServersAsync(); // for the server's name
            _channels = await _client.GetVoiceChannelsAsync(guildId);
            _channelsGuildId = guildId;
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not list voice channels: {ex.Message}");
            _channels = [];
            _channelsGuildId = guildId;
            _loadError = ex.Message;
        }
        _loading = false;
        if (_active && _view == View.Channels) Refresh();
    }

    private void BuildServers()
    {
        if (_client.Channel?.GuildId is not null || _browseGuildId is not null)
            _detail.Children.Add(BackRow("‹  Voice channels"));
        _detail.Children.Add(Ui.Section(_resources, "Your servers", first: _detail.Children.Count == 0));

        if (_servers is null)
        {
            LoadServers();
            _detail.Children.Add(Ui.Placeholder(_resources, "Loading your servers…", "One moment."));
            return;
        }
        if (_loadError is not null)
        {
            _detail.Children.Add(Ui.Placeholder(_resources, "Couldn't load your servers", _loadError));
            return;
        }
        foreach (var server in _servers)
        {
            var id = server.Id;
            _detail.Children.Add(OptionRow(_detail, "server:" + id, "\uE80F", server.Name, null,
                () => id == _client.Channel?.GuildId ? "In a call here  ›" : "›",
                () =>
                {
                    _browseGuildId = id;
                    _channels = null;
                    SetView(View.Channels, focusDetail: true);
                }));
        }
    }

    private async void LoadServers()
    {
        if (_loading) return;
        _loading = true;
        _loadError = null;
        try
        {
            _servers = await _client.GetServersAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not list servers: {ex.Message}");
            _servers = [];
            _loadError = ex.Message;
        }
        _loading = false;
        if (_active && _view == View.Servers) Refresh();
    }

    // Voice & audio ---------------------------------------------------------------------------

    private void BuildAudio()
    {
        var s = _client.Settings;
        _detail.Children.Add(Ui.Section(_resources, "Volume", first: true));
        _detail.Children.Add(SliderRow("audio:input", "Microphone volume", null, 100, 2,
            () => s.InputVolume, v => _client.SetInputVolume(v), v => $"{v}%", null));
        _detail.Children.Add(SliderRow("audio:output", "Speaker volume", "Everyone in Discord", 200, 4,
            () => s.OutputVolume, v => _client.SetOutputVolume(v), v => $"{v}%", null));

        _detail.Children.Add(Ui.Section(_resources, "Voice", first: false));
        _detail.Children.Add(OptionRow(_detail, "audio:mode", "\uE720", "Input mode", "Push to talk uses the key set in Discord",
            () => s.PushToTalk ? "Push to talk" : "Voice activity", () => { _client.SetPushToTalk(!s.PushToTalk); UpdateInPlace(); }));
        _detail.Children.Add(OptionRow(_detail, "audio:noise", "\uE9E9", "Noise suppression", null,
            () => s.NoiseSuppression ? "On" : "Off", () => { _client.SetNoiseSuppression(!s.NoiseSuppression); UpdateInPlace(); }));
        _detail.Children.Add(OptionRow(_detail, "audio:echo", "\uE8D6", "Echo cancellation", null,
            () => s.EchoCancellation ? "On" : "Off", () => { _client.SetEchoCancellation(!s.EchoCancellation); UpdateInPlace(); }));
        _detail.Children.Add(OptionRow(_detail, "audio:gain", "\uE713", "Automatic gain control", null,
            () => s.AutomaticGain ? "On" : "Off", () => { _client.SetAutomaticGain(!s.AutomaticGain); UpdateInPlace(); }));

        _detail.Children.Add(Ui.Section(_resources, "Devices", first: false));
        _detail.Children.Add(OptionRow(_detail, "inputDevice", "\uE720", "Microphone", null,
            () => DeviceName(s.InputDevices, s.InputDeviceId) + "  ›", () => SetView(View.InputDevices, focusDetail: true)));
        _detail.Children.Add(OptionRow(_detail, "outputDevice", "\uE7F6", "Speakers / headphones", null,
            () => DeviceName(s.OutputDevices, s.OutputDeviceId) + "  ›", () => SetView(View.OutputDevices, focusDetail: true)));
    }

    private void BuildDevices(bool input)
    {
        var s = _client.Settings;
        var devices = input ? s.InputDevices : s.OutputDevices;
        _detail.Children.Add(BackRow("‹  Voice & audio"));
        _detail.Children.Add(Ui.Section(_resources, input ? "Microphone" : "Speakers / headphones", first: false));
        foreach (var device in devices)
        {
            var id = device.Id;
            _detail.Children.Add(OptionRow(_detail, "device:" + id, input ? "\uE720" : "\uE7F6", device.Name, null,
                () => (input ? s.InputDeviceId : s.OutputDeviceId) == id ? "✓" : "",
                () =>
                {
                    if (input) _client.SetInputDevice(id);
                    else _client.SetOutputDevice(id);
                    SetView(View.Audio, focusDetail: true, focusKey: input ? "inputDevice" : "outputDevice");
                }));
        }
    }

    private static string DeviceName(List<AudioDevice> devices, string? id)
    {
        if (id is null or "default") return "Default";
        var name = devices.FirstOrDefault(d => d.Id == id)?.Name ?? "Default";
        return name.Length > 26 ? name[..25] + "…" : name;
    }

    // ---- Row builders -----------------------------------------------------------------------

    /// <summary>A row with an icon, title, optional hint, and a value on the right that updates in place.</summary>
    private Button OptionRow(Panel owner, string key, string glyph, string title, string? hint, Func<string>? value, Action onPress)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Ui.Icon(_resources, glyph, 16);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
        if (hint is not null)
            text.Children.Add(new TextBlock { Text = hint, Style = (Style)_resources.FindResource("RowHint"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (value is not null)
        {
            var valueText = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(valueText, 2);
            grid.Children.Add(valueText);
            _values.Add((valueText, value));
            if (key == "mic")
                _values.Add((icon, () => _client.Muted ? "\uEC54" : "\uE720"));
        }

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = grid,
            Tag = key,
            Padding = new Thickness(16, 13, 16, 13),
        };
        button.Click += (_, _) => onPress();
        return button;
    }

    private Button SliderRow(string key, string title, string? hint, int max, int step, Func<int> get, Action<int> set, Func<int, string> format, Func<bool>? dim)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = title, FontSize = 15 });
        if (hint is not null)
            titles.Children.Add(new TextBlock { Text = hint, Style = (Style)_resources.FindResource("RowHint") });
        top.Children.Add(titles);
        var value = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        Grid.SetColumn(value, 1);
        top.Children.Add(value);

        var content = new StackPanel();
        content.Children.Add(top);
        content.Children.Add(MakeBar(out var bar, height: 6));

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = content,
            Tag = key,
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var slider = new Slider { Get = get, Set = set, Max = max, Step = step, Value = value, Bar = bar, Dim = dim };
        _sliders[key] = slider;
        _values.Add((value, () => format(get())));
        button.PreviewMouseWheel += (_, e) =>
        {
            set(Math.Clamp(get() + (e.Delta > 0 ? step : -step), 0, max));
            UpdateInPlace();
            e.Handled = true;
        };
        return button;
    }

    private Button BackRow(string text)
    {
        var row = Ui.Row(_resources, text, null, null, () => GoBack(inDetail: true));
        row.Tag = "back";
        row.Opacity = 0.8;
        return row;
    }

    private Grid MakeBar(out Bar bar, double height)
    {
        var filled = new ColumnDefinition();
        var empty = new ColumnDefinition();
        var grid = new Grid { Height = height, Margin = new Thickness(0, height > 4 ? 10 : 8, 0, 0) };
        grid.ColumnDefinitions.Add(filled);
        grid.ColumnDefinitions.Add(empty);
        var track = new Border { Background = (Brush)_resources.FindResource("TrackBrush"), CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { Background = (Brush)_resources.FindResource(height > 4 ? "TextBrush" : "SubtleTextBrush"), CornerRadius = new CornerRadius(height / 2) };
        grid.Children.Add(track);
        grid.Children.Add(fill);
        bar = new Bar { Filled = filled, Empty = empty, Fill = fill };
        return grid;
    }

    /// <summary>Their initial on a tile, covered by their picture once it has downloaded, inside a "talking" ring.</summary>
    private Grid Avatar(VoiceMember member, double size, Ellipse ring)
    {
        var initial = new Grid { Width = size, Height = size };
        initial.Children.Add(new Ellipse { Fill = (Brush)_resources.FindResource("TrackBrush") });
        initial.Children.Add(new TextBlock
        {
            Text = member.Name.Length > 0 ? char.ToUpper(member.Name[0], CultureInfo.CurrentCulture).ToString() : "?",
            FontSize = size * 0.42,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var avatar = new Grid { Width = ring.Width, Height = ring.Height, VerticalAlignment = VerticalAlignment.Center };
        avatar.Children.Add(initial);
        if (LoadAvatar(member.AvatarUrl) is { } picture)
            avatar.Children.Add(new Ellipse { Width = size, Height = size, Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } });
        avatar.Children.Add(ring);
        return avatar;
    }

    // ---- Live updates -----------------------------------------------------------------------

    private void UpdateInPlace()
    {
        foreach (var (text, value) in _values)
            text.Text = value();
        foreach (var slider in _sliders.Values)
            slider.Bar.Set(slider.Get(), slider.Max, slider.Dim?.Invoke() == true);

        foreach (var member in _client.Channel?.Members ?? [])
        {
            if (!_members.TryGetValue(member.Id, out var row)) continue;
            row.Member = member;
            UpdateMember(row);
            ShowSpeaking(row, _client.IsSpeaking(member.Id));
        }
    }

    private void UpdateMember(MemberRow row)
    {
        var m = row.Member;
        var notes = new List<string>();
        bool muted = m.IsSelf ? _client.Muted : m.SelfMuted;
        bool deafened = m.IsSelf ? _client.Deafened : m.SelfDeafened;
        if (deafened) notes.Add("Deafened");
        else if (muted) notes.Add("Muted");
        if (m.ServerMuted) notes.Add("Muted by the server");
        if (m.ServerDeafened) notes.Add("Deafened by the server");
        if (!m.IsSelf && m.MutedForMe) notes.Add("Muted for you");
        row.Status.Text = notes.Count > 0 ? string.Join("  ·  ", notes) : m.IsSelf ? "You" : "";
        row.Status.Visibility = row.Status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (m.IsSelf)
        {
            row.Value.Text = "";
            return;
        }
        row.Value.Text = m.MutedForMe ? "Muted" : $"{m.Volume}%";
        row.Bar?.Set(m.Volume, 200, m.MutedForMe);
    }

    private static void ShowSpeaking(MemberRow row, bool speaking)
    {
        double target = speaking ? 1 : 0;
        if (Math.Abs(row.Ring.Opacity - target) < 0.01 && !row.Ring.HasAnimatedProperties) return;
        row.Ring.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(speaking ? 80 : 260))));
    }

    private static BitmapImage? LoadAvatar(string? url)
    {
        if (url is null) return null;
        if (Avatars.TryGetValue(url, out var cached)) return cached;
        try
        {
            // WPF downloads it in the background; the initial shows until it arrives.
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = 96;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            Avatars[url] = image;
            return image;
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not load an avatar: {ex.Message}");
            return null;
        }
    }

    // ---- Not connected yet ------------------------------------------------------------------

    private void BuildStatus()
    {
        void Add(string title, string? hint, Action? action, bool primary = false)
        {
            var row = Ui.Row(_resources, title, hint, action is null ? null : "", action);
            if (primary) row.Tag = "action";
            _status.Children.Add(row);
        }

        switch (_client.State)
        {
            case DiscordState.NotSetUp:
                _status.Children.Add(Ui.Section(_resources, "One-time setup", first: true));
                _status.Children.Add(Ui.Placeholder(_resources, "Discord voice controls need a free Discord app",
                    "Discord only lets apps you registered yourself control voice. Create one in the Discord Developer Portal, " +
                    "then enter its Client ID and Client Secret in Settings. The setup guide walks you through it in a couple of minutes."));
                Add("Open the setup guide", "Opens in your browser", () => Open(SetupGuideUrl), primary: true);
                Add("Open the Discord Developer Portal", "discord.com/developers/applications", () => Open(DeveloperPortalUrl));
                Add("Enter Client ID and Secret", "Settings › Discord", () => OpenSettings?.Invoke());
                break;

            case DiscordState.NotRunning:
                _status.Children.Add(Ui.Placeholder(_resources, "Discord isn't running",
                    "Start the Discord desktop app. The browser version of Discord can't be controlled."));
                Add("Try again", null, () => _ = _client.ConnectAsync(allowPrompt: false), primary: true);
                break;

            case DiscordState.Connecting:
                _status.Children.Add(Ui.Placeholder(_resources, "Connecting to Discord…", "This takes a moment."));
                break;

            case DiscordState.NeedsAuthorization:
                _status.Children.Add(Ui.Placeholder(_resources, "Allow Console Overlay in Discord",
                    _client.Problem ?? "Discord asks once. The overlay closes and Discord comes to the front: click Authorize there, then open the overlay again."));
                Add("Connect to Discord", null, () => CloseThen?.Invoke(() =>
                {
                    _ = _client.ConnectAsync(allowPrompt: true);
                    BringDiscordToFront();
                }), primary: true);
                break;

            case DiscordState.WaitingForApproval:
                _status.Children.Add(Ui.Placeholder(_resources, "Click Authorize in Discord",
                    "Discord is showing a prompt asking whether Console Overlay may control your voice settings."));
                Add("Show Discord", null, () => CloseThen?.Invoke(BringDiscordToFront), primary: true);
                break;

            case DiscordState.Failed:
                _status.Children.Add(Ui.Placeholder(_resources, "Couldn't connect to Discord", _client.Problem ?? "Something went wrong. The log has details."));
                Add("Try again", null, () => _ = _client.ConnectAsync(allowPrompt: false), primary: true);
                Add("Check Client ID and Secret", "Settings › Discord", () => OpenSettings?.Invoke());
                Add("Open the setup guide", "Opens in your browser", () => Open(SetupGuideUrl));
                break;
        }
    }

    // ---- Leaving the overlay ----------------------------------------------------------------

    private void Open(string url) => CloseThen?.Invoke(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {url}: {ex.Message}");
        }
    });

    private static void BringDiscordToFront()
    {
        foreach (var name in new[] { "Discord", "DiscordPTB", "DiscordCanary" })
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        OpenWindows.SwitchTo(process.MainWindowHandle);
                        return;
                    }
                }
            }
        }

        // Discord is probably minimized to the tray; its own link scheme brings the window back.
        try
        {
            Process.Start(new ProcessStartInfo("discord://") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not bring Discord to the front: {ex.Message}");
        }
    }
}
