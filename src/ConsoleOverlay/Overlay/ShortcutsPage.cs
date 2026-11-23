using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using ConsoleOverlay.Input;
using ConsoleOverlay.Interop;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Shortcuts page: your own apps, games and links as tiles. A launches one (the overlay closes first),
/// X removes it (press twice). The first tile adds a new one: an app from the Start menu (Steam and other
/// launchers put their games there too), any file you browse to, or a web link.
/// </summary>
internal sealed class ShortcutsPage : IOverlayPage
{
    private const double TileSize = 150, FocusedSize = 212, TileGap = 18;
    private static readonly TimeSpan GrowTime = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private enum View { Tiles, AddMenu, AppPicker, Link }

    private readonly FrameworkElement _resources;
    private readonly Grid _root = new();
    // Tiles view, laid out like All Tabs: one row of square tiles, the focused one bigger, its name big underneath.
    private readonly Grid _tilesView = new();
    private readonly ScrollViewer _strip = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        CanContentScroll = false,
    };
    private readonly StackPanel _tiles = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _title = new() { FontSize = 48, FontWeight = FontWeights.ExtraBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _meta = new() { FontSize = 16, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
    private readonly StackPanel _list = new();
    private ISettingsHost? _host;
    private bool _active;
    private View _view = View.Tiles;
    private List<(string Name, string Path)>? _startMenu;
    private TextBox? _linkName;
    private TextBox? _linkUrl;
    private string? _message;
    private int _confirmIndex = -1;
    private DateTime _confirmUntil;
    private DateTime _messageAt;

    /// <summary>Close the overlay (without going back to the game), then run this.</summary>
    public event Action<Action>? CloseThen;
    public event Action? HintChanged;

    /// <summary>Set while a file dialog is open, so the overlay doesn't close when the dialog takes focus.</summary>
    public event Action<bool>? KeepOpen;

    public ShortcutsPage(FrameworkElement resources)
    {
        _resources = resources;

        _tilesView.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FocusedSize + 52) });
        _tilesView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _tiles.Margin = new Thickness(12, 30, 12, 12); // room for the focus ring and halo
        _strip.Margin = new Thickness(-12, 0, -12, 0);
        _strip.Content = _tiles;
        KeyboardNavigation.SetDirectionalNavigation(_tiles, KeyboardNavigationMode.Contained);
        _tilesView.Children.Add(_strip);
        _title.FontFamily = (FontFamily)resources.FindResource("DisplayFont");
        _meta.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        var info = new StackPanel { Margin = new Thickness(0, 16, 0, 0), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        info.Children.Add(_title);
        info.Children.Add(_meta);
        info.Children.Add(_actions);
        Grid.SetRow(info, 1);
        _tilesView.Children.Add(info);

        _root.Children.Add(_tilesView);
        _root.Children.Add(_list);
        _list.Visibility = Visibility.Collapsed;
        _list.MaxWidth = 620;
        _list.HorizontalAlignment = HorizontalAlignment.Left;
        KeyboardNavigation.SetDirectionalNavigation(_list, KeyboardNavigationMode.Contained);
    }

    public Panel Content => _root;

    public string Hint => _view switch
    {
        View.Tiles => "B back to game · LB / RB other pages",
        View.AppPicker => "Type to search · A add · LT / RT jump between letters · B back",
        View.Link => "Type, or X paste · A save · B back",
        _ => "A select · B back",
    };

    public void SetHost(ISettingsHost host) => _host = host;

    private List<LaunchShortcut> Shortcuts => _host?.Settings.Shortcuts ?? [];

    // ---- IOverlayPage -----------------------------------------------------------------------

    public void Activate(IntPtr gameWindow)
    {
        _active = true;
        _message = null;
        _confirmIndex = -1;
        SetView(View.Tiles, focus: false);
    }

    public void Deactivate()
    {
        _active = false;
        _tiles.Children.Clear();
        _list.Children.Clear();
        _startMenu = null; // re-read next time, it may have changed
        _view = View.Tiles;
    }

    public UIElement? FirstFocus() => _view == View.Tiles
        ? _tiles.Children.OfType<Button>().Skip(Shortcuts.Count > 0 ? 1 : 0).FirstOrDefault() ?? _tiles.Children.OfType<Button>().FirstOrDefault()
        : _view == View.AppPicker
            ? (UIElement?)_results?.Children.OfType<Button>().FirstOrDefault() ?? _search
            : _list.Children.OfType<Control>().FirstOrDefault(c => c.Focusable);

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        switch (_view)
        {
            case View.Tiles:
                if (command == NavCommand.Secondary && focused is Button { Tag: int index })
                {
                    Remove(index);
                    return true;
                }
                return command is NavCommand.Up or NavCommand.Down; // one row: nothing above or below

            case View.Link:
                if (command == NavCommand.Secondary)
                {
                    PasteInto(focused as TextBox ?? _linkUrl);
                    return true;
                }
                if (command == NavCommand.Accept && focused is TextBox)
                {
                    SaveLink();
                    return true;
                }
                break;

            case View.AppPicker when command == NavCommand.Accept && focused == _search:
                if (_results?.Children.OfType<Button>().FirstOrDefault() is { } top)
                    Focus(top); // Enter in the search box goes to the best match; Enter again adds it
                return true;

            case View.AppPicker when command is NavCommand.ColumnLeft or NavCommand.ColumnRight:
                JumpLetter(focused, command == NavCommand.ColumnRight ? 1 : -1);
                return true;
        }

        if (command == NavCommand.Back && _view != View.Tiles)
        {
            SetView(_view == View.AddMenu ? View.Tiles : View.AddMenu, focus: true);
            return true;
        }
        return false;
    }

    public bool HandleKey(Key key, IInputElement? focused) => key switch
    {
        Key.Delete when _view == View.Tiles => Handle(NavCommand.Secondary, focused),
        Key.Tab when _view == View.AppPicker => Handle(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? NavCommand.ColumnLeft : NavCommand.ColumnRight, focused),
        _ => false,
    };

    // ---- Views ------------------------------------------------------------------------------

    private void SetView(View view, bool focus)
    {
        _view = view;
        _tilesView.Visibility = view == View.Tiles ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = view == View.Tiles ? Visibility.Collapsed : Visibility.Visible;
        _tiles.Children.Clear();
        _list.Children.Clear();
        _linkName = _linkUrl = null;
        _search = null;
        _results = null;

        switch (view)
        {
            case View.Tiles: BuildTiles(); break;
            case View.AddMenu: BuildAddMenu(); break;
            case View.AppPicker: BuildAppPicker(); break;
            case View.Link: BuildLink(); break;
        }

        if (view != View.Tiles)
            Ui.Stagger(_list.Children.OfType<FrameworkElement>().Take(12), 20);
        HintChanged?.Invoke();
        if (focus)
            Focus(view == View.Link ? _linkUrl : FirstFocus());
    }

    private static void Focus(UIElement? target)
    {
        if (target is null) return;
        target.Focus();
        Keyboard.Focus(target);
        (target as FrameworkElement)?.BringIntoView();
    }

    // ---- Tiles ------------------------------------------------------------------------------

    private void BuildTiles()
    {
        _strip.ScrollToHorizontalOffset(0);
        _messageAt = DateTime.UtcNow;
        _tiles.Children.Add(Tile(null, "\uE710", null, "Add", "An app, a game or a link", () => SetView(View.AddMenu, focus: true), tag: null));
        var shortcuts = Shortcuts;
        for (int i = 0; i < shortcuts.Count; i++)
        {
            var shortcut = shortcuts[i];
            bool link = IsLink(shortcut.Target);
            _tiles.Children.Add(Tile(IconFor(shortcut.Target), link ? "\uE774" : "\uE737", link ? Describe(shortcut.Target) : null,
                shortcut.Name, Describe(shortcut.Target), () => Launch(shortcut), tag: i));
        }
        if (_tiles.Children.OfType<Button>().FirstOrDefault() is { } first)
            ShowInfo(first);
    }

    /// <summary>
    /// A square tile like All Tabs: the app's big icon on a gradient of its own color. Links and the Add tile, which
    /// have no icon, get a glyph on a color picked from their name.
    /// </summary>
    private Button Tile(ImageSource? icon, string glyph, string? colorKey, string name, string subtitle, Action onClick, int? tag)
    {
        FrameworkElement picture = icon is not null
            ? new Image { Source = icon, Width = 64, Height = 64 }
            : Ui.Icon(_resources, glyph, 44);
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.VerticalAlignment = VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        if (picture is TextBlock text) text.Foreground = Brushes.White;

        Brush background;
        if (icon is not null)
        {
            var (light, dark) = Ui.TileColors(icon);
            background = new LinearGradientBrush(light, dark, new Point(0.2, 0), new Point(0.8, 1));
        }
        else if (colorKey is not null)
        {
            background = NameColor(colorKey);
        }
        else
        {
            background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)); // the Add tile: plain frosted
        }

        var button = new Button
        {
            Style = (Style)_resources.FindResource("WindowTile"),
            Content = new Border { CornerRadius = new CornerRadius(24), Background = background, Child = picture },
            Width = TileSize,
            Height = TileSize,
            Margin = new Thickness(0, 0, TileGap, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Tag = tag,
            DataContext = (name, subtitle), // read back by ShowInfo
        };
        button.Click += (_, _) => onClick();
        button.MouseEnter += (_, _) =>
        {
            if (!button.IsKeyboardFocused) Keyboard.Focus(button);
        };
        button.GotKeyboardFocus += (_, _) =>
        {
            int index = _tiles.Children.IndexOf(button);
            _strip.ScrollToHorizontalOffset(Math.Max(0, (index - 1) * (TileSize + TileGap)));
            Grow(button, true);
            ShowInfo(button);
        };
        button.LostKeyboardFocus += (_, _) => Grow(button, false);
        return button;
    }

    private static void Grow(Button button, bool focused)
    {
        var duration = new Duration(GrowTime);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        double size = focused ? FocusedSize : TileSize;
        button.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(size, duration) { EasingFunction = ease });
        button.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(size, duration) { EasingFunction = ease });
    }

    /// <summary>The focused tile's name big, what it is underneath, and what A and X do.</summary>
    private void ShowInfo(Button tile)
    {
        var (name, subtitle) = tile.DataContext is ValueTuple<string, string> info ? info : ("", "");
        bool confirming = tile.Tag is int index && index == _confirmIndex && DateTime.UtcNow <= _confirmUntil;
        _title.Text = name;
        // A note ("Removed Steam.") shows for a few seconds after the tiles are rebuilt, then the usual line.
        bool note = _message is not null && DateTime.UtcNow - _messageAt < TimeSpan.FromSeconds(4);
        _meta.Text = confirming ? "Press X again to remove" : note ? _message! : subtitle;
        _actions.Children.Clear();
        if (tile.Tag is int i)
        {
            _actions.Children.Add(Ui.ActionPill(_resources, "A", "Open", primary: true, () => tile.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent))));
            _actions.Children.Add(Ui.ActionPill(_resources, "X", confirming ? "Remove (press again)" : "Remove", primary: false, () => Remove(i)));
        }
        else
        {
            _actions.Children.Add(Ui.ActionPill(_resources, "A", "Add a shortcut", primary: true, () => SetView(View.AddMenu, focus: true)));
        }
    }

    private static Brush NameColor(string name)
    {
        Color[][] pairs =
        [
            [Color.FromRgb(0xFF, 0x5A, 0x5A), Color.FromRgb(0x7A, 0x14, 0x24)],
            [Color.FromRgb(0x74, 0x80, 0xFF), Color.FromRgb(0x2B, 0x2F, 0x98)],
            [Color.FromRgb(0x3E, 0xE0, 0x8F), Color.FromRgb(0x10, 0x6A, 0x40)],
            [Color.FromRgb(0x2F, 0x9B, 0xFF), Color.FromRgb(0x12, 0x2C, 0x6B)],
            [Color.FromRgb(0xF6, 0xC4, 0x53), Color.FromRgb(0x9A, 0x63, 0x0E)],
            [Color.FromRgb(0xC0, 0x7A, 0xFF), Color.FromRgb(0x4A, 0x18, 0x7A)],
        ];
        int hash = 0;
        foreach (char c in name) hash = hash * 31 + char.ToLowerInvariant(c);
        var pair = pairs[Math.Abs(hash % pairs.Length)];
        return new LinearGradientBrush(pair[0], pair[1], new Point(0.2, 0), new Point(0.8, 1));
    }

    private void Launch(LaunchShortcut shortcut) => CloseThen?.Invoke(() =>
    {
        try
        {
            Log.Write($"Launching shortcut \"{shortcut.Name}\".");
            Process.Start(new ProcessStartInfo(shortcut.Target)
            {
                UseShellExecute = true,
                WorkingDirectory = File.Exists(shortcut.Target) ? Path.GetDirectoryName(shortcut.Target) ?? "" : "",
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not launch {shortcut.Target}: {ex.Message}");
        }
    });

    private void Remove(int index)
    {
        if (_host is null || index < 0 || index >= Shortcuts.Count) return;
        var tile = _tiles.Children.OfType<Button>().FirstOrDefault(b => b.Tag is int i && i == index);

        if (_confirmIndex != index || DateTime.UtcNow > _confirmUntil)
        {
            _confirmIndex = index;
            _confirmUntil = DateTime.UtcNow + ConfirmWindow;
            if (tile is not null)
            {
                ShowInfo(tile);
                var reset = new DispatcherTimer { Interval = ConfirmWindow };
                reset.Tick += (_, _) =>
                {
                    reset.Stop();
                    if (Keyboard.FocusedElement == tile) ShowInfo(tile);
                };
                reset.Start();
            }
            return;
        }

        _confirmIndex = -1;
        var removed = Shortcuts[index];
        Shortcuts.RemoveAt(index);
        _host.Save();
        _message = $"Removed {removed.Name}.";
        SetView(View.Tiles, focus: false);
        var buttons = _tiles.Children.OfType<Button>().ToList();
        Focus(buttons[Math.Clamp(index + 1, 0, buttons.Count - 1)]); // +1: the Add tile comes first
    }

    private void Add(string name, string target)
    {
        if (_host is null) return;
        if (Shortcuts.Any(s => string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase)))
        {
            _message = $"{name} is already here.";
        }
        else
        {
            Shortcuts.Add(new LaunchShortcut { Name = name, Target = target });
            _host.Save();
            _message = null;
        }
        SetView(View.Tiles, focus: false);
        Focus(_tiles.Children.OfType<Button>().LastOrDefault());
    }

    // ---- Adding -----------------------------------------------------------------------------

    private void BuildAddMenu()
    {
        _list.Children.Add(Ui.Section(_resources, "Add a shortcut", first: true));
        AddRow("App or game", "From the Start menu. Steam, Epic and other launchers put their games there too", () => SetView(View.AppPicker, focus: true));
        AddRow("Link", "A website, or a link like steam://", () => SetView(View.Link, focus: true));
        AddRow("Browse for a file", "Any program, game or document on this PC", Browse);
        AddRow("Cancel", null, () => SetView(View.Tiles, focus: true));
    }

    private Button AddRow(string title, string? hint, Action onClick, string? tag = null)
    {
        var row = Ui.Row(_resources, title, hint, null, onClick);
        row.Tag = tag;
        _list.Children.Add(row);
        return row;
    }

    private void BuildAppPicker()
    {
        _startMenu ??= ReadStartMenu();
        _list.Children.Add(Ui.Section(_resources, $"Apps and games ({_startMenu.Count})", first: true));
        if (_startMenu.Count == 0)
        {
            _list.Children.Add(Ui.Note(_resources, "Nothing found in the Start menu. Use Browse for a file instead."));
            return;
        }

        // Search: type anywhere on this view and it lands here; the list narrows as you type.
        var search = new TextBox { Style = (Style)_resources.FindResource("FieldBox"), Tag = "search" };
        var placeholder = new TextBlock
        {
            Text = "Search apps and games",
            FontSize = 15,
            Margin = new Thickness(17, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Foreground = (Brush)_resources.FindResource("SubtleTextBrush"),
        };
        var searchIcon = Ui.Icon(_resources, "\uE721", 14);
        searchIcon.HorizontalAlignment = HorizontalAlignment.Right;
        searchIcon.Margin = new Thickness(0, 0, 16, 0);
        searchIcon.IsHitTestVisible = false;
        searchIcon.Foreground = placeholder.Foreground;
        var searchHost = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        searchHost.Children.Add(search);
        searchHost.Children.Add(placeholder);
        searchHost.Children.Add(searchIcon);
        _list.Children.Add(searchHost);
        _search = search;

        _results = new StackPanel();
        _list.Children.Add(_results);
        search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            FillResults(search.Text.Trim());
        };
        FillResults("");
    }

    private TextBox? _search;
    private StackPanel? _results;

    private void FillResults(string query)
    {
        if (_results is null || _startMenu is null) return;
        _results.Children.Clear();

        IEnumerable<(string Name, string Path)> matches = _startMenu;
        if (query.Length > 0)
        {
            // Names that start with it first, then names with a word that starts with it, then anything containing it.
            matches = _startMenu
                .Select(e => (Entry: e, Rank: Rank(e.Name, query)))
                .Where(x => x.Rank >= 0)
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Entry);
        }

        char? letter = null;
        int count = 0;
        foreach (var (name, path) in matches)
        {
            char first = char.ToUpperInvariant(name[0]);
            if (!char.IsLetter(first)) first = '#';
            if (query.Length == 0 && first != letter)
            {
                letter = first;
                _results.Children.Add(Ui.Section(_resources, first.ToString(), first: false));
            }
            var row = Ui.Row(_resources, name, null, null, () => Add(name, path));
            row.Tag = query.Length == 0 ? first.ToString() : "match";
            row.Padding = new Thickness(18, 10, 18, 10);
            _results.Children.Add(row);
            count++;
        }
        if (count == 0)
            _results.Children.Add(Ui.Note(_resources, $"Nothing called \"{query}\". Try Browse for a file instead."));
    }

    private static int Rank(string name, string query)
    {
        if (name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 0;
        int at = name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0) return -1;
        return char.IsLetterOrDigit(name[at - 1]) ? 2 : 1;
    }

    /// <summary>Typing a letter or digit while a row is focused goes to the search box.</summary>
    public bool HandleRawKey(KeyEventArgs e)
    {
        if (_view != View.AppPicker || _search is null || Keyboard.FocusedElement == _search) return false;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        char? c = e.Key switch
        {
            >= Key.A and <= Key.Z => (char)('a' + (e.Key - Key.A)),
            >= Key.D0 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.None => (char)('0' + (e.Key - Key.D0)),
            >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
            _ => null,
        };
        if (c is null) return false;
        _search.Text += c.Value;
        _search.CaretIndex = _search.Text.Length;
        Focus(_search);
        return true;
    }

    /// <summary>LT / RT: to the first entry of the previous or next letter.</summary>
    private void JumpLetter(IInputElement? focused, int direction)
    {
        var rows = _results?.Children.OfType<Button>().ToList() ?? [];
        if (rows.Count == 0) return;
        int at = focused is Button button ? Math.Max(0, rows.IndexOf(button)) : 0;
        var starts = rows.Select((r, i) => (r, i)).Where(x => x.i == 0 || rows[x.i - 1].Tag as string != x.r.Tag as string).Select(x => x.i).ToList();
        int currentStart = starts.LastOrDefault(i => i <= at);
        int pos = starts.IndexOf(currentStart);
        int next = direction > 0 ? Math.Min(pos + 1, starts.Count - 1) : (at > currentStart ? pos : Math.Max(pos - 1, 0));
        Focus(rows[starts[next]]);
    }

    /// <summary>Shortcuts in the Start menu for everyone and for you, without uninstallers and readme files.</summary>
    private static List<(string Name, string Path)> ReadStartMenu()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        string[] skip = ["uninstall", "readme", "read me", "help", "documentation", "license", "website", "release notes", "manual"];
        var found = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = Path.GetFileNameWithoutExtension(file);
                    if (name.Length == 0 || skip.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    found.TryAdd(name, file);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Could not read the Start menu folder {folder}: {ex.Message}");
            }
        }
        return found.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase).Select(p => (p.Key, p.Value)).ToList();
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add a shortcut",
            Filter = "Programs and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        KeepOpen?.Invoke(true);
        bool? picked;
        try
        {
            picked = dialog.ShowDialog(Window.GetWindow(_resources));
        }
        finally
        {
            KeepOpen?.Invoke(false);
        }
        if (_active && picked == true)
            Add(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName);
    }

    private void BuildLink()
    {
        _list.Children.Add(Ui.Section(_resources, "Address", first: true));
        _linkUrl = Field();
        _list.Children.Add(Ui.Section(_resources, "Name (optional)", first: false));
        _linkName = Field();
        if (_message is not null)
            _list.Children.Add(new TextBlock { Text = _message, Foreground = (Brush)_resources.FindResource("DangerTextBrush"), FontSize = 13, Margin = new Thickness(2, 0, 0, 12) });
        AddRow("Paste from the clipboard", "Into the address", () => PasteInto(_linkUrl));
        AddRow("Save", null, SaveLink);
        AddRow("Cancel", null, () => SetView(View.AddMenu, focus: true));
        _message = null;
    }

    private TextBox Field()
    {
        var box = new TextBox { Style = (Style)_resources.FindResource("FieldBox"), Margin = new Thickness(0, 0, 0, 12) };
        _list.Children.Add(box);
        return box;
    }

    private void PasteInto(TextBox? box)
    {
        if (box is null) return;
        try
        {
            if (Clipboard.ContainsText())
            {
                box.Text = Clipboard.GetText().Trim();
                box.CaretIndex = box.Text.Length;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read the clipboard: {ex.Message}");
        }
        Focus(box);
    }

    private void SaveLink()
    {
        var url = _linkUrl?.Text.Trim() ?? "";
        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var typedName = _linkName?.Text ?? "";
            _message = "That doesn't look like an address. Try something like youtube.com.";
            SetView(View.Link, focus: true);
            if (_linkName is not null) _linkName.Text = typedName;
            if (_linkUrl is not null) _linkUrl.Text = url;
            return;
        }
        var name = _linkName?.Text.Trim() is { Length: > 0 } n ? n : (uri.Host.Length > 0 ? uri.Host.Replace("www.", "") : uri.Scheme);
        Add(name, uri.ToString());
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static bool IsLink(string target) =>
        target.Contains("://", StringComparison.Ordinal) && !File.Exists(target);

    private static string Describe(string target)
    {
        if (IsLink(target))
            return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.Replace("www.", "") : "Link";
        return File.Exists(target) || Directory.Exists(target) ? "App" : "Not found";
    }

    /// <summary>The big, sharp icon of an app; for a .lnk, its target's icon (without the shortcut arrow).</summary>
    private static ImageSource? IconFor(string target)
    {
        if (IsLink(target) || !File.Exists(target)) return null;
        if (Icons.TryGetValue(target, out var cached)) return cached;
        var file = target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && ShortcutTarget(target) is { } resolved ? resolved : target;
        var source = ShellIcons.ForFile(file, 256) ?? ShellIcons.ForFile(target, 256);
        Icons[target] = source;
        return source;
    }

    /// <summary>The program a .lnk file points to, or null.</summary>
    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(lnk);
                string path = link.TargetPath;
                return File.Exists(path) ? path : null;
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read the shortcut {lnk}: {ex.Message}");
            return null;
        }
    }
}
