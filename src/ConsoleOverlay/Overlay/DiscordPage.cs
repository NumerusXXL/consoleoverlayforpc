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
/// The Discord page: your mic and deafen on the left, the voice channel you're in on the right with a ring around
/// whoever is talking. Left / right sets how loud you hear someone, A or Y mutes them for you, X mutes your mic
/// from anywhere on the page. Before Discord is set up or connected it shows what to do instead.
/// </summary>
internal sealed class DiscordPage : IOverlayPage
{
    public const string SetupGuideUrl = "https://github.com/NumerusXXL/consoleoverlayforpc#discord-voice-controls";
    private const string DeveloperPortalUrl = "https://discord.com/developers/applications";
    private const int VolumeStep = 5;
    private const string MicKey = "mic";
    private const string DeafKey = "deaf";
    private const string LeaveKey = "leave";
    private static readonly Color SpeakingColor = Color.FromRgb(0x3B, 0xD6, 0x7F);
    private static readonly Dictionary<string, BitmapImage> Avatars = []; // small, and the same people come back

    private sealed class MemberRow
    {
        public required VoiceMember Member { get; set; }
        public required Button Button { get; init; }
        public required Ellipse Ring { get; init; }
        public required TextBlock Value { get; init; }
        public required TextBlock Status { get; init; }
        public ColumnDefinition? Filled { get; init; }
        public ColumnDefinition? Empty { get; init; }
        public Border? Fill { get; init; }
    }

    private readonly FrameworkElement _resources;
    private readonly DiscordClient _client;
    private readonly Grid _root = new();
    private readonly StackPanel _status = new();
    private readonly Grid _controls = new();
    private readonly StackPanel _you = new();
    private readonly StackPanel _channel = new();
    private readonly Dictionary<string, MemberRow> _members = [];
    private TextBlock? _micValue, _micIcon, _deafValue;
    private string _layoutSignature = "";
    private bool _active;

    /// <summary>Close the overlay (without going back to the game), then run this.</summary>
    public event Action<Action>? CloseThen;
    public event Action? EditSettings;
    public event Action? ReloadSettings;
    public event Action? HintChanged;

    public DiscordPage(FrameworkElement resources)
    {
        _resources = resources;
        _client = new DiscordClient(resources.Dispatcher);
        _client.StateChanged += () =>
        {
            if (!_active) return;
            _layoutSignature = "";
            Refresh();
            HintChanged?.Invoke();
        };
        _client.VoiceChanged += () =>
        {
            if (_active) Refresh();
        };
        _client.SpeakingChanged += (userId, speaking) =>
        {
            if (_active && _members.TryGetValue(userId, out var row)) ShowSpeaking(row, speaking);
        };

        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        _controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_channel, 2);
        _controls.Children.Add(_you);
        _controls.Children.Add(_channel);

