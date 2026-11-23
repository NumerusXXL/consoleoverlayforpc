using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using ConsoleOverlay.Steam;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// A game's store page, drawn by the overlay itself like a console store: the game's screenshot fills the background,
/// big title, price, what kind of game it is, controller support, and a row of screenshots (moving through them
/// changes the background). Read from Steam's public store data.
///
/// A buys it, installs it or plays it, depending on whether it's yours: buying and installing open the game in the
/// Steam app, because Valve sells only through its own store and checkout. X opens its full store page in the Steam
/// app (reviews, DLC, wishlist), or its page in your Steam library if it's yours. B goes back.
/// </summary>
internal sealed class SteamGamePage
{
    private const double ShotWidth = 256, ShotHeight = 144, FocusedShotWidth = 304, FocusedShotHeight = 171;

    private readonly FrameworkElement _resources;
    private readonly StackPanel _root = new();
    private readonly TextBlock _title = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _price = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly WrapPanel _tags = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _description = new()
    {
        FontSize = 16,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.WordEllipsis,
        MaxHeight = 46,
        MaxWidth = 880,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 14, 0, 0),
    };
    private readonly TextBlock _facts = new() { FontSize = 14, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 20, 0, 0) };
    private readonly TextBlock _shotsLabel;
    private readonly Shelf _shots;
    private readonly DispatcherTimer _backdropDelay;

    private StoreItem? _item;
    private StoreDetails? _details;
    private CancellationTokenSource _cancel = new();
    private int _token;
    private string? _backdropUrl;

    /// <summary>Is the game yours, and is it installed? (From your library on this PC and, with a key, your account.)</summary>
    public Func<int, (bool Owned, bool Installed)> Ownership { get; set; } = _ => (false, false);

    /// <summary>A steam:// link to open, and whether to open Big Picture first (buying, store pages).</summary>
    public event Action<string, bool>? OpenLink;

    /// <summary>A picture to show behind the page; null for the usual background.</summary>
    public event Action<BitmapSource?>? BackdropChanged;

    /// <summary>B: back to where it was opened from.</summary>
    public event Action? Closed;

    /// <summary>A on a game you don't have: its page in the overlay's Store (buying happens there).</summary>
    public event Action<int>? OpenStore;

    public SteamGamePage(FrameworkElement resources)
    {
        _resources = resources;
        _title.Style = (Style)resources.FindResource("HeroTitle");
        _title.FontSize = 46;
        _description.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        _facts.Foreground = (Brush)resources.FindResource("SubtleTextBrush");
        _shotsLabel = Ui.Section(resources, "Screenshots", first: false);
        _shotsLabel.Margin = new Thickness(2, 18, 0, 0);
        _shots = new Shelf(resources, ShotWidth, ShotHeight, FocusedShotWidth, FocusedShotHeight);

        _root.Children.Add(_title);
        _root.Children.Add(_price);
        _root.Children.Add(_tags);
        _root.Children.Add(_description);
        _root.Children.Add(_facts);
        _root.Children.Add(_actions);
        _root.Children.Add(_shotsLabel);
        _root.Children.Add(_shots.View);
        _root.Visibility = Visibility.Collapsed;

        _backdropDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(240) };
        _backdropDelay.Tick += (_, _) =>
        {
            _backdropDelay.Stop();
            if (_backdropUrl is { } url) _ = ShowBackdropAsync(url);
        };
    }

    public FrameworkElement View => _root;

    public bool IsOpen => _item is not null;

    public string Hint => "← → screenshots · B back · LB / RB other pages";

    public string? Subtitle => "Store";

    public void Open(StoreItem item)
    {
        _item = item;
        _details = null;
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        _token++;
        _root.Visibility = Visibility.Visible;
        Build();
        _ = LoadAsync(item, _token);
    }

    public void Close()
    {
        _item = null;
        _details = null;
        _cancel.Cancel();
        _token++;
        _backdropDelay.Stop();
        _shots.Clear();
        _root.Visibility = Visibility.Collapsed;
        BackdropChanged?.Invoke(null);
    }

    /// <summary>Left the Steam page: let go of the pictures, keep the page to come back to.</summary>
    public void Pause()
    {
        _cancel.Cancel();
        _backdropDelay.Stop();
        _token++;
        BackdropChanged?.Invoke(null);
    }

    public void Resume()
    {
        if (_item is not { } item) return;
        _cancel = new CancellationTokenSource();
        _token++;
        Build();
        _ = LoadAsync(item, _token);
    }

    public void Focus()
    {
        if (_shots.Count > 0) _shots.Focus(0);
        else Keyboard.ClearFocus();
    }

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_item is not { } item) return false;
        switch (command)
        {
            case NavCommand.Back:
                Closed?.Invoke();
                return true;
            case NavCommand.Accept:
                Primary(item);
                return true;
            case NavCommand.Secondary:
                More(item);
                return true;
            case NavCommand.Up or NavCommand.Down or NavCommand.Mute or NavCommand.ColumnLeft or NavCommand.ColumnRight:
                return true;
        }
        return false;
    }

    // ---- Content ----------------------------------------------------------------------------

    private async Task LoadAsync(StoreItem item, int token)
    {
        try
        {
            var details = await SteamStore.DetailsAsync(item.AppId, _cancel.Token);
            if (token != _token || details is null) return;
            _details = details;
            Build();
            var first = details.Screenshots.FirstOrDefault().Full ?? details.Background;
            if (first is not null) _ = ShowBackdropAsync(first);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"Steam store: could not load the game page for {item.AppId}: {ex.Message}");
            if (token == _token) _facts.Text = "Couldn't load the details. Check your internet connection.";
        }
    }

    private void Build()
    {
        if (_item is not { } item) return;
        var d = _details;
        var (owned, installed) = Ownership(item.AppId);

        _title.Text = d?.Name is { Length: > 0 } name ? name : item.Name;

        _price.Children.Clear();
        if (owned)
        {
            _price.Children.Add(Pill(installed ? "Installed" : "In your library", light: true));
        }
        else
        {
            int discount = d?.DiscountPercent ?? item.DiscountPercent;
            string? price = d is null ? SteamStore.FormatPrice(item) : d.IsFree ? "Free to play" : d.Price;
            string? original = d is null ? (item.DiscountPercent > 0 ? SteamStore.FormatPrice(item, original: true) : null) : d.OriginalPrice;
            if (discount > 0) _price.Children.Add(Pill($"−{discount}%", light: true));
            if (price is not null) _price.Children.Add(Text(price, 22, FontWeights.SemiBold, "TextBrush"));
            if (original is not null)
            {
                var was = Text(original, 18, FontWeights.Normal, "SubtleTextBrush");
                was.TextDecorations = TextDecorations.Strikethrough;
                was.Margin = new Thickness(12, 0, 0, 0);
                _price.Children.Add(was);
            }
            if (d?.ComingSoon == true) _price.Children.Add(Text((price is null ? "" : "   ") + "Coming " + d.ReleaseDate, 18, FontWeights.Normal, "SoftTextBrush"));
        }

        _tags.Children.Clear();
        if (d is not null)
        {
            if (d.ControllerSupport == "full") _tags.Children.Add(Pill("  Full controller support", light: false));
            else if (d.ControllerSupport == "partial") _tags.Children.Add(Pill("  Partial controller support", light: false));
            foreach (var genre in d.Genres) _tags.Children.Add(Pill(genre, light: false));
            foreach (var feature in d.Features.Take(3)) _tags.Children.Add(Pill(feature, light: false));
        }
        _tags.Visibility = _tags.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _description.Text = d?.Description ?? "";
        _description.Visibility = _description.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var facts = new List<string>();
        if (d is not null)
        {
            if (!d.ComingSoon && d.ReleaseDate is { Length: > 0 } date) facts.Add(date);
            if (d.Developer is { Length: > 0 } dev) facts.Add(dev);
            if (d.Metacritic is { } score) facts.Add($"Metacritic {score}");
            if (d.Reviews is { } reviews) facts.Add(reviews.ToString("N0", CultureInfo.CurrentCulture) + " reviews");
            if (d.Platforms.Count > 0) facts.Add(string.Join(", ", d.Platforms));
        }
        _facts.Text = d is null ? "Loading…" : string.Join("  ·  ", facts);

        _actions.Children.Clear();
        string primary = owned ? (installed ? "Play" : "Install") : d?.IsFree == true ? "Get it in the Store" : "Buy in the Store";
        _actions.Children.Add(Ui.ActionPill(_resources, "A", primary, primary: true, () => Primary(item)));
        _actions.Children.Add(Ui.ActionPill(_resources, "X", owned ? "Achievements and more in Steam" : "Reviews, DLC and wishlist in Steam",
            primary: false, () => More(item)));

        BuildShots(d);
    }

    private void BuildShots(StoreDetails? d)
    {
        var shots = d?.Screenshots ?? [];
        bool same = _shots.Count == shots.Count && _shots.Count > 0;
        _shotsLabel.Visibility = shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _shots.View.Visibility = shots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (same) return;

        _shots.Clear();
        int token = _token;
        foreach (var (thumb, full) in shots)
        {
            var art = new Border { CornerRadius = new CornerRadius(18), Background = (Brush)_resources.FindResource("TileBrush") };
            var fullUrl = full;
            _shots.Add(art, _ =>
            {
                _backdropUrl = fullUrl;
                _backdropDelay.Stop();
                _backdropDelay.Start();
            }, () => { if (_item is { } i) Primary(i); });
            var thumbUrl = thumb;
            var cancel = _cancel.Token;
            _ = Task.Run(async () =>
            {
                var image = await WebImages.LoadAsync(thumbUrl, 600, cancel);
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
        if (shots.Count > 0 && _root.IsVisible) _shots.Focus(0);
    }

    /// <summary>The screenshot behind the page, lightly blurred so the page reads on top of it.</summary>
    private async Task ShowBackdropAsync(string url)
    {
        int token = _token;
        var image = await WebImages.LoadAsync(url, 1280, _cancel.Token, cache: false);
        if (image is null || token != _token || _item is null) return;
        BackdropChanged?.Invoke(ScreenCapture.SoftBlur(image));
    }

    private void Primary(StoreItem item)
    {
        var (owned, installed) = Ownership(item.AppId);
        if (installed) OpenLink?.Invoke(SteamLibrary.RunLink(item.AppId), false);
        else if (owned) OpenLink?.Invoke($"steam://install/{item.AppId}", false);
        else OpenStore?.Invoke(item.AppId);
    }

    /// <summary>X: the game in your Steam library (achievements, news) if it's yours, else its full store page.</summary>
    private void More(StoreItem item)
    {
        if (Ownership(item.AppId).Owned) OpenLink?.Invoke(SteamLibrary.GamePageLink(item.AppId), true);
        else OpenLink?.Invoke(SteamStore.ClientStoreLink(item.AppId), true);
    }

    private Border Pill(string text, bool light) => new()
    {
        Background = light ? Brushes.White : (Brush)_resources.FindResource("PaneBrush"),
        CornerRadius = new CornerRadius(15),
        Padding = new Thickness(14, 5, 14, 6),
        Margin = new Thickness(0, 0, 10, 8),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 14,
            FontWeight = light ? FontWeights.Bold : FontWeights.SemiBold,
            FontFamily = text.Length > 0 && text[0] >= '' && text[0] <= ''
                ? new FontFamily(((FontFamily)_resources.FindResource("UiFont")).Source + ", " + ((FontFamily)_resources.FindResource("IconFont")).Source)
                : (FontFamily)_resources.FindResource("UiFont"),
            Foreground = light ? (Brush)_resources.FindResource("SelectedTextBrush") : (Brush)_resources.FindResource("TextBrush"),
        },
    };

    private TextBlock Text(string text, double size, FontWeight weight, string brush) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        Foreground = (Brush)_resources.FindResource(brush),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 8),
    };
}
