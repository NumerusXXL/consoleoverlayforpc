using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConsoleOverlay.Input;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// A keyboard you type on with the controller, for search boxes, chat and sign-in forms. D-pad moves, A types the
/// key, X deletes, Y is a space, LT is Shift, RT is Enter, B closes it. English and Russian letters.
/// </summary>
internal sealed class OnScreenKeyboard
{
    private const double KeySize = 50;

    private static readonly string[] English = ["1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,.?"];
    private static readonly string[] EnglishShift = ["!@#$%^&*()", "QWERTYUIOP", "ASDFGHJKL:", "ZXCVBNM-_/"];
    private static readonly string[] Russian = ["1234567890", "йцукенгшщзх", "фывапролджэ", "ячсмитьбю.,"];
    private static readonly string[] RussianShift = ["!\"№;%:?*()", "ЙЦУКЕНГШЩЗХ", "ФЫВАПРОЛДЖЭ", "ЯЧСМИТЬБЮ!?"];

    private readonly FrameworkElement _resources;
    private readonly StackPanel _rows = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly List<List<Button>> _keys = [];
    private bool _shift;
    private bool _russian;
    private string _enterLabel = "Done";

    /// <summary>A key was typed (one character, or a space).</summary>
    public event Action<string>? Typed;
    public event Action? Backspace;
    /// <summary>Enter / Done / Search / Send.</summary>
    public event Action? Enter;
    /// <summary>B: put the keyboard away.</summary>
    public event Action? Closed;

    public OnScreenKeyboard(FrameworkElement resources)
    {
        _resources = resources;
        View = new Border
        {
            Child = _rows,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(26),
            Background = (Brush)resources.FindResource("PaneBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        KeyboardNavigation.SetDirectionalNavigation(_rows, KeyboardNavigationMode.Contained);
        _russian = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru"
            || InputLanguageManager.Current?.CurrentInputLanguage?.TwoLetterISOLanguageName == "ru";
        Build();
    }

    public FrameworkElement View { get; }

    /// <summary>Lower keys, for a keyboard that floats over something you still want to see (the chat).</summary>
    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            Build();
        }
    }

    private bool _compact;

    /// <summary>What Enter says: "Search", "Send", "Done".</summary>
    public string EnterLabel
    {
        get => _enterLabel;
        set
        {
            if (_enterLabel == value) return;
            _enterLabel = value;
            Build();
        }
    }

    public string Hint => $"A type · X delete · Y space · LT / RT shift or {EnterLabel.ToLowerInvariant()} · B close keyboard";

    public bool Contains(IInputElement? element) => element is DependencyObject d && Ui.IsInside(d, _rows);

    public void FocusFirst()
    {
        var key = _keys.Count > 1 && _keys[1].Count > 0 ? _keys[1][0] : _keys.SelectMany(r => r).FirstOrDefault();
        if (key is null) return;
        key.Focus();
        Keyboard.Focus(key);
    }

    /// <summary>Controller buttons while a key has focus. Directions and A use the overlay's normal handling.</summary>
    public bool Handle(NavCommand command)
    {
        switch (command)
        {
            case NavCommand.Secondary: Backspace?.Invoke(); return true;
            case NavCommand.Mute: Typed?.Invoke(" "); return true;
            case NavCommand.ColumnLeft: ToggleShift(); return true;
            case NavCommand.ColumnRight: Enter?.Invoke(); return true;
            case NavCommand.Back: Closed?.Invoke(); return true;
        }
        return false;
    }

    private void ToggleShift()
    {
        _shift = !_shift;
        Build();
    }

    private void Build()
    {
        // Keep focus on the same key across a rebuild (Shift, language).
        (int Row, int Column)? focused = null;
        for (int r = 0; r < _keys.Count; r++)
            for (int c = 0; c < _keys[r].Count; c++)
                if (_keys[r][c].IsKeyboardFocused) focused = (r, c);

        _rows.Children.Clear();
        _keys.Clear();
        var layout = _russian ? (_shift ? RussianShift : Russian) : (_shift ? EnglishShift : English);
        foreach (var line in layout)
        {
            var row = new List<Button>();
            foreach (char ch in line)
            {
                string text = ch.ToString();
                row.Add(Key(text, KeySize, () =>
                {
                    Typed?.Invoke(text);
                    if (_shift && char.IsLetter(text[0])) ToggleShift(); // one capital, like a phone keyboard
                }));
            }
            AddRow(row);
        }
        AddRow(
        [
            Key(_shift ? "⇧ SHIFT" : "⇧ Shift", KeySize * 2, ToggleShift, selected: _shift),
            Key(_russian ? "RU" : "EN", KeySize * 1.2, () =>
            {
                _russian = !_russian;
                Build();
            }),
            Key("Space", KeySize * 4.6, () => Typed?.Invoke(" ")),
            Key("⌫", KeySize * 1.4, () => Backspace?.Invoke()),
            Key(_enterLabel, KeySize * 2.2, () => Enter?.Invoke(), selected: true),
        ]);

        if (focused is { } f && f.Row < _keys.Count)
        {
            var row = _keys[f.Row];
            var key = row[Math.Min(f.Column, row.Count - 1)];
            key.Focus();
            Keyboard.Focus(key);
        }
    }

    private void AddRow(List<Button> keys)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var key in keys) panel.Children.Add(key);
        _rows.Children.Add(panel);
        _keys.Add(keys);
    }

    private Button Key(string text, double width, Action onPress, bool selected = false)
    {
        var button = new Button
        {
            Style = (Style)_resources.FindResource("RowButton"),
            Width = width + (width > KeySize ? (width / KeySize - 1) * 6 : 0), // wide keys line up with the gaps
            Height = _compact ? 40 : KeySize,
            Margin = new Thickness(3),
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Content = new TextBlock
            {
                Text = text,
                FontSize = text.Length == 1 ? 20 : 15,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (selected) button.Uid = "selected"; // a white key, like the Enter key on a console keyboard
        button.Click += (_, _) => onPress();
        return button;
    }
}
