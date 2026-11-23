using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConsoleOverlay.Input;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// The Store tab: Valve's own Steam Store web page (store.steampowered.com) in the overlay's lightweight browser
/// (<see cref="SteamBrowser"/>), restyled for a TV and a controller by the overlay's style sheet: the overlay's font,
/// white pill buttons, rounded game pictures, frosted panels, no footer or side clutter. Everything the Store has
/// works, including your wishlist, the cart and paying, because it is the real Store; you sign in once on Steam's own
/// sign-in page (shared with the chat).
///
/// The D-pad moves the white ring between links and buttons, A clicks, Y searches (or types into the field the ring
/// is on), X opens the page in the Steam app, B goes back a page (or to the game from the Store's first page), and
/// LT / RT still switch to the library and friends. The browser runs only while the Store tab is on screen.
/// </summary>
internal sealed class SteamStoreView
{
    private const string Home = "https://store.steampowered.com/";

    private readonly Border _card;
    private readonly SteamBrowser _browser;
    private string? _lastUrl;
    private DateTime _leftAt;
    private bool _active;

    /// <summary>A steam:// link, and whether to open Big Picture first.</summary>
    public event Action<string, bool>? OpenLink;

    /// <summary>B on the Store's first page.</summary>
    public event Action? BackToGame;

    public event Action? HintChanged;

    public SteamStoreView(FrameworkElement resources)
    {
        _browser = new SteamBrowser(resources, SteamWebMode.Store) { SecondaryLabel = "open in Steam" };
        _browser.Closed += () => BackToGame?.Invoke();
        _browser.OpenLink += link => OpenLink?.Invoke(link, false);
        _browser.HintChanged += () => HintChanged?.Invoke();
        _card = new Border
        {
            CornerRadius = new CornerRadius(30),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 14, 0, 0),
            Background = (Brush)resources.FindResource("GlassBrush"),
            BorderBrush = (Brush)resources.FindResource("GlassEdgeBrush"),
            Child = _browser.View,
        };
        _card.IsVisibleChanged += (_, _) => Fit();
    }

    public FrameworkElement View => _card;

    public SteamBrowser Browser => _browser;

    public string Hint => _browser.Hint;

    public string? Subtitle => _browser.Title is { Length: > 0 } title ? "Store · " + Shorten(title) : "Store";

    /// <summary>The Store tab is on screen: start the browser, on the page you left (for a while) or the Store's front page.</summary>
    public void Activate()
    {
        _active = true;
        bool recent = DateTime.Now - _leftAt < TimeSpan.FromMinutes(15);
        string url = recent && _lastUrl is { } last && !last.Contains("checkout.", StringComparison.OrdinalIgnoreCase) ? last : Home;
        _browser.Open(url);
        Fit();
        HintChanged?.Invoke();
    }

    /// <summary>Another tab or page, or the overlay closed: the browser shuts down and frees its memory.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        if (_browser.Url is { } url) _lastUrl = url;
        _leftAt = DateTime.Now;
        _browser.Close();
    }

    /// <summary>Opens a game's store page (to buy it); the tab must be on screen (or about to be).</summary>
    public void OpenApp(int appId)
    {
        _lastUrl = $"https://store.steampowered.com/app/{appId}/";
        _leftAt = DateTime.Now;
        if (_active) _browser.Open(_lastUrl);
    }

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_browser.Handle(command, focused)) return true;
        if (command == NavCommand.Secondary)
        {
            if (_browser.Url is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                OpenLink?.Invoke("steam://openurl/" + url, true);
            return true;
        }
        return false; // LT / RT: library and friends; LB / RB: other pages
    }

    public Task<bool> SignOutAsync() => SteamBrowser.SignOutAsync(_browser);

    /// <summary>The page fills the space under the tabs, down to the bottom of the overlay's page area.</summary>
    private void Fit()
    {
        if (!_card.IsVisible) return;
        _card.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            DependencyObject? node = _card;
            while (node is not null && node is not ScrollViewer)
                node = VisualTreeHelper.GetParent(node);
            double height = 560;
            if (node is ScrollViewer scroller && scroller.ViewportHeight > 0 && scroller.Content is Visual content)
            {
                try
                {
                    double top = _card.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
                    height = scroller.ViewportHeight - top - _card.Padding.Top - _card.Padding.Bottom - 26;
                    scroller.ScrollToTop();
                }
                catch (InvalidOperationException) { } // not laid out yet
            }
            _browser.SetHeight(height);
        });
    }

    private static string Shorten(string title) => title.Length > 48 ? title[..46].TrimEnd() + "…" : title;
}
