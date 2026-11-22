using System;
using System.Collections.Generic;
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
/// The Store view on the Steam page: the store's front page shelves (Specials, Top sellers, New releases, Coming soon)
/// your wishlist and search, as big game tiles. Up / down changes shelf. A opens the game's page, drawn by the overlay
/// (<see cref="SteamGamePage"/>); X opens it in the Steam app; Y searches with the on-screen keyboard.
/// </summary>
internal sealed class SteamStoreView
{
    private const double TileWidth = 240, TileHeight = 138, FocusedWidth = 300, FocusedHeight = 172;
    private const string SearchShelf = "search";

    private readonly FrameworkElement _resources;
    private readonly StackPanel _root = new();
    private readonly StackPanel _shelfTabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
    private readonly Shelf _shelf;
    private readonly StackPanel _info = new() { Margin = new Thickness(0, 18, 0, 0) };
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _meta = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _description = new()
    {
        FontSize = 15,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.WordEllipsis,
        MaxHeight = 44,
        MaxWidth = 900,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
    private readonly StackPanel _searchPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _query;
    private readonly OnScreenKeyboard _keyboard;
    private readonly DispatcherTimer _detailsDelay;

    private Dictionary<string, List<StoreItem>> _shelves = [];
    private List<StoreItem> _results = [];
    private string? _resultsFor;
    private string _current = SteamStore.Shelves[0].Id;
    private List<StoreItem> _shown = [];
    private StoreItem? _focused;
    private bool _active;
    private bool _searching;
    private string? _error;
    private CancellationTokenSource _cancel = new();
    private int _artToken;

    /// <summary>Open a game's page.</summary>
    public event Action<StoreItem>? OpenGame;

    /// <summary>Close the overlay and open a steam:// link; true: in Big Picture.</summary>
    public event Action<string, bool>? OpenLink;

    /// <summary>Your account id, for the wishlist shelf (null: not signed in to Steam on this PC).</summary>
    public Func<string?> SteamId { get; set; } = () => null;

    /// <summary>Is the game yours, and is it installed?</summary>
    public Func<int, (bool Owned, bool Installed)> Ownership { get; set; } = _ => (false, false);

    private const string WishlistShelf = "wishlist";
    private List<StoreItem> _wishlist = [];
    private DateTime _wishlistAt;

    public event Action? HintChanged;

    public SteamStoreView(FrameworkElement resources)
    {
        _resources = resources;
        _shelf = new Shelf(resources, TileWidth, TileHeight, FocusedWidth, FocusedHeight);

        _title.Style = (Style)resources.FindResource("HeroTitle");
        _description.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        _info.Children.Add(_title);
        _info.Children.Add(_meta);
        _info.Children.Add(_description);
        _info.Children.Add(_actions);

        _query = new TextBox
        {
            Style = (Style)resources.FindResource("FieldBox"),
            MaxWidth = 760,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 14),
            Tag = "search",
        };
        _keyboard = new OnScreenKeyboard(resources) { EnterLabel = "Search" };
        _keyboard.Typed += t => Type(t);
        _keyboard.Backspace += () =>
        {
            if (_query.Text.Length > 0) _query.Text = _query.Text[..^1];
        };
        _keyboard.Enter += () => _ = SearchAsync();
        _keyboard.Closed += () => StopSearching(focusShelf: true);
        _searchPanel.Children.Add(new TextBlock { Text = "Search the Steam Store", Style = (Style)resources.FindResource("HeroTitle"), FontSize = 30 });
        _searchPanel.Children.Add(_query);
        _searchPanel.Children.Add(_keyboard.View);

        _root.Children.Add(_shelfTabs);
        _root.Children.Add(_shelf.View);
        _root.Children.Add(_info);
        _root.Children.Add(_searchPanel);

        _detailsDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
        _detailsDelay.Tick += (_, _) =>
        {
            _detailsDelay.Stop();
            if (_focused is { } item) _ = LoadDetailsAsync(item);
        };
    }

    public FrameworkElement View => _root;

    public string Hint => _searching
        ? _keyboard.Hint
        : "↑ ↓ other shelves · LT / RT library or friends · B back to game · LB / RB other pages";

    public string? Subtitle => "Store";

    public void Activate()
    {
        _active = true;
        _cancel = new CancellationTokenSource();
        BuildShelfTabs();
        if (_shelves.Count == 0) ShowMessage("Loading the store…", null);
        else Rebuild();
        _ = LoadAsync();
    }

    public void Deactivate()
    {
        _active = false;
        _cancel.Cancel();
        _detailsDelay.Stop();
        _artToken++;
        if (_searching) StopSearching(focusShelf: false);
        foreach (var tile in _shelf.Tiles)
            if (tile.Content is Grid layers && layers.Children.Count > 1 && layers.Children[1] is Border art) art.Background = null;
        WebImages.Clear();
    }

