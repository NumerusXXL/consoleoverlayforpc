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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Shortcuts page: your own apps, games and links as tiles. A launches one (the overlay closes first),
/// X removes it (press twice). The first tile adds a new one: an app from the Start menu (Steam and other
/// launchers put their games there too), any file you browse to, or a web link.
/// </summary>
internal sealed class ShortcutsPage : IOverlayPage
{
    private const double TileWidth = 150;
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(3);
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private enum View { Tiles, AddMenu, AppPicker, Link }

    private readonly FrameworkElement _resources;
    private readonly Grid _root = new();
    private readonly WrapPanel _tiles = new();
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

    /// <summary>Close the overlay (without going back to the game), then run this.</summary>
    public event Action<Action>? CloseThen;
    public event Action? HintChanged;

    /// <summary>Set while a file dialog is open, so the overlay doesn't close when the dialog takes focus.</summary>
    public event Action<bool>? KeepOpen;

    public ShortcutsPage(FrameworkElement resources)
    {
        _resources = resources;
        _root.Children.Add(_tiles);
        _root.Children.Add(_list);
        _list.Visibility = Visibility.Collapsed;
        _list.MaxWidth = 620;
        _list.HorizontalAlignment = HorizontalAlignment.Left;
        KeyboardNavigation.SetDirectionalNavigation(_list, KeyboardNavigationMode.Contained);
    }

    public Panel Content => _root;

    public string Hint => _view switch
    {
        View.Tiles => "A open · X remove · B back to game · LB / RB other pages",
        View.AppPicker => "A add · LT / RT jump between letters · B back",
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
                return false;

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
        _tiles.Visibility = view == View.Tiles ? Visibility.Visible : Visibility.Collapsed;
        _list.Visibility = view == View.Tiles ? Visibility.Collapsed : Visibility.Visible;
        _tiles.Children.Clear();
        _list.Children.Clear();
        _linkName = _linkUrl = null;

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
        _tiles.Children.Add(Tile("", null, "Add", "App, game or link", () => SetView(View.AddMenu, focus: true), tag: null));
        var shortcuts = Shortcuts;
        for (int i = 0; i < shortcuts.Count; i++)
        {
            var shortcut = shortcuts[i];
            _tiles.Children.Add(Tile(IsLink(shortcut.Target) ? "" : "", IconFor(shortcut.Target), shortcut.Name,
                Describe(shortcut.Target), () => Launch(shortcut), tag: i));
        }
        if (_message is not null)
        {
            _tiles.Children.Add(new TextBlock
            {
                Text = _message,
                Style = (Style)_resources.FindResource("RowHint"),
                Width = 600,
                Margin = new Thickness(4, 0, 0, 0),
            });
        }
    }

    private Button Tile(string glyph, ImageSource? icon, string name, string subtitle, Action onClick, int? tag)
    {
        UIElement picture = icon is not null
            ? new Image { Source = icon, Width = 40, Height = 40 }
            : Ui.Icon(_resources, glyph, 30);
        if (picture is FrameworkElement p)
        {
            p.HorizontalAlignment = HorizontalAlignment.Center;
            p.VerticalAlignment = VerticalAlignment.Center;
        }
        var art = new Border
        {
            Height = 76,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            Child = picture,
        };

        var content = new StackPanel { Width = TileWidth };
        content.Children.Add(art);
        content.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = (Brush)_resources.FindResource("SubtleTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var button = new Button
        {
            Style = (Style)_resources.FindResource("TileButton"),
            Content = content,
            Tag = tag,
            ToolTip = name,
        };
        button.Click += (_, _) => onClick();
        return button;
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
            if (tile?.Content is StackPanel { Children.Count: 3 } panel && panel.Children[2] is TextBlock subtitle)
            {
                string original = subtitle.Text;
                subtitle.Text = "Press X again to remove";
                var reset = new DispatcherTimer { Interval = ConfirmWindow };
                reset.Tick += (_, _) =>
                {
                    reset.Stop();
                    subtitle.Text = original;
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
            _list.Children.Add(Ui.Note(_resources, "Nothing found in the Start menu. Use Browse for a file instead."));

        char? letter = null;
        foreach (var (name, path) in _startMenu)
        {
            char first = char.ToUpperInvariant(name[0]);
            if (!char.IsLetter(first)) first = '#';
            if (first != letter)
            {
                letter = first;
                _list.Children.Add(Ui.Section(_resources, first.ToString(), first: false));
            }
            var row = AddRow(name, null, () => Add(name, path), tag: first.ToString());
            row.Padding = new Thickness(18, 10, 18, 10);
        }
    }

    /// <summary>LT / RT: to the first entry of the previous or next letter.</summary>
    private void JumpLetter(IInputElement? focused, int direction)
    {
        var rows = _list.Children.OfType<Button>().ToList();
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

    private static ImageSource? IconFor(string target)
    {
        if (IsLink(target) || !File.Exists(target)) return null;
        if (Icons.TryGetValue(target, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(target);
            if (icon is not null)
            {
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                source = bitmap;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"No icon for {target}: {ex.Message}");
        }
        Icons[target] = source;
        return source;
    }
}
