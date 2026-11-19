using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ConsoleOverlay.Input;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Settings page: a list of sections on the left (so it can grow), the picked section on the right.
/// Moving through the list shows each section right away; A or → goes into it, B or ← comes back.
/// On a choice, ← → (or A) changes it. Every change is saved and applied immediately.
/// </summary>
internal sealed class SettingsPage : IOverlayPage
{
    public const string ReleasesUrl = "https://github.com/NumerusXXL/consoleoverlayforpc/releases";
    private const string DefaultHotkey = "Ctrl+Alt+G";

    private sealed record Section(string Id, string Name, string Icon);

    private static readonly Section[] Sections =
    [
        new("controls", "Controls", ""),
        new("look", "Appearance", ""),
        new("startup", "Startup", ""),
        new("games", "Games", ""),
        new("discord", "Discord", ""),
        new("about", "About", ""),
    ];

    private readonly FrameworkElement _resources;
    private readonly Grid _root = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _detail = new();
    private readonly List<(TextBlock Text, Func<string> Value)> _values = [];
    private readonly Dictionary<string, Action<int>> _cyclers = [];
    private readonly Dictionary<string, Border> _sectionMarks = [];
    private ISettingsHost? _host;
    private bool _active;
    private string _section = "controls";
    private string _signature = "";
    private string? _hint;
    private string? _builtSection;

    private string? _message;
    private bool _messageIsError;
    private string? _gameName;

    private bool _capturingKey;
    private string _keyLive = "";
    private bool _capturingCombo;
    private bool _comboCancelled;
    private string _comboLive = "";

    private string? _editing; // "id" or "secret"
    private Control? _editBox;

    /// <summary>Close the overlay (without going back to the game), then run this.</summary>
    public event Action<Action>? CloseThen;
    public event Action? HintChanged;

