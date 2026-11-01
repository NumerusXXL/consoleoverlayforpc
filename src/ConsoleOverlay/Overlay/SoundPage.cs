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
/// The Sound page: output and input devices on the left, master volume and a mixer for every app on the right.
/// Up / down picks, left / right changes a volume, A picks a device or mutes, Y mutes, LT / RT jump between columns.
/// While the page is shown it re-reads Windows audio once a second so changes made elsewhere show up.
/// </summary>
internal sealed class SoundPage
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
        public AppAudio? App { get; set; } // null for master
    }

    private readonly FrameworkElement _resources;
    private readonly AudioService _audio = new();
    private readonly DispatcherTimer _poll;
    private readonly StackPanel _devices = new();
    private readonly StackPanel _mixer = new();
    private readonly Dictionary<string, VolumeRow> _volumeRows = [];
    private string _layoutSignature = "";
    private uint _gameProcessId;

    public SoundPage(FrameworkElement resources)
    {
        _resources = resources;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Refresh();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_mixer, 2);
        grid.Children.Add(_devices);
        grid.Children.Add(_mixer);
        Content = grid;
    }

    public Panel Content { get; }

    public const string Hint = "A select · Y mute · ← → volume · LT / RT switch column · B back to game · LB / RB other pages";

    /// <summary>Called when the page comes into view. <paramref name="gameWindow"/> is the window the overlay opened over.</summary>
    public void Activate(IntPtr gameWindow)
    {
        _gameProcessId = 0;
        if (gameWindow != IntPtr.Zero)
            Interop.Native.GetWindowThreadProcessId(gameWindow, out _gameProcessId);
        _layoutSignature = ""; // always rebuild on open
        Refresh();
        _poll.Start();
    }

    /// <summary>Called when another page is picked or the overlay hides. Lets go of all audio objects.</summary>
    public void Deactivate()
    {
        _poll.Stop();
        _audio.Release();
        _devices.Children.Clear();
        _mixer.Children.Clear();
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
                Change(row, command == NavCommand.Right ? Step : -Step);
                return true;
        }
        return false;
    }

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
        UIElement? target = left
            ? _devices.Children.OfType<Button>().FirstOrDefault(b => b.Tag is string k && k.StartsWith("out:", StringComparison.Ordinal) && IsDefaultRow(b))
              ?? _devices.Children.OfType<Button>().FirstOrDefault()
            : FirstFocus();
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

    private void Change(VolumeRow row, int delta)
    {
        if (row.App is null)
            _audio.SetMasterVolume(Math.Clamp(_audio.MasterVolume + delta, 0, 100));
        else
            _audio.SetAppVolume(row.App, Math.Clamp(row.App.Volume + delta, 0, 100));
        UpdateRow(row);
    }

    // ---- Building ---------------------------------------------------------------------------

    private void Refresh()
    {
        _audio.Refresh(_gameProcessId);

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
            return;
        }

        string? focusedKey = (Keyboard.FocusedElement as Button)?.Tag as string;
        bool hadFocus = Keyboard.FocusedElement is DependencyObject d0 && (IsInside(d0, _devices) || IsInside(d0, _mixer));
        _layoutSignature = signature;
        Build();

        if (hadFocus && focusedKey is not null)
        {
            var again = _devices.Children.OfType<Button>().Concat(_mixer.Children.OfType<Button>())
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
        _volumeRows.Clear();

        _devices.Children.Add(Section("Output", first: true));
        if (_audio.Outputs.Count == 0)
            _devices.Children.Add(Note("No speakers or headphones found"));
        foreach (var device in _audio.Outputs)
            _devices.Children.Add(DeviceRow("out:" + device.Id, device, isInput: false));

        _devices.Children.Add(Section("Input", first: false));
        if (_audio.Inputs.Count == 0)
            _devices.Children.Add(Note("No microphones found"));
        foreach (var device in _audio.Inputs)
            _devices.Children.Add(DeviceRow("in:" + device.Id, device, isInput: true));

        _mixer.Children.Add(Section("Volume", first: true));
        if (!_audio.HasOutput)
        {
            _mixer.Children.Add(Note("Plug in speakers or headphones to change the volume"));
            return;
        }
        AddVolumeRow(MasterKey, "Master", null, large: true);
        foreach (var app in _audio.Apps)
            AddVolumeRow("app:" + app.Key, app.Name, app, large: false);
        if (_audio.Apps.Count == 0)
            _mixer.Children.Add(Note("Apps playing sound show up here"));
    }

    private Button DeviceRow(string key, AudioDevice device, bool isInput)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock { Text = device.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        // A check on the device in use; a crossed mic when that microphone is muted.
        string? mark = device.IsDefault ? (isInput && device.Muted ? "" : "") : null;
        if (mark is not null)
        {
            var icon = new TextBlock
            {
                Text = mark,
                FontFamily = (FontFamily)_resources.FindResource("IconFont"),
                FontSize = 14,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = isInput && device.Muted ? "Muted" : "In use",
            };
            Grid.SetColumn(icon, 1);
            grid.Children.Add(icon);
        }

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = grid,
            Tag = key,
            Uid = device.IsDefault ? "default" : "",
            Padding = new Thickness(14, 12, 14, 12),
            ToolTip = device.Name,
        };

        string id = device.Id;
        button.Click += (_, _) =>
        {
            if (device.IsDefault)
            {
                if (isInput) ToggleMute(button);
                return;
            }
            _audio.SetDefaultDevice(id);
            _layoutSignature = "";
            Refresh();
        };
        return button;
    }

    private void AddVolumeRow(string key, string name, AppAudio? app, bool large)
    {
        var text = new Grid();
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        text.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = large ? 15 : 14,
            FontWeight = large ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var value = new TextBlock { FontSize = large ? 15 : 14, FontWeight = large ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(12, 0, 0, 0) };
        Grid.SetColumn(value, 1);
        text.Children.Add(value);

        double barHeight = large ? 6 : 4;
        var filled = new ColumnDefinition();
        var empty = new ColumnDefinition();
        var bar = new Grid { Height = barHeight, Margin = new Thickness(0, large ? 10 : 8, 0, 0) };
        bar.ColumnDefinitions.Add(filled);
        bar.ColumnDefinitions.Add(empty);
        var track = new Border { Background = (Brush)_resources.FindResource("TrackBrush"), CornerRadius = new CornerRadius(barHeight / 2) };
        Grid.SetColumnSpan(track, 2);
        var fill = new Border
        {
            Background = (Brush)_resources.FindResource(large ? "TextBrush" : "SubtleTextBrush"),
            CornerRadius = new CornerRadius(barHeight / 2),
        };
        bar.Children.Add(track);
        bar.Children.Add(fill);

        var content = new StackPanel();
        content.Children.Add(text);
        content.Children.Add(bar);

        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Content = content,
            Tag = key,
            Padding = large ? new Thickness(16, 14, 16, 14) : new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, large ? 8 : 2),
            ToolTip = name,
        };

        var row = new VolumeRow { Button = button, Value = value, Filled = filled, Empty = empty, Fill = fill, App = app };
        button.Click += (_, _) => ToggleMute(button); // A (or a click) mutes too
        button.PreviewMouseWheel += (_, e) =>
        {
            Change(row, e.Delta > 0 ? Step : -Step);
            e.Handled = true;
        };

        _volumeRows[key] = row;
        _mixer.Children.Add(button);
        UpdateRow(row);
    }

    private void UpdateRow(VolumeRow row)
    {
        int volume = row.App?.Volume ?? _audio.MasterVolume;
        bool muted = row.App?.Muted ?? _audio.MasterMuted;
        row.Value.Text = muted ? "Muted" : volume.ToString(System.Globalization.CultureInfo.CurrentCulture);
        row.Filled.Width = new GridLength(Math.Max(volume, 0.0001), GridUnitType.Star);
        row.Empty.Width = new GridLength(Math.Max(100 - volume, 0.0001), GridUnitType.Star);
        row.Fill.Opacity = muted ? 0.3 : 1;
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
