using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConsoleOverlay.Audio;
using ConsoleOverlay.Input;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Sound page: a row of device cards at the top (speakers, headphones, microphones; the one in use is white),
/// a big master volume slider, and every app's volume below in two columns.
/// Up / down moves between them, left / right changes a volume (or picks a card), A uses a device or mutes,
/// Y mutes, LT / RT jump between the devices and the volumes.
/// While the page is shown it re-reads Windows audio once a second so changes made elsewhere show up.
/// </summary>
internal sealed class SoundPage : IOverlayPage
{
    private const int Step = 2;
    private const string MasterKey = "master";

    private sealed class VolumeRow
    {
        public required Button Button { get; init; }
        public required TextBlock Value { get; init; }
        public required ColumnDefinition Filled { get; init; }
        public required ColumnDefinition Empty { get; init; }
        public required Border Fill { get; init; }
        public TextBlock? Name { get; init; }
        public AppAudio? App { get; set; } // null for master
        public int ShownVolume { get; set; } = -1; // what is on screen now, so unchanged rows aren't laid out again
        public bool ShownMuted { get; set; }
    }

    private readonly FrameworkElement _resources;
    private readonly AudioService _audio = new();
    private readonly DispatcherTimer _poll;
    private readonly StackPanel _devices = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _deviceStrip = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        CanContentScroll = false,
    };
    private readonly StackPanel _mixer = new();
    private readonly List<Button> _appButtons = [];
    // Made new on every rebuild: a panel can only sit in one grid, and the old grid may still hold the old ones.
    private StackPanel _appsLeft = new();
    private StackPanel _appsRight = new();
    private readonly Dictionary<string, VolumeRow> _volumeRows = [];
    private string _layoutSignature = "";
    private uint _gameProcessId;
    private volatile bool _active;
    private bool _refreshing;
    private bool _refreshAgain;
    private int _generation;

    public SoundPage(FrameworkElement resources)
    {
        _resources = resources;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Refresh();

        // Padding inside the strip so the focus ring around a card isn't cut off.
        _devices.Margin = new Thickness(10);
        _deviceStrip.Content = _devices;
        _deviceStrip.Margin = new Thickness(-10, -10, -10, 14);
        _deviceStrip.SizeChanged += (_, _) => FitDeviceCards();
        var page = new StackPanel();
        page.Children.Add(_deviceStrip);
        page.Children.Add(_mixer);
        Content = page;
    }

    public Panel Content { get; }

    /// <summary>The hint never changes; raised when the device in use (the subtitle) may have.</summary>
    public event Action? HintChanged;

    public string Hint => "A select · Y mute · ← → volume · LT / RT devices or volume · B back to game · LB / RB other pages";

    public string? Subtitle => _audio.Outputs.FirstOrDefault(d => d.IsDefault)?.Name;

    /// <summary>Called when the page comes into view. <paramref name="gameWindow"/> is the window the overlay opened over.</summary>
    public void Activate(IntPtr gameWindow)
    {
        _gameProcessId = 0;
        if (gameWindow != IntPtr.Zero)
            Interop.Native.GetWindowThreadProcessId(gameWindow, out _gameProcessId);
        _layoutSignature = ""; // always rebuild on open
        _active = true;
        // Show what was read last time (or by the prefetch when the overlay opened) at once; fresh values follow.
        if (_audio.HasOutput || _audio.Outputs.Count > 0)
            Apply();
        Refresh();
        _poll.Start();
    }

    /// <summary>
    /// Reads audio in the background as soon as the overlay opens, so the Sound page has it ready when you get
    /// there. Lets go of the devices again right after if the page isn't open by then.
    /// </summary>
    public void Prefetch(IntPtr gameWindow)
    {
        if (_active) return;
        uint pid = 0;
        if (gameWindow != IntPtr.Zero)
            Interop.Native.GetWindowThreadProcessId(gameWindow, out pid);
        _audio.Refresh(pid, () =>
        {
            if (!_active) _audio.Release();
        });
    }

    /// <summary>Called when another page is picked or the overlay hides. Lets go of all audio objects.</summary>
    public void Deactivate()
    {
        _poll.Stop();
        _active = false;
        _refreshing = false;
        _refreshAgain = false;
        _generation++; // a refresh still running is thrown away
        _audio.Release();
        _devices.Children.Clear();
        _mixer.Children.Clear();
        _appsLeft.Children.Clear();
        _appsRight.Children.Clear();
        _appButtons.Clear();
        _volumeRows.Clear();
        _layoutSignature = "";
    }

    /// <summary>The control to focus when the page opens: master volume.</summary>
    public UIElement? FirstFocus() =>
        _volumeRows.TryGetValue(MasterKey, out var master) ? master.Button : _devices.Children.OfType<Button>().FirstOrDefault();

    // ---- Input ------------------------------------------------------------------------------

    /// <summary>Handles a command aimed at the focused control. Returns false to let the overlay do its default.</summary>
    public bool Handle(NavCommand command, IInputElement? focused)
    {
        switch (command)
        {
            case NavCommand.ColumnLeft:
                FocusColumn(left: true);
                return true;
            case NavCommand.ColumnRight:
                FocusColumn(left: false);
                return true;
            case NavCommand.Mute:
                ToggleMute(focused);
                return true;
            case NavCommand.Left or NavCommand.Right when focused is Button { Tag: string key } && _volumeRows.TryGetValue(key, out var row):
                Change(row, HoldStep(command == NavCommand.Right ? 1 : -1));
                return true;
            case NavCommand.Up or NavCommand.Down when focused is Button button:
                return MoveVertically(button, down: command == NavCommand.Down);
        }
        return false;
    }

    /// <summary>
    /// Up / down go devices → master → apps. The apps read like a list wrapped into two columns: down the left
    /// column, then on at the top of the right one, so every app is reachable without left / right (which change
    /// the volume).
    /// </summary>
    private bool MoveVertically(Button focused, bool down)
    {
        _volumeRows.TryGetValue(MasterKey, out var master);
        UIElement? target = null;
        if (IsInside(focused, _devices))
        {
            if (down) target = master?.Button ?? _appButtons.FirstOrDefault();
            else return true;
        }
        else if (master is not null && focused == master.Button)
        {
            target = down ? _appButtons.FirstOrDefault() : DefaultDeviceCard();
        }
        else
        {
            int index = _appButtons.IndexOf(focused);
            if (index < 0) return false;
            if (down) target = index + 1 < _appButtons.Count ? _appButtons[index + 1] : null;
            else target = index > 0 ? _appButtons[index - 1] : master?.Button ?? DefaultDeviceCard();
        }
        if (target is null) return true;
        target.Focus();
        Keyboard.Focus(target);
        (target as FrameworkElement)?.BringIntoView();
        return true;
    }

    private Button? DefaultDeviceCard() =>
        _devices.Children.OfType<Button>().FirstOrDefault(b => b.Tag is string k && k.StartsWith("out:", StringComparison.Ordinal) && IsDefaultRow(b))
        ?? _devices.Children.OfType<Button>().FirstOrDefault();

    /// <summary>Keyboard: ← → change a volume, M mutes, Tab jumps between the columns.</summary>
    public bool HandleKey(Key key, IInputElement? focused) => key switch
    {
        Key.Left => Handle(NavCommand.Left, focused),
        Key.Right => Handle(NavCommand.Right, focused),
        Key.M => Handle(NavCommand.Mute, focused),
        Key.Tab => Handle(focused is DependencyObject d && IsInside(d, _mixer) ? NavCommand.ColumnLeft : NavCommand.ColumnRight, focused),
        _ => false,
    };

    private void FocusColumn(bool left)
    {
        UIElement? target = left ? DefaultDeviceCard() : FirstFocus();
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
        (target as FrameworkElement)?.BringIntoView();
    }

    private static bool IsDefaultRow(Button button) => button.Uid == "default";

    private void ToggleMute(IInputElement? focused)
    {
        if (focused is not Button { Tag: string key })
            return;

        if (_volumeRows.TryGetValue(key, out var row))
        {
            if (row.App is null)
                _audio.SetMasterMuted(!_audio.MasterMuted);
            else
                _audio.SetAppMuted(row.App, !row.App.Muted);
            UpdateRow(row);
        }
        else if (key.StartsWith("out:", StringComparison.Ordinal))
        {
            // On an output device, mute means the speakers.
            _audio.SetMasterMuted(!_audio.MasterMuted);
            if (_volumeRows.TryGetValue(MasterKey, out var master))
                UpdateRow(master);
            UpdateDeviceSubtitles();
        }
        else if (key.StartsWith("in:", StringComparison.Ordinal))
        {
            var device = _audio.Inputs.FirstOrDefault(d => "in:" + d.Id == key);
            if (device is null) return;
            if (!device.IsDefault)
                return; // only the mic in use shows its mute state
            _audio.SetDeviceMuted(device.Id, !device.Muted);
            _layoutSignature = "";
            Refresh();
        }
    }

    /// <summary>
    /// The device cards share the page's width, so none is cut off at the edge. With many devices they keep a
    /// readable width and the row scrolls to the focused one.
    /// </summary>
    private void FitDeviceCards()
    {
        const double Gap = 14, MinWidth = 250, MaxWidth = 420;
        var cards = _devices.Children.OfType<Button>().ToList();
        double available = _deviceStrip.ActualWidth - _devices.Margin.Left - _devices.Margin.Right;
        if (cards.Count == 0 || available <= 0) return;
        double width = Math.Clamp((available - Gap * (cards.Count - 1)) / cards.Count, MinWidth, MaxWidth);
        for (int i = 0; i < cards.Count; i++)
        {
            cards[i].Width = Math.Floor(width);
            cards[i].Margin = new Thickness(0, 0, i < cards.Count - 1 ? Gap : 0, 0);
        }
    }

    // Holding left / right: the volume moves faster the longer it's held, like a console's sliders.
    private int _holdDirection;
    private long _holdStarted, _holdLast;

    private int HoldStep(int direction)
    {
        long now = Environment.TickCount64;
        // While held, repeats arrive every 30-110 ms after a first pause of up to about half a second; a longer gap is a new press.
        if (direction != _holdDirection || now - _holdLast > 600)
            _holdStarted = now;
        _holdDirection = direction;
        _holdLast = now;
        long held = now - _holdStarted;
        int step = held switch
        {
            < 400 => Step,
            < 1000 => 4,
            < 1800 => 6,
            _ => 10,
        };
        return direction * step;
    }

    private void Change(VolumeRow row, int delta)
    {
        if (row.App is null)
            _audio.SetMasterVolume(Math.Clamp(_audio.MasterVolume + delta, 0, 100));
        else
            _audio.SetAppVolume(row.App, Math.Clamp(row.App.Volume + delta, 0, 100));
        UpdateRow(row);
    }

    // ---- Building ---------------------------------------------------------------------------

    /// <summary>Reads Windows audio in the background, then updates the rows. Never blocks the UI.</summary>
    private void Refresh()
    {
        if (_refreshing)
        {
            _refreshAgain = true; // one at a time; run again right after, so a change shows up at once
            return;
        }
        _refreshing = true;
        int generation = _generation;
        _audio.Refresh(_gameProcessId, () => _resources.Dispatcher.BeginInvoke(() =>
        {
            if (generation != _generation || !_active) return;
            _refreshing = false;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Apply();
            if (timer.ElapsedMilliseconds > 20)
                Log.Write($"Sound: updating the rows took {timer.ElapsedMilliseconds} ms.");
            if (_refreshAgain)
            {
                _refreshAgain = false;
                Refresh();
            }
        }));
    }

    private void Apply()
    {

        // Rebuild only when the rows themselves change, so focus stays put while values update in place.
        string signature = string.Join("|",
            _audio.Outputs.Select(d => $"o{d.Id}{d.IsDefault}{d.Name}")
                .Concat(_audio.Inputs.Select(d => $"i{d.Id}{d.IsDefault}{d.Muted}{d.Name}"))
                .Concat(_audio.Apps.Select(a => "a" + a.Key))
                .Append(_audio.HasOutput ? "m" : ""));

        if (signature == _layoutSignature)
        {
            foreach (var row in _volumeRows.Values)
            {
                if (row.App is not null && row.Button.Tag is string key)
                    row.App = _audio.Apps.FirstOrDefault(a => "app:" + a.Key == key) ?? row.App;
                UpdateRow(row);
            }
            UpdateDeviceSubtitles();
            return;
        }
        HintChanged?.Invoke(); // the subtitle names the device in use

        string? focusedKey = (Keyboard.FocusedElement as Button)?.Tag as string;
        bool hadFocus = Keyboard.FocusedElement is DependencyObject d0 && (IsInside(d0, _devices) || IsInside(d0, _mixer));
        bool firstLoad = _volumeRows.Count == 0 && _devices.Children.Count == 0;
        _layoutSignature = signature;
        Build();

        // The page opened before the audio was read; now there is something to focus.
        if (firstLoad && !hadFocus && FirstFocus() is { } first)
        {
            first.Focus();
            Keyboard.Focus(first);
            return;
        }

        if (hadFocus && focusedKey is not null)
        {
            var again = _devices.Children.OfType<Button>().Append(_volumeRows.GetValueOrDefault(MasterKey)?.Button).Concat(_appButtons).OfType<Button>()
                .FirstOrDefault(b => b.Tag as string == focusedKey);
            var target = (UIElement?)again ?? FirstFocus();
            if (target is not null)
            {
                target.Focus();
                Keyboard.Focus(target);
            }
        }
    }

    private void Build()
    {
        _devices.Children.Clear();
        _mixer.Children.Clear();
        _appsLeft.Children.Clear();
        _appsRight.Children.Clear();
        _appButtons.Clear();
        _volumeRows.Clear();
        _deviceSubtitles.Clear();

        foreach (var device in _audio.Outputs)
            _devices.Children.Add(DeviceCard("out:" + device.Id, device, isInput: false));
        foreach (var device in _audio.Inputs)
            _devices.Children.Add(DeviceCard("in:" + device.Id, device, isInput: true));
        if (_devices.Children.Count == 0)
            _devices.Children.Add(Note("No speakers, headphones or microphones found"));
        FitDeviceCards();

        if (!_audio.HasOutput)
        {
            _mixer.Children.Add(Note("Plug in speakers or headphones to change the volume"));
            return;
        }
        AddMaster();

        // Apps: a list wrapped into two columns (left column first), see MoveVertically.
        _appsLeft = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        _appsRight = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        var columns = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_appsRight, 1);
        columns.Children.Add(_appsLeft);
        columns.Children.Add(_appsRight);
        _mixer.Children.Add(columns);

        int leftCount = (_audio.Apps.Count + 1) / 2;
        for (int i = 0; i < _audio.Apps.Count; i++)
        {
            var app = _audio.Apps[i];
            var button = AddAppRow("app:" + app.Key, app);
            (i < leftCount ? _appsLeft : _appsRight).Children.Add(button);
            _appButtons.Add(button);
        }
        if (_audio.Apps.Count == 0)
            _appsLeft.Children.Add(Note("Apps playing sound show up here"));
    }

    private readonly Dictionary<string, (TextBlock Text, AudioDevice Device, bool IsInput)> _deviceSubtitles = [];

    /// <summary>A device card: icon, name, and "In use" (or the mic's state). The one in use is white.</summary>
    private Button DeviceCard(string key, AudioDevice device, bool isInput)
    {
        string glyph = isInput ? "\uE720" // microphone
            : device.Name.Contains("head", StringComparison.OrdinalIgnoreCase) ? "\uE7F6" // headphones
            : device.Name.Contains("monitor", StringComparison.OrdinalIgnoreCase) || device.Name.Contains("display", StringComparison.OrdinalIgnoreCase)
              || device.Name.Contains("hdmi", StringComparison.OrdinalIgnoreCase) || device.Name.Contains("tv", StringComparison.OrdinalIgnoreCase) ? "\uE7F4" // a screen
            : "\uE7F5"; // speakers
        var icon = Ui.Icon(_resources, glyph, 20);
        icon.Margin = new Thickness(0, 0, 14, 0);

        var subtitle = new TextBlock { FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 0) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = device.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(subtitle);

        var layout = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        layout.Children.Add(icon);
        layout.Children.Add(text);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("DeviceCard"),
            Content = layout,
            Tag = key,
            Uid = device.IsDefault ? "default" : "", // the DeviceCard style draws this one white
        };
        _deviceSubtitles[key] = (subtitle, device, isInput);
        SetDeviceSubtitle(subtitle, device, isInput);

        string id = device.Id;
        button.Click += (_, _) =>
        {
            if (device.IsDefault)
            {
                ToggleMute(button);
                return;
            }
            _audio.SetDefaultDevice(id);
            _layoutSignature = "";
            Refresh();
        };
        return button;
    }

    private void SetDeviceSubtitle(TextBlock text, AudioDevice device, bool isInput)
    {
        if (isInput)
            text.Text = !device.IsDefault ? "A to use" : device.Muted ? "Muted · Y to unmute" : "On · Y to mute";
        else
            text.Text = !device.IsDefault ? "A to use" : _audio.MasterMuted ? "In use · muted" : "In use";
    }

    private void UpdateDeviceSubtitles()
    {
        foreach (var (text, device, isInput) in _deviceSubtitles.Values)
            SetDeviceSubtitle(text, device, isInput);
    }

    /// <summary>Master volume: a label and value, then a thick white slider with a speaker icon in it.</summary>
    private void AddMaster()
    {
        var name = new TextBlock { Text = "Master", FontSize = 20, FontWeight = FontWeights.Bold };
        var value = new TextBlock { FontSize = 20, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        var label = new Grid { Margin = new Thickness(2, 0, 2, 10) };
        label.Children.Add(name);
        label.Children.Add(value);
        _mixer.Children.Add(label);

        var filled = new ColumnDefinition();
        var empty = new ColumnDefinition();
        var bar = new Grid { Height = 64 };
        bar.ColumnDefinitions.Add(filled);
        bar.ColumnDefinitions.Add(empty);
        var track = new Border { Background = (Brush)_resources.FindResource("TrackBrush"), CornerRadius = new CornerRadius(32) };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { Background = (Brush)_resources.FindResource("SelectedFillBrush"), CornerRadius = new CornerRadius(32), MinWidth = 64 };
        var speaker = Ui.Icon(_resources, "\uE767", 20);
        speaker.Foreground = (Brush)_resources.FindResource("SelectedTextBrush");
        speaker.Margin = new Thickness(24, 0, 0, 0);
        speaker.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumnSpan(speaker, 2);
        bar.Children.Add(track);
        bar.Children.Add(fill);
        bar.Children.Add(speaker);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("SliderButton"),
            Content = bar,
            Tag = MasterKey,
        };
        var row = new VolumeRow { Button = button, Value = value, Filled = filled, Empty = empty, Fill = fill, App = null };
        button.Click += (_, _) => ToggleMute(button); // A (or a click) mutes too
        button.PreviewMouseWheel += (_, e) =>
        {
            Change(row, e.Delta > 0 ? Step : -Step);
            e.Handled = true;
        };
        _volumeRows[MasterKey] = row;
        _mixer.Children.Add(button);
        UpdateRow(row);
    }

    /// <summary>An app: a colored square with its first letter, its name and volume, and a slim bar.</summary>
    private Button AddAppRow(string key, AppAudio app)
    {
        var badge = AppBadge(app);

        var name = new TextBlock { Text = app.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var value = new TextBlock { FontSize = 15, Margin = new Thickness(12, 0, 0, 0) };
        var text = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        text.Children.Add(value);
        text.Children.Add(name);

        var filled = new ColumnDefinition();
        var empty = new ColumnDefinition();
        var bar = new Grid { Height = 8, Margin = new Thickness(0, 10, 0, 0) };
        bar.ColumnDefinitions.Add(filled);
        bar.ColumnDefinitions.Add(empty);
        var track = new Border { Background = (Brush)_resources.FindResource("TrackBrush"), CornerRadius = new CornerRadius(4) };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { Background = (Brush)_resources.FindResource("TextBrush"), CornerRadius = new CornerRadius(4) };
        bar.Children.Add(track);
        bar.Children.Add(fill);

        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(text);
        right.Children.Add(bar);
        var layout = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        layout.Children.Add(badge);
        layout.Children.Add(right);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = layout,
            Tag = key,
            Padding = new Thickness(14, 12, 18, 12),
        };

        var row = new VolumeRow { Button = button, Value = value, Filled = filled, Empty = empty, Fill = fill, App = app, Name = name };
        button.Click += (_, _) => ToggleMute(button); // A (or a click) mutes too
        button.PreviewMouseWheel += (_, e) =>
        {
            Change(row, e.Delta > 0 ? Step : -Step);
            e.Handled = true;
        };

        _volumeRows[key] = row;
        UpdateRow(row);
        return button;
    }

    /// <summary>
    /// The app's own icon (big and sharp, from its program file). System sounds get a speaker; an app whose program
    /// can't be read (one running as administrator) gets its first letter on a color.
    /// </summary>
    private FrameworkElement AppBadge(AppAudio app)
    {
        var margin = new Thickness(0, 0, 16, 0);
        if (app.ProcessId != 0 && Interop.ShellIcons.ExePath(app.ProcessId) is { } path
            && Interop.ShellIcons.ForFile(path, 64) is { } icon)
        {
            var image = new Image { Source = icon, Width = 44, Height = 44, Margin = margin };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        var badge = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = AppBrush(app.Name),
            Margin = margin,
        };
        if (app.ProcessId == 0)
        {
            var speaker = Ui.Icon(_resources, "\uE767", 20);
            speaker.Foreground = Brushes.White;
            speaker.HorizontalAlignment = HorizontalAlignment.Center;
            badge.Child = speaker;
        }
        else
        {
            badge.Child = new TextBlock
            {
                Text = app.Name.Length > 0 ? char.ToUpper(app.Name[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "?",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        return badge;
    }

    private static Brush AppBrush(string name)
    {
        Color[][] pairs =
        [
            [Color.FromRgb(0xFF, 0x9A, 0x5A), Color.FromRgb(0x6B, 0x24, 0x52)],
            [Color.FromRgb(0x74, 0x80, 0xFF), Color.FromRgb(0x3B, 0x3F, 0xB8)],
            [Color.FromRgb(0x3E, 0xE0, 0x8F), Color.FromRgb(0x13, 0x7A, 0x4A)],
            [Color.FromRgb(0x2F, 0x6B, 0xFF), Color.FromRgb(0x1B, 0x2C, 0x6B)],
            [Color.FromRgb(0xF6, 0xC4, 0x53), Color.FromRgb(0xB9, 0x83, 0x16)],
            [Color.FromRgb(0xFF, 0x7A, 0xA8), Color.FromRgb(0x7A, 0x18, 0x40)],
        ];
        int hash = 0;
        foreach (char c in name) hash = hash * 31 + char.ToLowerInvariant(c);
        var pair = pairs[Math.Abs(hash % pairs.Length)];
        var brush = new LinearGradientBrush(pair[0], pair[1], new Point(0.2, 0), new Point(0.8, 1));
        brush.Freeze();
        return brush;
    }

    private void UpdateRow(VolumeRow row)
    {
        int volume = row.App?.Volume ?? _audio.MasterVolume;
        bool muted = row.App?.Muted ?? _audio.MasterMuted;
        // The page re-reads audio every second; only touch the row (and cause a layout pass) when something changed.
        if (volume == row.ShownVolume && muted == row.ShownMuted) return;
        row.ShownVolume = volume;
        row.ShownMuted = muted;
        row.Value.Text = muted ? "Muted" : volume.ToString(System.Globalization.CultureInfo.CurrentCulture);
        row.Filled.Width = new GridLength(Math.Max(volume, 0.0001), GridUnitType.Star);
        row.Empty.Width = new GridLength(Math.Max(100 - volume, 0.0001), GridUnitType.Star);
        row.Fill.Opacity = muted ? 0.35 : 1;
        if (row.Name is not null) row.Name.Opacity = muted ? 0.6 : 1;
    }

    private TextBlock Section(string text, bool first) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = (Style)_resources.FindResource("SectionLabel"),
        Margin = new Thickness(2, first ? 0 : 18, 0, 8),
    };

    private TextBlock Note(string text) => new()
    {
        Text = text,
        Style = (Style)_resources.FindResource("RowHint"),
        Margin = new Thickness(16, 4, 0, 10),
    };

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == container)
                return true;
        return false;
    }
}