    public UIElement? FirstFocus() => _searching ? null : _shelf.Count > 0 ? _shelf.Tiles[0] : null;

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_searching)
        {
            if (focused == _query)
            {
                switch (command)
                {
                    case NavCommand.Accept: _ = SearchAsync(); return true;
                    case NavCommand.Back: StopSearching(focusShelf: true); return true;
                    case NavCommand.Down: _keyboard.FocusFirst(); return true;
                    case NavCommand.Up: return true;
                }
                return false;
            }
            if (_keyboard.Handle(command)) return true;
            if (command is NavCommand.Up && focused is DependencyObject d && IsTopRow(d))
            {
                _query.Focus();
                _query.CaretIndex = _query.Text.Length;
                return true;
            }
            return command is NavCommand.ColumnLeft or NavCommand.ColumnRight;
        }

        switch (command)
        {
            case NavCommand.Up: SwitchShelf(-1); return true;
            case NavCommand.Down: SwitchShelf(1); return true;
            case NavCommand.Secondary:
                if (_focused is { } item) OpenLink?.Invoke(SteamStore.ClientStoreLink(item.AppId), true);
                return true;
            case NavCommand.Mute: StartSearching(); return true;
            case NavCommand.Back:
                return false; // back to the game
        }
        return false;
    }

    /// <summary>Typing on a real keyboard while searching goes into the search box.</summary>
    public bool HandleRawKey(KeyEventArgs e)
    {
        if (!_searching || Keyboard.FocusedElement == _query) return false;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        bool typing = e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9
            or Key.Space or >= Key.Oem1 and <= Key.OemBackslash;
        if (!typing) return false;
        _query.Focus();
        _query.CaretIndex = _query.Text.Length;
        return false; // the key now types into the box
    }

    // ---- Loading ----------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        var cancel = _cancel.Token;
        try
        {
            var shelves = await SteamStore.FeaturedAsync(cancel);
            if (cancel.IsCancellationRequested || !_active) return;
            bool changed = !ReferenceEquals(shelves, _shelves);
            _shelves = shelves;
            _error = null;
            if (changed) Rebuild();
            _ = LoadWishlistAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam store: could not load the front page: {ex.Message}");
            if (!_active) return;
            _error = "The store didn't load";
            if (_shown.Count == 0) ShowMessage(_error, "Check your internet connection, then press A to try again.");
        }
    }

    /// <summary>Your wishlist as a shelf, when your profile's game details are public.</summary>
    private async Task LoadWishlistAsync()
    {
        if (SteamId() is not { } steamId || DateTime.Now - _wishlistAt < TimeSpan.FromMinutes(10)) return;
        var cancel = _cancel.Token;
        try
        {
            var wishlist = await SteamStore.WishlistAsync(steamId, cancel);
            if (cancel.IsCancellationRequested || !_active) return;
            _wishlistAt = DateTime.Now;
            bool had = _wishlist.Count > 0;
            _wishlist = wishlist;
            if (had != (wishlist.Count > 0)) BuildShelfTabs();
            if (_current == WishlistShelf) Rebuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam store: no wishlist: {ex.Message}");
        }
    }

    private async Task SearchAsync()
    {
        var term = _query.Text.Trim();
        if (term.Length == 0) return;
        var cancel = _cancel.Token;
        StopSearching(focusShelf: false);
        _current = SearchShelf;
        _focused = null;
        _resultsFor = term;
        _results = [];
        BuildShelfTabs();
        ShowMessage($"Searching for “{term}”…", null);
        try
        {
            var results = await SteamStore.SearchAsync(term, cancel);
            if (cancel.IsCancellationRequested || !_active || _resultsFor != term) return;
            _results = results;
            Rebuild();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam store: search failed: {ex.Message}");
            if (_active) ShowMessage("Search didn't work", "Check your internet connection and try again with Y.");
        }
    }

    private async Task LoadDetailsAsync(StoreItem item)
    {
        try
        {
            var details = await SteamStore.DetailsAsync(item.AppId, _cancel.Token);
            if (details is null || _focused != item || !_active) return;
            ShowInfo(item, details);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam store: could not load details for {item.AppId}: {ex.Message}");
        }
    }

    // ---- Building ---------------------------------------------------------------------------

    private IEnumerable<(string Id, string Name)> ShelvesInOrder()
    {
        foreach (var shelf in SteamStore.Shelves) yield return shelf;
        if (_wishlist.Count > 0) yield return (WishlistShelf, "Wishlist");
        if (_resultsFor is not null) yield return (SearchShelf, $"“{_resultsFor}”");
    }

    private void BuildShelfTabs()
    {
        _shelfTabs.Children.Clear();
        foreach (var (id, name) in ShelvesInOrder())
        {
            var tab = new Button
            {
                Style = (Style)_resources.FindResource("TabPill"),
                Content = name,
                Tag = id == _current ? "selected" : null,
                Height = 30,
                FontSize = 13,
            };
            var target = id;
            tab.Click += (_, _) =>
            {
                _current = target;
                _focused = null;
                BuildShelfTabs();
                Rebuild();
            };
            _shelfTabs.Children.Add(tab);
        }
    }

    private void SwitchShelf(int step)
    {
        var ids = ShelvesInOrder().Select(s => s.Id).ToList();
        int index = ids.IndexOf(_current);
        int next = Math.Clamp(index + step, 0, ids.Count - 1);
        if (next == index) return;
        _current = ids[next];
        _focused = null;
        BuildShelfTabs();
        Rebuild();
    }

    private void Rebuild()
    {
        // Coming back from a store page, the same game keeps focus.
        int? keepAppId = _focused?.AppId;
        _shelf.Clear();
        _artToken++;
        _shown = _current == SearchShelf ? _results : _current == WishlistShelf ? _wishlist
            : _shelves.TryGetValue(_current, out var list) ? list : [];
        if (_shown.Count == 0)
        {
            if (_current == SearchShelf) ShowMessage("Nothing found", "Press Y to search for something else.");
            else if (_shelves.Count > 0) ShowMessage("Nothing on this shelf right now", "Press ↑ or ↓ for another shelf.");
            return;
        }

        _shelf.View.Visibility = Visibility.Visible;
        foreach (var item in _shown)
        {
            var current = item;
            _shelf.Add(MakeTile(item), index =>
            {
                _focused = current;
                ShowInfo(current, null);
                _detailsDelay.Stop();
                _detailsDelay.Start();
                RequestArt(index);
            }, () => OpenGame?.Invoke(current));
        }
        int start = keepAppId is { } id ? Math.Max(0, _shown.FindIndex(i => i.AppId == id)) : 0;
        if (start == 0) _shelf.Rise();
        RequestArt(start);
        _focused = _shown[start];
        ShowInfo(_shown[start], null);
        if (_active) _shelf.Focus(start);
    }

    private Grid MakeTile(StoreItem item)
    {
        var layers = new Grid();
        layers.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = (Brush)_resources.FindResource("TileBrush"),
            Child = new TextBlock
            {
                Text = item.Name,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(14),
            },
        });
        layers.Children.Add(new Border { CornerRadius = new CornerRadius(20) }); // the art, once downloaded
        var (owned, installed) = Ownership(item.AppId);
        if (owned)
            layers.Children.Add(Badge(installed ? "Installed" : "In library", new Thickness(10), HorizontalAlignment.Left, VerticalAlignment.Top));
        else if (item.DiscountPercent > 0)
            layers.Children.Add(Badge($"−{item.DiscountPercent}%", new Thickness(10), HorizontalAlignment.Left, VerticalAlignment.Top));
        return layers;
    }

    private Border Badge(string text, Thickness margin, HorizontalAlignment h, VerticalAlignment v) => new()
    {
        Background = Brushes.White,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(10, 4, 10, 5),
        Margin = margin,
        HorizontalAlignment = h,
        VerticalAlignment = v,
        Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (Brush)_resources.FindResource("SelectedTextBrush") },
    };

    private void ShowInfo(StoreItem item, StoreDetails? details)
    {
        _info.Visibility = Visibility.Visible;
        _title.Text = details?.Name is { Length: > 0 } name ? name : item.Name;

        _meta.Children.Clear();
        var (owned, installed) = Ownership(item.AppId);
        int discount = owned ? 0 : details?.DiscountPercent ?? item.DiscountPercent;
        string? price = owned ? (installed ? "Installed" : "In your library")
            : details is null ? SteamStore.FormatPrice(item)
            : details.IsFree ? "Free to play" : details.Price;
        string? original = owned ? null : details is null ? (item.DiscountPercent > 0 ? SteamStore.FormatPrice(item, original: true) : null)
            : details.OriginalPrice;
        if (discount > 0)
        {
            var badge = Badge($"−{discount}%", new Thickness(0, 0, 12, 0), HorizontalAlignment.Left, VerticalAlignment.Center);
            _meta.Children.Add(badge);
        }
        if (price is not null)
            _meta.Children.Add(MetaText(price, FontWeights.SemiBold, (Brush)_resources.FindResource("TextBrush")));
        if (original is not null)
        {
            var was = MetaText(original, FontWeights.Normal, (Brush)_resources.FindResource("SubtleTextBrush"));
            was.TextDecorations = TextDecorations.Strikethrough;
            was.Margin = new Thickness(10, 0, 0, 0);
            _meta.Children.Add(was);
        }
        var extra = new List<string>();
        if (details is not null)
        {
            if (details.ComingSoon && details.ReleaseDate is { Length: > 0 } soon) extra.Add("Coming " + soon);
            else if (details.ReleaseDate is { Length: > 0 } date) extra.Add(date);
            extra.AddRange(details.Genres);
            if (details.Developer is { Length: > 0 } dev) extra.Add(dev);
        }
        if (extra.Count > 0)
        {
            var more = MetaText((price is null && original is null && discount == 0 ? "" : "  ·  ") + string.Join("  ·  ", extra),
                FontWeights.Normal, (Brush)_resources.FindResource("SoftTextBrush"));
            _meta.Children.Add(more);
        }
        _description.Text = details?.Description ?? "";
        _description.Visibility = _description.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        _actions.Children.Clear();
        _actions.Children.Add(Ui.ActionPill(_resources, "A", "Game page", primary: true, () => OpenGame?.Invoke(item)));
        _actions.Children.Add(Ui.ActionPill(_resources, "X", "Open in Steam", primary: false,
            () => OpenLink?.Invoke(SteamStore.ClientStoreLink(item.AppId), true)));
        _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Search", primary: false, StartSearching));
    }

    private static TextBlock MetaText(string text, FontWeight weight, Brush brush) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = weight,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void ShowMessage(string title, string? detail)
    {
        _shelf.Clear();
        _shown = [];
        _focused = null;
        _shelf.View.Visibility = Visibility.Hidden;
        _info.Visibility = Visibility.Visible;
        _title.Text = title;
        _meta.Children.Clear();
        _description.Text = detail ?? "";
        _description.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;
        _actions.Children.Clear();
        _actions.Children.Add(Ui.ActionPill(_resources, "A", "Try again", primary: true, () => _ = LoadAsync()));
        _actions.Children.Add(Ui.ActionPill(_resources, "Y", "Search", primary: false, StartSearching));
    }

    /// <summary>A on an empty shelf loads the store again.</summary>
    public bool AcceptWithoutTiles()
    {
        if (_searching || _shelf.Count > 0) return false;
        _ = LoadAsync();
        return true;
    }

    /// <summary>Puts focus back on the game you were on (coming back from its page).</summary>
    public void FocusCurrent()
    {
        if (_searching) { _keyboard.FocusFirst(); return; }
        int index = _focused is null ? 0 : Math.Max(0, _shown.IndexOf(_focused));
        _shelf.Focus(index);
    }

    // ---- Search -----------------------------------------------------------------------------

    private void StartSearching()
    {
        if (_searching) return;
        _searching = true;
        _query.Text = _resultsFor ?? "";
        _shelfTabs.Visibility = Visibility.Collapsed;
        _shelf.View.Visibility = Visibility.Collapsed;
        _info.Visibility = Visibility.Collapsed;
        _searchPanel.Visibility = Visibility.Visible;
        _keyboard.FocusFirst();
        HintChanged?.Invoke();
    }

    private void StopSearching(bool focusShelf)
    {
        if (!_searching) return;
        _searching = false;
        _searchPanel.Visibility = Visibility.Collapsed;
        _shelfTabs.Visibility = Visibility.Visible;
        _shelf.View.Visibility = _shelf.Count > 0 ? Visibility.Visible : Visibility.Hidden;
        _info.Visibility = Visibility.Visible;
        if (focusShelf) _shelf.Focus(Math.Max(0, _focused is null ? 0 : _shown.IndexOf(_focused)));
        HintChanged?.Invoke();
    }

    private void Type(string text)
    {
        _query.Text += text;
        _query.CaretIndex = _query.Text.Length;
    }

    private bool IsTopRow(DependencyObject element)
    {
        // The first row of keys (digits): ↑ from there goes to the search box.
        var rows = (_keyboard.View as Border)?.Child as StackPanel;
        return rows is not null && rows.Children.Count > 0 && Ui.IsInside(element, rows.Children[0]);
    }

    // ---- Art --------------------------------------------------------------------------------

    private void RequestArt(int around)
    {
        int token = _artToken;
        double pixelsPerUnit = VisualTreeHelper.GetDpi(_root).DpiScaleX * 2;
        int width = (int)Math.Clamp(FocusedWidth * pixelsPerUnit, 300, 640);
        for (int i = Math.Max(0, around - 2); i <= Math.Min(_shown.Count - 1, around + 6); i++)
        {
            var tile = _shelf.Tiles[i];
            if (tile.Content is not Grid layers || layers.Children[1] is not Border art || art.Tag is int t && t == token) continue;
            art.Tag = token;
            var url = _shown[i].ImageUrl;
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
}