    public SettingsPage(FrameworkElement resources)
    {
        _resources = resources;
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_detail, 2);
        _root.Children.Add(_list);
        _root.Children.Add(_detail);
        // Up / down stay inside a column; ← → and LT / RT move between them.
        KeyboardNavigation.SetDirectionalNavigation(_list, KeyboardNavigationMode.Contained);
        KeyboardNavigation.SetDirectionalNavigation(_detail, KeyboardNavigationMode.Contained);
        _root.PreviewGotKeyboardFocus += (_, _) => _root.Dispatcher.BeginInvoke(UpdateHint);
    }

    public Panel Content => _root;

    public string Hint => _hint ??= ComputeHint();

    private AppSettings Settings => _host?.Settings ?? new AppSettings();

    public void SetHost(ISettingsHost host) => _host = host;

    /// <summary>Settings changed (from this page, the tray or the file): show the new values.</summary>
    public void UpdateSettings()
    {
        if (_active) Refresh();
    }

    // ---- IOverlayPage -----------------------------------------------------------------------

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        _message = null;
        _editing = null;
        _gameName = GameName(gameWindow);
        BuildList();
        _signature = "";
        Refresh();
        UpdateHint();
    }

    public void Deactivate()
    {
        _active = false;
        if (_capturingKey)
        {
            _capturingKey = false;
            _host?.ResumeHotkey();
        }
        if (_capturingCombo)
        {
            _comboCancelled = true;
            _host?.CancelComboCapture();
        }
        _editing = null;
        _editBox = null;
        _list.Children.Clear();
        _detail.Children.Clear();
        _values.Clear();
        _cyclers.Clear();
        _sectionMarks.Clear();
        _signature = "";
        _builtSection = null;
    }

    public UIElement? FirstFocus() => SectionButton(_section);

    /// <summary>Opens a section, for example from the Discord page.</summary>
    public void ShowSection(string id, bool focusDetail)
    {
        if (!_active) return;
        SelectSection(id);
        if (focusDetail) FocusDetail();
        else Focus(SectionButton(id));
    }

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        // Recording a combo: the controller is busy with that; the keyboard can only cancel.
        if (_capturingCombo)
        {
            if (command == NavCommand.Back)
            {
                _comboCancelled = true;
                _host?.CancelComboCapture();
            }
            return true;
        }
        if (_capturingKey)
        {
            if (command == NavCommand.Back) EndKeyCapture(null);
            return true;
        }

        bool inDetail = focused is DependencyObject d && Ui.IsInside(d, _detail);
        string? key = (focused as FrameworkElement)?.Tag as string;

        if (_editing is not null && inDetail)
        {
            switch (command)
            {
                case NavCommand.Secondary:
                    PasteIntoField();
                    return true;
                case NavCommand.Accept when focused == _editBox:
                    SaveField();
                    return true;
                case NavCommand.Back:
                    CancelField();
                    return true;
                case NavCommand.Left or NavCommand.Right:
                    return true;
            }
        }

        switch (command)
        {
            case NavCommand.Back when inDetail:
            case NavCommand.ColumnLeft:
                Focus(SectionButton(_section));
                return true;
            case NavCommand.ColumnRight:
                FocusDetail();
                return true;
            case NavCommand.Left when inDetail:
                if (key is not null && _cyclers.TryGetValue(key, out var back)) back(-1);
                else Focus(SectionButton(_section));
                return true;
            case NavCommand.Right when inDetail:
                if (key is not null && _cyclers.TryGetValue(key, out var forward)) forward(+1);
                return true;
            case NavCommand.Right:
                FocusDetail();
                return true;
            case NavCommand.Left:
                return true;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused)
    {
        if (key != Key.Tab) return false;
        bool inDetail = focused is DependencyObject d && Ui.IsInside(d, _detail);
        return Handle(inDetail ? NavCommand.ColumnLeft : NavCommand.ColumnRight, focused);
    }

    /// <summary>While recording a shortcut every key lands here, Ctrl / Alt / Win included.</summary>
    public bool HandleRawKey(KeyEventArgs e)
    {
        if (!_capturingKey) return false;
        e.Handled = true;
        if (e.IsRepeat) return true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        if (key is Key.ImeProcessed or Key.DeadCharProcessed or Key.None)
            return true;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            _keyLive = modifiers == ModifierKeys.None ? "Press keys…" : ModifierText(modifiers) + "+…";
            RefreshValues();
            return true;
        }

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            EndKeyCapture(null);
            return true;
        }

        bool standalone = key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll;
        if (!standalone && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            ShowMessage("Add Ctrl, Alt or Win to it (F-keys work alone), so typing in a game doesn't open the overlay.", error: true);
            _keyLive = "Press keys…";
            RefreshValues();
            return true;
        }

        EndKeyCapture(HotkeyService.Format(modifiers, key));
        return true;
    }

    // ---- Hints ------------------------------------------------------------------------------

    private string ComputeHint()
    {
        if (_capturingCombo) return "Hold the new buttons together for a second · wait 10 seconds or press Esc to cancel";
        if (_capturingKey) return "Press the new shortcut, for example Ctrl+Alt+G · Esc cancels";
        bool inDetail = Keyboard.FocusedElement is DependencyObject d && Ui.IsInside(d, _detail);
        if (_editing is not null && inDetail) return "Type, or X paste · A save · B cancel";
        if (inDetail) return "A select · ← → change · B back to sections · LB / RB other pages";
        return "A open · B back to game · LB / RB other pages";
    }

    private void UpdateHint()
    {
        var hint = ComputeHint();
        if (hint == _hint) return;
        _hint = hint;
        HintChanged?.Invoke();
    }

    // ---- Section list -----------------------------------------------------------------------

    private void BuildList()
    {
        _list.Children.Clear();
        _sectionMarks.Clear();
        foreach (var section in Sections)
        {
            var mark = new Border
            {
                Width = 3,
                Height = 18,
                CornerRadius = new CornerRadius(1.5),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0,
            };
            mark.SetResourceReference(Border.BackgroundProperty, "AccentBrush"); // follows accent changes
            _sectionMarks[section.Id] = mark;

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(mark);
            var icon = Ui.Icon(_resources, section.Icon, 16);
            icon.Margin = new Thickness(0, 0, 12, 0);
            content.Children.Add(icon);
            content.Children.Add(new TextBlock { Text = section.Name, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });

            var button = new Button
            {
                Style = (Style)_resources.FindResource("RowButton"),
                Content = content,
                Tag = "sec:" + section.Id,
                Padding = new Thickness(10, 12, 14, 12),
            };
            string id = section.Id;
            button.GotKeyboardFocus += (_, _) => SelectSection(id);
            button.Click += (_, _) =>
            {
                SelectSection(id);
                FocusDetail();
            };
            _list.Children.Add(button);
        }
        UpdateSectionMarks();
    }

    private Button? SectionButton(string id) =>
        _list.Children.OfType<Button>().FirstOrDefault(b => b.Tag as string == "sec:" + id);

    private void SelectSection(string id)
    {
        if (id == _section && _signature.Length > 0) return;
        _section = id;
        _message = null;
        _editing = null;
        UpdateSectionMarks();
        Refresh();
    }

    private void UpdateSectionMarks()
    {
        foreach (var (id, mark) in _sectionMarks)
            Ui.Fade(mark, id == _section ? 1 : 0, 160);
    }

    // ---- Detail -----------------------------------------------------------------------------

    private string Signature()
    {
        var s = Settings;
        return string.Join("|", _section, _editing, _message, _messageIsError, _gameName,
            string.Join(",", s.ExclusiveFullscreenExcludedGames),
            s.DiscordClientId.Length > 0, s.DiscordClientSecret.Length > 0);
    }

    /// <summary>Rebuilds the section only when its rows change, so focus and the focus ring stay put otherwise.</summary>
    private void Refresh()
    {
        if (!_active) return;
        var signature = Signature();
        if (signature == _signature)
        {
            RefreshValues();
            return;
        }
        _signature = signature;

        string? focusedKey = (Keyboard.FocusedElement as FrameworkElement)?.Tag as string;
        bool hadFocus = Keyboard.FocusedElement is DependencyObject d && Ui.IsInside(d, _detail);
        BuildDetail();

        if (_editBox is not null && _editing is not null)
            Focus(_editBox);
        else if (hadFocus)
            Focus(DetailItems().FirstOrDefault(c => c.Tag as string == focusedKey) ?? DetailItems().FirstOrDefault());
        UpdateHint();
    }

    private void RefreshValues()
    {
        foreach (var (text, value) in _values)
            text.Text = value();
    }

    private IEnumerable<Control> DetailItems() =>
        _detail.Children.OfType<Control>().Where(c => c.Focusable && c.IsEnabled);

    private void FocusDetail() => Focus(_editBox ?? DetailItems().FirstOrDefault());

    private static void Focus(UIElement? target)
    {
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
        (target as FrameworkElement)?.BringIntoView();
    }

    private void BuildDetail()
    {
        _detail.Children.Clear();
        _values.Clear();
        _cyclers.Clear();
        _editBox = null;

        if (_message is not null)
        {
            _detail.Children.Add(new TextBlock
            {
                Text = _message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)_resources.FindResource(_messageIsError ? "DangerTextBrush" : "TextBrush"),
                Margin = new Thickness(2, 0, 0, 14),
            });
        }

        switch (_section)
        {
            case "controls": BuildControls(); break;
            case "look": BuildLook(); break;
            case "startup": BuildStartup(); break;
            case "games": BuildGames(); break;
            case "discord": if (_editing is null) BuildDiscord(); else BuildField(); break;
            default: BuildAbout(); break;
        }

        // A new section rises in; a rebuild of the same one (a message, a removed game) doesn't.
        if (_section != _builtSection)
            Ui.Stagger(_detail.Children.OfType<FrameworkElement>(), 22);
        _builtSection = _section;
    }

    private void ShowMessage(string? text, bool error = false)
    {
        _message = text;
        _messageIsError = error;
        Refresh();
    }

    // ---- Controls ---------------------------------------------------------------------------

    private void BuildControls()
    {
        Header("Open the overlay", first: true);
        Row("combo", "With the controller", "Hold these buttons together. A records new ones.",
            () => _capturingCombo ? _comboLive : ButtonNames.Friendly(Settings.ParseCombo(out _)), StartComboCapture);
        Row("hotkey", "With the keyboard", "Works from any app. A records a new shortcut.",
            () => _capturingKey ? _keyLive : Settings.Hotkey, StartKeyCapture);
        Row("controls-reset", "Reset to View + Menu and Ctrl+Alt+G", null, null, ResetControls);
        Note("The Xbox button works too. Turn it off in the Xbox Game Bar settings (and Steam, if you use it) first, " +
             "so they don't open at the same time.");
    }

    private void StartComboCapture()
    {
        if (_host is null || _capturingCombo) return;
        _capturingCombo = true;
        _comboCancelled = false;
        _comboLive = "Hold the new buttons…";
        _message = null;
        _signature = "";
        Refresh();
        UpdateHint();

        _host.CaptureCombo(
            held =>
            {
                _comboLive = held == ControllerButtons.None ? "Hold the new buttons…" : $"Holding {ButtonNames.Friendly(held)}…";
                RefreshValues();
            },
            result =>
            {
                _capturingCombo = false;
                if (!_active) return;
                UpdateHint();
                if (result is not { } combo)
                {
                    ShowMessage(_comboCancelled ? null : "Nothing changed: no buttons were held.");
                    return;
                }

                var buttons = ButtonNames.Split(combo).ToList();
                if (buttons.Count == 1 && combo != ControllerButtons.Guide)
                {
                    ShowMessage("Use two buttons or more (or the Xbox button alone), so a normal press in a game doesn't open the overlay.", error: true);
                    return;
                }

                Settings.ControllerCombo = ButtonNames.ToSettingNames(combo);
                _host.Save();
                ShowMessage($"Saved. Hold {ButtonNames.Friendly(combo)} to open the overlay.");
            });
    }

    private void StartKeyCapture()
    {
        if (_host is null || _capturingKey) return;
        _capturingKey = true;
        _keyLive = "Press keys…";
        _host.SuspendHotkey();
        _message = null;
        _signature = "";
        Refresh();
        UpdateHint();
    }

    private void EndKeyCapture(string? hotkey)
    {
        _capturingKey = false;
        UpdateHint();
        if (_host is null) return;
        if (hotkey is null)
        {
            _host.ResumeHotkey();
            ShowMessage(null);
            return;
        }
        if (_host.TrySetHotkey(hotkey, out var error))
            ShowMessage($"Saved. {hotkey} now opens the overlay.");
        else
            ShowMessage($"{error} Pick another one.", error: true);
    }

    private void ResetControls()
    {
        if (_host is null) return;
        Settings.ControllerCombo = ["Back", "Start"];
        _host.Save();
        if (Settings.Hotkey != DefaultHotkey && !_host.TrySetHotkey(DefaultHotkey, out var error))
        {
            ShowMessage($"The controller is back to View + Menu, but {error}", error: true);
            return;
        }
        ShowMessage("Back to View + Menu and Ctrl+Alt+G.");
    }

    /// <summary>"Ctrl+Alt" for the modifiers held so far.</summary>
    private static string ModifierText(ModifierKeys modifiers) => HotkeyService.Format(modifiers, Key.None)[..^"+None".Length];

    // ---- Appearance -------------------------------------------------------------------------

    private void BuildLook()
    {
        Header("Look", first: true);
        Choice("accent", "Accent color", "The focus ring and the current page in the dock",
            Appearance.Accents.Select(a => (a.Value, a.Label)).ToArray(), () => Settings.AccentColor, v => Settings.AccentColor = v);
        Choice("size", "Size", "Big fits the screen, for playing from the couch",
            Appearance.Sizes, () => Settings.UiSize, v => Settings.UiSize = v);
        Choice("background", "Background", "What you see behind the overlay",
            Appearance.Backgrounds, () => Settings.Background, v => Settings.Background = v);
        Choice("clock", "Clock", "The time at the top of every page",
            Appearance.ClockFormats, () => Settings.ClockFormat, v => Settings.ClockFormat = v);
    }

    // ---- Startup ----------------------------------------------------------------------------

    private void BuildStartup()
    {
        Header("When Windows starts", first: true);
        Toggle("autostart", "Start with Windows", "Console Overlay waits in the tray from the moment you sign in",
            () => Autostart.IsEnabled, on =>
            {
                if (!Autostart.Set(on))
                    ShowMessage("Windows didn't allow that. The log has details.", error: true);
            });
        Toggle("notice", "Notification on start", "A short note in the corner saying it's running",
            () => Settings.StartupNotification, on => Settings.StartupNotification = on);
    }

    // ---- Games ------------------------------------------------------------------------------

    private void BuildGames()
    {
        Header("Exclusive fullscreen", first: true);
        Note("Some games take the whole screen so nothing can draw on top. The overlay can minimize them, open, " +
             "and bring the game back when you close it.");
        Choice("fallback", "When a game doesn't allow overlays", null,
            [("on", "Minimize it and open"), ("off", "Don't open")],
            () => Settings.ExclusiveFullscreenFallback ? "on" : "off", v => Settings.ExclusiveFullscreenFallback = v == "on");

        var skipped = Settings.ExclusiveFullscreenExcludedGames;
        if (_gameName is not null && !skipped.Any(g => SameGame(g, _gameName)))
        {
            Row("skip-current", $"Skip {_gameName}", "Never minimize this one for the overlay. It opens only when the game allows it.", null, () =>
            {
                skipped.Add(_gameName + ".exe");
                _host?.Save();
                ShowMessage($"{_gameName} won't be minimized any more.");
            });
        }

        Header("Skipped games", first: false);
        if (skipped.Count == 0)
            Note("None. Open the overlay over a game, then skip it here.");
        foreach (var game in skipped.ToList())
        {
            Row("skip:" + game, game, null, () => "Remove", () =>
            {
                skipped.RemoveAll(g => g == game);
                _host?.Save();
                ShowMessage($"{game} is no longer skipped.");
            });
        }
    }

    private static bool SameGame(string a, string b) =>
        string.Equals(System.IO.Path.GetFileNameWithoutExtension(a.Trim()), System.IO.Path.GetFileNameWithoutExtension(b.Trim()), StringComparison.OrdinalIgnoreCase);

    private static string? GameName(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        Interop.Native.GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (pid == Environment.ProcessId || name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                return null;
            return name;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // ---- Discord ----------------------------------------------------------------------------

    private void BuildDiscord()
    {
        Header("Your Discord app", first: true);
        Note("Voice controls use your own free Discord app. The setup guide shows how to make one in a couple of minutes.");
        Row("discord-id", "Client ID", "From the OAuth2 page of your app",
            () => Settings.DiscordClientId.Trim() is { Length: > 0 } id ? id : "Not set", () => EditField("id"));
        Row("discord-secret", "Client Secret", "Stays on this PC and is only ever sent to Discord",
            () => Settings.DiscordClientSecret.Trim() is { Length: > 0 } s ? "••••" + s[^Math.Min(4, s.Length)..] : "Not set",
            () => EditField("secret"));
        Row("discord-guide", "Open the setup guide", "Opens in your browser", () => "", () => OpenUrl(DiscordPage.SetupGuideUrl));
        Row("discord-signout", "Sign out of Discord", "Next time, Discord asks again whether Console Overlay may control voice", null, () =>
        {
            _host?.SignOutOfDiscord();
            ShowMessage("Signed out of Discord.");
        });
    }

    private void EditField(string field)
    {
        _editing = field;
        _message = null;
        Refresh();
    }

    private void BuildField()
    {
        bool secret = _editing == "secret";
        Header(secret ? "Discord Client Secret" : "Discord Client ID", first: true);
        string current = secret ? Settings.DiscordClientSecret : Settings.DiscordClientId;

        Control box = secret
            ? new PasswordBox { Password = current, Style = (Style)_resources.FindResource("FieldPasswordBox") }
            : new TextBox { Text = current, Style = (Style)_resources.FindResource("FieldBox") };
        box.Tag = "field";
        box.Margin = new Thickness(0, 0, 0, 12);
        _editBox = box;
        _detail.Children.Add(box);

        Row("edit-paste", "Paste from the clipboard", "Copy it in your browser first", null, PasteIntoField);
        Row("edit-save", "Save", null, null, SaveField);
        Row("edit-clear", "Clear", null, null, () => SetFieldText(""));
        Row("edit-cancel", "Cancel", null, null, CancelField);
    }

    private string FieldText => _editBox switch
    {
        PasswordBox p => p.Password,
        TextBox t => t.Text,
        _ => "",
    };

    private void SetFieldText(string text)
    {
        if (_editBox is PasswordBox p) p.Password = text;
        else if (_editBox is TextBox t)
        {
            t.Text = text;
            t.CaretIndex = text.Length;
        }
        Focus(_editBox);
    }

    private void PasteIntoField()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                SetFieldText(Clipboard.GetText().Trim());
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read the clipboard: {ex.Message}");
        }
        ShowMessageKeepingField("The clipboard has no text. Copy the value in your browser first.");
    }

    private void ShowMessageKeepingField(string text)
    {
        var typed = FieldText;
        ShowMessage(text, error: true);
        SetFieldText(typed);
    }

    private void SaveField()
    {
        if (_host is null || _editing is null) return;
        var value = FieldText.Trim();
        if (_editing == "id" && value.Length > 0 && !value.All(char.IsAsciiDigit))
        {
            ShowMessageKeepingField("A Client ID is only digits, like 123456789012345678. Check that you copied the Client ID.");
            return;
        }

        bool secret = _editing == "secret";
        if (secret) Settings.DiscordClientSecret = value;
        else Settings.DiscordClientId = value;
        _host.Save();

        _editing = null;
        ShowMessage(secret ? "Client Secret saved." : "Client ID saved.");
        Focus(DetailItems().FirstOrDefault(c => c.Tag as string == (secret ? "discord-secret" : "discord-id")));
    }

    private void CancelField()
    {
        string field = _editing == "secret" ? "discord-secret" : "discord-id";
        _editing = null;
        ShowMessage(null);
        Focus(DetailItems().FirstOrDefault(c => c.Tag as string == field));
    }

    // ---- About ------------------------------------------------------------------------------

    private void BuildAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Header("Console Overlay", first: true);
        Row("version", $"Version {version?.ToString(3) ?? "?"}", "A controller-friendly overlay for Windows", null, null);
        Row("updates", "Check for updates", "Opens the downloads page in your browser", () => "", () => OpenUrl(ReleasesUrl));

        Header("Help", first: false);
        Row("log", "Open the log", "For when something goes wrong", null, () => _host?.OpenLog());
        Row("file", "Edit the settings file", "Everything on these pages, as text", null, () => _host?.OpenSettingsFile());
        var quit = Row("quit", "Quit Console Overlay", null, null, () => _host?.Quit());
        quit.Foreground = (Brush)_resources.FindResource("DangerTextBrush");
    }

    private void OpenUrl(string url) => CloseThen?.Invoke(() =>
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

    // ---- Building blocks --------------------------------------------------------------------

    private void Header(string text, bool first) => _detail.Children.Add(Ui.Section(_resources, text, first));

    private void Note(string text) => _detail.Children.Add(Ui.Note(_resources, text));

    private Button Row(string key, string title, string? hint, Func<string>? value, Action? onClick)
    {
        var row = Ui.Row(_resources, title, hint, value is null ? null : "", onClick);
        row.Tag = key;
        if (value is not null && FindValueText(row) is { } text)
        {
            _values.Add((text, () => value() is { Length: > 0 } v ? v + "  ›" : "›"));
            text.Text = _values[^1].Value();
        }
        _detail.Children.Add(row);
        return row;
    }

    /// <summary>A row with a few options: ← → (or A) steps through them, and each step is saved.</summary>
    private void Choice(string key, string title, string? hint, (string Value, string Label)[] options, Func<string> get, Action<string> set)
    {
        int Index() => Math.Max(0, Array.FindIndex(options, o => Appearance.Is(o.Value, get())));

        void Step(int delta)
        {
            var next = options[(Index() + delta + options.Length) % options.Length];
            set(next.Value);
            _host?.Save();
            RefreshValues();
        }

        var row = Ui.Row(_resources, title, hint, "", () => Step(+1));
        row.Tag = key;
        if (FindValueText(row) is { } text)
        {
            _values.Add((text, () => $"‹  {options[Index()].Label}  ›"));
            text.Text = _values[^1].Value();
        }
        _cyclers[key] = Step;
        _detail.Children.Add(row);
    }

    private void Toggle(string key, string title, string? hint, Func<bool> get, Action<bool> set) =>
        Choice(key, title, hint, [("off", "Off"), ("on", "On")], () => get() ? "on" : "off", v => set(v == "on"));

    private static TextBlock? FindValueText(Button row) =>
        (row.Content as Grid)?.Children.OfType<TextBlock>().FirstOrDefault(t => Grid.GetColumn(t) == 1);
}
