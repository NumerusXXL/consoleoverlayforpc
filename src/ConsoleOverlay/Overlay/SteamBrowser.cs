using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ConsoleOverlay.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ConsoleOverlay.Overlay;

/// <summary>
/// Valve's own web pages (the Steam Store, its checkout, Steam web chat) inside the overlay, in Microsoft Edge
/// WebView2. You sign in on Steam's own sign-in page (the QR code works with the Steam phone app) and buy on Steam's own
/// checkout, so the overlay never sees your password or payment details. The sign-in is remembered in
/// %LOCALAPPDATA%\ConsoleOverlay\WebView2.
///
/// The controller drives the page through <see cref="WebControllerScript"/>: D-pad moves a white ring, A clicks,
/// Y opens the on-screen keyboard, LT / RT scroll, X opens the page in the Steam app, B goes back.
/// </summary>
internal sealed class SteamBrowser
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConsoleOverlay", "WebView2");
    private static Task<CoreWebView2Environment>? _environment;

    private readonly FrameworkElement _resources;
    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly TextBlock _title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _address = new() { FontSize = 14, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _message = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly OnScreenKeyboard _keyboard;
    private readonly DispatcherTimer _idle;
    private WebView2? _web;
    private bool _ready;
    private string? _pendingUrl;
    private string? _openChatWith;
    private int _openChatTries;
    private bool _keyboardOpen;
    private bool _shown;

    /// <summary>B on the first page: leave the browser.</summary>
    public event Action? Closed;

    /// <summary>A steam:// link (Play, Install, open in the Steam app): close the overlay and open it.</summary>
    public event Action<string>? OpenLink;

    public event Action? HintChanged;

    public SteamBrowser(FrameworkElement resources)
    {
        _resources = resources;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        _status.Foreground = (Brush)resources.FindResource("SubtleTextBrush");
        _address.Foreground = (Brush)resources.FindResource("SubtleTextBrush");
        DockPanel.SetDock(_status, Dock.Right);
        header.Children.Add(_status);
        var lockIcon = Ui.Icon(resources, "", 16);
        lockIcon.Margin = new Thickness(0, 0, 10, 0);
        lockIcon.Foreground = (Brush)resources.FindResource("SoftTextBrush");
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(lockIcon);
        left.Children.Add(_title);
        left.Children.Add(_address);
        header.Children.Add(left);
        Grid.SetRow(header, 0);
        _root.Children.Add(header);

        // The page itself sits on a rounded dark pane so its square edges don't look pasted onto the glass.
        var frame = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x28, 0x38)),
            Padding = new Thickness(6),
            Child = _body,
        };
        _body.Children.Add(_message);
        Grid.SetRow(frame, 1);
        _root.Children.Add(frame);

        _keyboard = new OnScreenKeyboard(resources) { EnterLabel = "Enter" };
        _keyboard.View.Margin = new Thickness(0, 12, 0, 0);
        _keyboard.View.Visibility = Visibility.Collapsed;
        _keyboard.Typed += t => _ = Script($"__co.type({JsonSerializer.Serialize(t)})");
        _keyboard.Backspace += () => _ = Script("__co.back()");
        _keyboard.Enter += () => _ = Script("__co.enter()");
        _keyboard.Closed += () => ShowKeyboard(false);
        Grid.SetRow(_keyboard.View, 2);
        _root.Children.Add(_keyboard.View);

        // The browser takes a lot of memory, so it is closed a few minutes after you leave it.
        _idle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(4) };
        _idle.Tick += (_, _) =>
        {
            _idle.Stop();
            if (!_shown) Dispose();
        };
    }

    public FrameworkElement View => _root;

    /// <summary>A page is open (shown, or kept in the background while you are on another page).</summary>
    public bool IsOpen { get; private set; }

    public string Hint => _keyboardOpen
        ? _keyboard.Hint
        : "A select · B back · Y keyboard · X open in Steam · LT / RT scroll · LB / RB other pages";

    /// <summary>True when the Edge WebView2 runtime is installed (it is on Windows 11 and up-to-date Windows 10).</summary>
    public static bool Available()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 }; }
        catch { return false; }
    }

    /// <summary>Opens <paramref name="url"/>. <paramref name="chatWith"/>: on Steam web chat, open this friend's chat.</summary>
    public void Open(string url, string title, string? chatWith = null)
    {
        IsOpen = true;
        _title.Text = title;
        _address.Text = Host(url);
        _openChatWith = chatWith;
        _openChatTries = 0;
        ShowKeyboard(false);
        Show();

        if (!Available())
        {
            ShowMessage("Microsoft Edge WebView2 is needed for the store",
                "Install the WebView2 Runtime from Microsoft (it's free), or press X to open this page in the Steam app.");
            _pendingUrl = url;
            return;
        }
        if (_ready && _web?.CoreWebView2 is { } core)
        {
            core.Navigate(url);
            FocusPage();
            return;
        }
        _pendingUrl = url;
        _ = CreateAsync();
    }

    public void Show()
    {
        _shown = true;
        _idle.Stop();
        _root.Visibility = Visibility.Visible;
        if (_web is not null) _web.Visibility = Visibility.Visible;
        FocusPage();
    }

    /// <summary>Hidden while you are on another page; the page stays as it was for a few minutes.</summary>
    public void Hide()
    {
        _shown = false;
        if (_web is not null) _web.Visibility = Visibility.Collapsed;
        _root.Visibility = Visibility.Collapsed;
        if (IsOpen) _idle.Start();
    }

    /// <summary>Closes the browser and frees its memory. The sign-in stays saved.</summary>
    public void Dispose()
    {
        _idle.Stop();
        IsOpen = false;
        _ready = false;
        _pendingUrl = null;
        if (_web is null) return;
        _body.Children.Remove(_web);
        try { _web.Dispose(); }
        catch (Exception ex) { Log.Write($"Browser: closing it failed: {ex.Message}"); }
        _web = null;
    }

    /// <summary>Signs out of Steam in the overlay's browser: deletes its saved cookies and data.</summary>
    public static async Task<bool> SignOutAsync(SteamBrowser? open)
    {
        open?.Dispose();
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, recursive: true);
                _environment = null;
                return true;
            }
            catch (IOException) { await Task.Delay(700); } // the browser's own processes take a moment to quit
            catch (UnauthorizedAccessException) { await Task.Delay(700); }
        }
        Log.Write("Browser: could not delete its saved data; it is still in use.");
        return false;
    }

    /// <summary>Lays the page out to fit: the browser can't be scrolled like the overlay's own pages.</summary>
    public void SetHeight(double height) => _root.Height = Math.Max(300, height);

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (_keyboardOpen && _keyboard.Contains(focused))
        {
            // D-pad and A move between and press keys the usual way; LB / RB still change pages.
            return _keyboard.Handle(command);
        }

        switch (command)
        {
            case NavCommand.Up: _ = Move("up"); return true;
            case NavCommand.Down: _ = Move("down"); return true;
            case NavCommand.Left: _ = Move("left"); return true;
            case NavCommand.Right: _ = Move("right"); return true;
            case NavCommand.Accept: _ = ActivateAsync(); return true;
            case NavCommand.ColumnLeft: _ = Script("__co.scroll(-1)"); return true;
            case NavCommand.ColumnRight: _ = Script("__co.scroll(1)"); return true;
            case NavCommand.Mute:
                if (_keyboardOpen) _keyboard.FocusFirst();
                else ShowKeyboard(true);
                return true;
            case NavCommand.Secondary:
                var url = _web?.CoreWebView2?.Source ?? _pendingUrl;
                if (url is not null) OpenLink?.Invoke("steam://openurl/" + url);
                return true;
            case NavCommand.Back:
                if (_keyboardOpen) ShowKeyboard(false);
                else if (_web?.CoreWebView2 is { CanGoBack: true } core) core.GoBack();
                else Closed?.Invoke();
                return true;
        }
        return false;
    }

    // ---- WebView2 ---------------------------------------------------------------------------

    private async Task CreateAsync()
    {
        if (_web is not null) return;
        ShowMessage("Opening Steam…", null);
        try
        {
            var web = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1B, 0x28, 0x38),
                Visibility = _shown ? Visibility.Visible : Visibility.Collapsed,
            };
            _web = web;
            _body.Children.Add(web);
            _environment ??= CoreWebView2Environment.CreateAsync(null, DataFolder);
            var environment = await _environment;
            await web.EnsureCoreWebView2Async(environment);
            if (_web != web) return; // closed meanwhile
            var core = web.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebControllerScript.Source);

            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenLink?.Invoke(e.Uri);
                    return;
                }
                _status.Text = "Loading…";
                _address.Text = Host(e.Uri);
            };
            core.NavigationCompleted += (_, e) =>
            {
                _status.Text = e.IsSuccess ? "" : "Couldn't load the page";
                _address.Text = Host(core.Source);
                ShowMessage(null, null);
                if (_openChatWith is not null) _ = OpenChatAsync();
                else if (e.IsSuccess && Host(core.Source).EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase))
                    _ = PlaceRingAsync();
            };
            // Pop-ups (payment providers, "open in new tab") open in the same view, so B comes back from them.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.Uri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) OpenLink?.Invoke(e.Uri);
                else core.Navigate(e.Uri);
            };
            core.DocumentTitleChanged += (_, _) => HintChanged?.Invoke();

            web.Loaded += (_, _) => ApplyZoom();
            web.SizeChanged += (_, _) => ApplyZoom();
            ApplyZoom();
            _ready = true;
            if (_pendingUrl is { } url)
            {
                _pendingUrl = null;
                core.Navigate(url);
            }
            FocusPage();
        }
        catch (Exception ex)
        {
            Log.Write($"Browser: could not start WebView2: {ex}");
            _environment = null;
            Dispose();
            IsOpen = true; // keep the message on screen; B leaves
            ShowMessage("The store couldn't open here", "Press X to open it in the Steam app instead.");
        }
    }

    /// <summary>The overlay is scaled up for TVs; the web page is zoomed the same so its text is just as readable.</summary>
    private void ApplyZoom()
    {
        if (_web is null || !_ready) return;
        var window = Window.GetWindow(_web);
        if (window is null) return;
        try
        {
            var transform = _web.TransformToAncestor(window);
            var a = transform.Transform(new Point(0, 0));
            var b = transform.Transform(new Point(100, 0));
            double scale = Math.Clamp((b.X - a.X) / 100, 1, 2.5);
            if (Math.Abs(_web.ZoomFactor - scale) > 0.01) _web.ZoomFactor = scale;
        }
        catch (InvalidOperationException) { } // not in the window yet
    }

    private void FocusPage()
    {
        if (_web is null || !_shown || _keyboardOpen) return;
        _web.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_web is null || !_shown) return;
            _web.Focus();
        });
    }

    private async Task<string?> Script(string code)
    {
        if (!_ready || _web?.CoreWebView2 is not { } core) return null;
        try
        {
            var result = await core.ExecuteScriptAsync($"window.__co ? {code} : 'none'");
            return result.Trim('"');
        }
        catch (Exception ex)
        {
            Log.Write($"Browser: script failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>On a store page the ring starts on its main button (Add to Cart, Purchase, Continue).</summary>
    private async Task PlaceRingAsync()
    {
        await Task.Delay(700); // the page lays itself out after it has loaded
        await Script("__co.home()");
    }

    private Task Move(string direction) => Script($"__co.move('{direction}')");

    private async Task ActivateAsync()
    {
        var result = await Script("__co.activate()");
        if (result == "text") ShowKeyboard(true);
    }

    /// <summary>Steam web chat loads its friends list after the page: look for the friend for a few seconds.</summary>
    private async Task OpenChatAsync()
    {
        var name = _openChatWith;
        if (name is null) return;
        while (_openChatWith == name && _openChatTries++ < 15)
        {
            await Task.Delay(1000);
            if (_openChatWith != name) return;
            if (await Script($"__co.openByName({JsonSerializer.Serialize(name)})") == "opened")
            {
                _openChatWith = null;
                await Task.Delay(600);
                ShowKeyboard(true); // ready to type
                return;
            }
        }
        _openChatWith = null;
    }

    private void ShowKeyboard(bool show)
    {
        if (_keyboardOpen == show) return;
        _keyboardOpen = show;
        _keyboard.View.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) _keyboard.FocusFirst();
        else FocusPage();
        HintChanged?.Invoke();
    }

    private void ShowMessage(string? title, string? detail)
    {
        _message.Children.Clear();
        if (title is null)
        {
            _message.Visibility = Visibility.Collapsed;
            return;
        }
        _message.Visibility = Visibility.Visible;
        _message.Children.Add(new TextBlock { Text = title, Style = (Style)_resources.FindResource("HeroTitle"), FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center });
        if (detail is not null)
            _message.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 16,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 640,
                Foreground = (Brush)_resources.FindResource("SoftTextBrush"),
            });
    }

    private static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http") ? uri.Host : "";
}