        _root.Children.Add(_status);
        _root.Children.Add(_controls);
    }

    public Panel Content => _root;

    public string Hint => _client.State == DiscordState.Ready
        ? "X mute your mic · A / Y mute · ← → their volume · LT / RT switch column · B back to game · LB / RB other pages"
        : "A select · B back to game · LB / RB other pages";

    public void UpdateSettings(AppSettings settings) => _client.Configure(settings.DiscordClientId, settings.DiscordClientSecret);

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        _layoutSignature = "";
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
        _you.Children.Clear();
        _channel.Children.Clear();
        _members.Clear();
        _micValue = _micIcon = _deafValue = null;
        _layoutSignature = "";
    }

    public UIElement? FirstFocus() => _client.State == DiscordState.Ready
        ? _you.Children.OfType<Button>().FirstOrDefault()
        : _status.Children.OfType<Button>().FirstOrDefault(b => b.Tag is "action") ?? _status.Children.OfType<Button>().FirstOrDefault();

    // ---- Input ------------------------------------------------------------------------------

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_client.State != DiscordState.Ready)
            return command is NavCommand.Mute or NavCommand.Secondary or NavCommand.ColumnLeft or NavCommand.ColumnRight;

        switch (command)
        {
            case NavCommand.Secondary:
                _client.SetMuted(!_client.Muted);
                return true;
            case NavCommand.Mute:
                if (focused is Button { Tag: string key })
                    Press(key, mute: true);
                return true;
            case NavCommand.ColumnLeft:
                FocusInto(_you);
                return true;
            case NavCommand.ColumnRight:
                FocusInto(_channel);
                return true;
            case NavCommand.Left or NavCommand.Right
                when focused is Button { Tag: string id } && _members.TryGetValue(id, out var row) && !row.Member.IsSelf:
                _client.SetUserVolume(row.Member, row.Member.Volume + (command == NavCommand.Right ? VolumeStep : -VolumeStep));
                UpdateMember(row);
                return true;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) => key switch
    {
        Key.X => Handle(NavCommand.Secondary, focused),
        Key.M => Handle(NavCommand.Mute, focused),
        Key.Left => Handle(NavCommand.Left, focused),
        Key.Right => Handle(NavCommand.Right, focused),
        Key.Tab when _client.State == DiscordState.Ready =>
            Handle(focused is DependencyObject d && Ui.IsInside(d, _channel) ? NavCommand.ColumnLeft : NavCommand.ColumnRight, focused),
        _ => false,
    };

    private static void FocusInto(Panel panel)
    {
        var target = panel.Children.OfType<Button>().FirstOrDefault();
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
        target.BringIntoView();
    }

    /// <summary>A (or a click) on a row; Y does the same except it never leaves the call.</summary>
    private void Press(string key, bool mute)
    {
        switch (key)
        {
            case MicKey:
                _client.SetMuted(!_client.Muted);
                break;
            case DeafKey:
                _client.SetDeafened(!_client.Deafened);
                break;
            case LeaveKey:
                if (!mute) _client.LeaveChannel();
                break;
            default:
                if (_members.TryGetValue(key, out var row))
                {
                    if (row.Member.IsSelf)
                        _client.SetMuted(!_client.Muted);
                    else
                    {
                        _client.SetUserMutedForMe(row.Member, !row.Member.MutedForMe);
                        UpdateMember(row);
                    }
                }
                break;
        }
    }

    // ---- Building ---------------------------------------------------------------------------

    private void Refresh()
    {
        bool ready = _client.State == DiscordState.Ready;
        var channel = _client.Channel;
        string signature = ready
            ? "ready|" + (channel is null ? "-" : channel.Id + "|" + channel.GuildName + "|" + string.Join(",", channel.Members.Select(m => m.Id + m.Name)))
            : $"{_client.State}|{_client.Problem}";

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
        _you.Children.Clear();
        _channel.Children.Clear();
        _members.Clear();
        if (ready) BuildControls();
        else BuildStatus();

        if (!hadFocus) return;
        var buttons = (ready ? _you.Children.OfType<Button>().Concat(_channel.Children.OfType<Button>()) : _status.Children.OfType<Button>()).ToList();
        var target = (UIElement?)buttons.FirstOrDefault(b => focusedTag is not null && b.Tag as string == focusedTag) ?? FirstFocus();
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
    }

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
                    "then put its Client ID and Client Secret in the settings file. The setup guide walks you through it in a couple of minutes."));
                Add("Open the setup guide", "Opens in your browser", () => Open(SetupGuideUrl), primary: true);
                Add("Open the Discord Developer Portal", "discord.com/developers/applications", () => Open(DeveloperPortalUrl));
                Add("Edit settings file", "Paste DiscordClientId and DiscordClientSecret, then save", () => EditSettings?.Invoke());
                Add("Reload settings", "Do this after saving the settings file", () => ReloadSettings?.Invoke());
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
                Add("Edit settings file", "Check DiscordClientId and DiscordClientSecret", () => EditSettings?.Invoke());
                Add("Open the setup guide", "Opens in your browser", () => Open(SetupGuideUrl));
                break;
        }
    }

    private void BuildControls()
    {
        _you.Children.Add(Ui.Section(_resources, "You", first: true));
        _you.Children.Add(ToggleRow(MicKey, "", "Microphone", out _micIcon, out _micValue));
        _you.Children.Add(ToggleRow(DeafKey, "", "Deafen", out _, out _deafValue));

        var channel = _client.Channel;
        if (channel is not null)
        {
            var leave = ToggleRow(LeaveKey, "", "Leave call", out _, out var leaveValue);
            leaveValue.Text = "";
            leave.Foreground = (Brush)_resources.FindResource("DangerTextBrush");
            leave.Margin = new Thickness(0, 14, 0, 0);
            _you.Children.Add(leave);
        }

        if (channel is null)
        {
            _channel.Children.Add(Ui.Section(_resources, "Voice", first: true));
            _channel.Children.Add(Ui.Placeholder(_resources, "You're not in a voice channel",
                "Join one in Discord and the people in it show up here. Mute and deafen work either way."));
        }
        else
        {
            string where = channel.GuildName is null ? channel.Name : $"{channel.Name}  ·  {channel.GuildName}";
            _channel.Children.Add(Ui.Section(_resources, where, first: true));
            foreach (var member in channel.Members)
                _channel.Children.Add(BuildMember(member));
        }
        UpdateInPlace();
    }

    private Button ToggleRow(string key, string glyph, string title, out TextBlock icon, out TextBlock value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        icon = Ui.Icon(_resources, glyph, 16);
        icon.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(icon);
        var text = new TextBlock { Text = title, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        value = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = grid,
            Tag = key,
            Padding = new Thickness(16, 14, 16, 14),
        };
        button.Click += (_, _) => Press(key, mute: false);
        return button;
    }

    private Button BuildMember(VoiceMember member)
    {
        // Avatar: their initial on a tile, covered by their picture once it has downloaded, inside a "talking" ring.
        var ring = new Ellipse { Width = 44, Height = 44, Stroke = new SolidColorBrush(SpeakingColor), StrokeThickness = 2.5, Opacity = 0 };
        var initial = new Grid { Width = 36, Height = 36 };
        initial.Children.Add(new Ellipse { Fill = (Brush)_resources.FindResource("TrackBrush") });
        initial.Children.Add(new TextBlock
        {
            Text = member.Name.Length > 0 ? char.ToUpper(member.Name[0], CultureInfo.CurrentCulture).ToString() : "?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var avatar = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        avatar.Children.Add(initial);
        if (LoadAvatar(member.AvatarUrl) is { } picture)
            avatar.Children.Add(new Ellipse { Width = 36, Height = 36, Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } });
        avatar.Children.Add(ring);

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

        ColumnDefinition? filled = null, empty = null;
        Border? fill = null;
        if (!member.IsSelf)
        {
            // How loud you hear them: 0 to 200%, so normal (100%) sits in the middle.
            filled = new ColumnDefinition();
            empty = new ColumnDefinition();
            var bar = new Grid { Height = 4, Margin = new Thickness(0, 8, 0, 0) };
            bar.ColumnDefinitions.Add(filled);
            bar.ColumnDefinitions.Add(empty);
            var track = new Border { Background = (Brush)_resources.FindResource("TrackBrush"), CornerRadius = new CornerRadius(2) };
            Grid.SetColumnSpan(track, 2);
            fill = new Border { Background = (Brush)_resources.FindResource("SubtleTextBrush"), CornerRadius = new CornerRadius(2) };
            bar.Children.Add(track);
            bar.Children.Add(fill);
            text.Children.Add(bar);
        }

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
        var row = new MemberRow { Member = member, Button = button, Ring = ring, Value = value, Status = status, Filled = filled, Empty = empty, Fill = fill };
        button.Click += (_, _) => Press(member.Id, mute: false);
        button.PreviewMouseWheel += (_, e) =>
        {
            if (row.Member.IsSelf) return;
            _client.SetUserVolume(row.Member, row.Member.Volume + (e.Delta > 0 ? VolumeStep : -VolumeStep));
            UpdateMember(row);
            e.Handled = true;
        };

        _members[member.Id] = row;
        ring.Opacity = _client.IsSpeaking(member.Id) ? 1 : 0;
        return button;
    }

    private void UpdateInPlace()
    {
        if (_micValue is not null && _micIcon is not null)
        {
            _micValue.Text = _client.Muted ? "Muted" : "On";
            _micIcon.Text = _client.Muted ? "" : "";
        }
        if (_deafValue is not null)
            _deafValue.Text = _client.Deafened ? "On" : "Off";

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
        row.Status.Text = notes.Count > 0 ? string.Join("  ·  ", notes) : m.IsSelf ? "You" : "";
        row.Status.Visibility = row.Status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (m.IsSelf)
        {
            row.Value.Text = "";
            return;
        }
        row.Value.Text = m.MutedForMe ? "Muted for you" : $"{m.Volume}%";
        row.Filled!.Width = new GridLength(Math.Max(m.Volume, 0.0001), GridUnitType.Star);
        row.Empty!.Width = new GridLength(Math.Max(200 - m.Volume, 0.0001), GridUnitType.Star);
        row.Fill!.Opacity = m.MutedForMe ? 0.3 : 1;
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
            image.DecodePixelWidth = 72;
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
