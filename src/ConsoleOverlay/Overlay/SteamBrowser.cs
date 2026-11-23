using System;
using System.Diagnostics;
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

/// <summary>What a <see cref="SteamBrowser"/> shows: Steam's web chat, or the Steam Store.</summary>
internal enum SteamWebMode { Chat, Store }

/// <summary>
/// Valve's own Steam web pages (the web chat on steamcommunity.com, the Store on store.steampowered.com) inside the
/// overlay, in Microsoft Edge WebView2, restyled to the overlay's look by our own style sheet
/// (<see cref="SteamWebStyle"/>). It is our browser showing Steam's public web pages, the same as Edge would: nothing
/// touches the Steam app. You sign in on Steam's own sign-in page (the QR code works with the Steam phone app), so the
/// overlay never sees your password; the sign-in is kept in %LOCALAPPDATA%\ConsoleOverlay\WebView2 and shared by the
/// chat and the Store.
///
/// Built to cost as little as possible: the browser starts only when a chat or the Store is opened and is shut down
/// completely (all its processes) when you leave it, so it uses no memory while you play. While it is open it runs
/// without a separate GPU process, with one page process, and without videos.
///
/// The controller drives the page through <see cref="WebControllerScript"/>: the D-pad moves a white ring, A clicks,
/// Y opens the on-screen keyboard (under the page, so the page stays readable), B goes back.
/// </summary>
internal sealed class SteamBrowser
{
    private static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConsoleOverlay", "WebView2");

    /// <summary>
    /// Switches that keep the browser small: no GPU process (the page is simple enough to draw in software), one page
    /// process, no spare page process, no back/forward page cache, no extensions, translate, shopping or SmartScreen
    /// helpers, no background updates.
    /// </summary>
    private const string LightArguments =
        "--disable-gpu --in-process-gpu --renderer-process-limit=1 --disable-extensions --disable-background-networking "
        + "--disable-component-update --no-first-run --disable-sync "
        + "--disable-features=Translate,msSmartScreenProtection,msEdgeShopping,msWebOOUI,msPdfOOUI,"
        + "AutofillServerCommunication,SpareRendererForSitePerProcess,BackForwardCache,MediaRouter";

    /// <summary>Used instead if the light switches ever fail on a PC (the drawing process crashing).</summary>
    private const string SafeArguments =
        "--renderer-process-limit=1 --disable-extensions --disable-background-networking --no-first-run "
        + "--disable-features=Translate,msEdgeShopping,SpareRendererForSitePerProcess,BackForwardCache";

    private static readonly Brush PageBrush = Frozen(Color.FromRgb(0x16, 0x1B, 0x23));
    private static readonly System.Drawing.Color PageColor = System.Drawing.Color.FromArgb(0x16, 0x1B, 0x23);

    private static Task<CoreWebView2Environment>? _environment;
    private static bool _safeMode;
    private static int _openCount;

    private readonly SteamWebMode _mode;
    private readonly FrameworkElement _resources;
    private readonly Grid _root = new();
    private readonly Grid _body = new();
    private readonly StackPanel _message = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _keyboardPanel;
    private readonly OnScreenKeyboard _keyboard;
    private WebView2? _web;
    private bool _ready;
    private string? _pendingUrl;
    private string? _openChatWith;
    private string? _homeUrl;
    private bool _keyboardOpen;
    private bool _memoryLogged;
    private int _session;

    /// <summary>B with nowhere to go back to: leave the page (the chat closes; the Store goes back to the game).</summary>
    public event Action? Closed;

    /// <summary>A steam:// link on the page (Play, Install, open in the Steam app): close the overlay and open it.</summary>
    public event Action<string>? OpenLink;

    /// <summary>The prompts or the page's title changed.</summary>
    public event Action? HintChanged;

    public SteamBrowser(FrameworkElement resources, SteamWebMode mode)
    {
        _resources = resources;
        _mode = mode;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // The page sits in a rounded pane of the same colour as the page's own background, so its square edges
        // disappear into the glass card around it.
        var frame = new Border { CornerRadius = new CornerRadius(20), Background = PageBrush, Padding = new Thickness(8), Child = _body };
        _body.Children.Add(_message);
        Grid.SetRow(frame, 0);
        _root.Children.Add(frame);

        // The keyboard opens under the page (a web page can't have anything drawn over it), and the page gets
        // shorter: its message box and latest messages stay just above the keys.
        _keyboard = new OnScreenKeyboard(resources) { EnterLabel = mode == SteamWebMode.Chat ? "Send" : "Done", Compact = true };
        if (_keyboard.View is Border keys) keys.Background = Brushes.Transparent;
        _keyboard.Typed += t => _ = Script($"__co.type({JsonSerializer.Serialize(t)})");
        _keyboard.Backspace += () => _ = Script("__co.back()");
        _keyboard.Enter += () => _ = EnterAsync();
        _keyboard.Closed += () => ShowKeyboard(false);
        _keyboardPanel = new Border
        {
            Child = _keyboard.View,
            CornerRadius = new CornerRadius(26),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)resources.FindResource("GlassEdgeBrush"),
            Background = Frozen(Color.FromArgb(0xF0, 0x14, 0x18, 0x20)),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetRow(_keyboardPanel, 1);
        _root.Children.Add(_keyboardPanel);
    }

    public FrameworkElement View => _root;

    /// <summary>A page is open (the browser is running for it).</summary>
    public bool IsOpen { get; private set; }

    public bool KeyboardOpen => _keyboardOpen;

    /// <summary>The address of the page on screen (to open it again later, or in the Steam app).</summary>
    public string? Url => _ready ? _web?.CoreWebView2?.Source : _pendingUrl;

    /// <summary>The page's title, without "Steam Community ::" and the like.</summary>
    public string? Title
    {
        get
        {
            string? title = _ready ? _web?.CoreWebView2?.DocumentTitle : null;
            if (string.IsNullOrWhiteSpace(title)) return null;
            foreach (var prefix in new[] { "Steam Community :: ", "Steam Store :: " })
                if (title.StartsWith(prefix, StringComparison.Ordinal)) title = title[prefix.Length..];
            return title.Replace(" on Steam", "", StringComparison.Ordinal).Trim();
        }
    }

    /// <summary>What X does, for the button prompts (the page around it decides).</summary>
    public string SecondaryLabel { get; set; } = "open in Steam";

    public string Hint => _keyboardOpen
        ? _keyboard.Hint
        : _mode == SteamWebMode.Chat
            ? $"A select · Y type · X {SecondaryLabel} · LT / RT scroll · B {(CanGoBack ? "back" : "close chat")} · LB / RB other pages"
            : $"A select · Y search or type · X {SecondaryLabel} · B {(CanGoBack ? "back" : "back to game")} · LT / RT library and friends · LB / RB other pages";

    private bool CanGoBack => _ready && _web?.CoreWebView2 is { CanGoBack: true } core
        && !(_mode == SteamWebMode.Chat && IsChatPage(core.Source));

    /// <summary>True when the Edge WebView2 runtime is installed (it is on Windows 11 and up-to-date Windows 10).</summary>
    public static bool Available()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString() is { Length: > 0 }; }
        catch { return false; }
    }

    /// <summary>
    /// Opens <paramref name="url"/>, starting the browser if it isn't running. <paramref name="chatWith"/>: on the web
    /// chat, open the chat with the friend of this name.
    /// </summary>
    public void Open(string url, string? chatWith = null)
    {
        _openChatWith = chatWith;
        _homeUrl = url;
        ShowKeyboard(false);
        _root.Visibility = Visibility.Visible;

        if (!Available())
        {
            IsOpen = true;
            ShowMessage("Microsoft Edge WebView2 is needed here",
                "Install the WebView2 Runtime from Microsoft (it's free and small), then try again.");
            return;
        }
        if (_ready && _web?.CoreWebView2 is { } core)
        {
            core.Navigate(url);
            FocusPage();
            return;
        }
        _pendingUrl = url;
        if (!IsOpen)
        {
            IsOpen = true;
            _ = CreateAsync(++_session);
        }
    }

    /// <summary>Shuts the browser down and frees all its memory. The sign-in stays saved.</summary>
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _session++;
        _ready = false;
        _pendingUrl = null;
        _openChatWith = null;
        _memoryLogged = false;
        ShowKeyboard(false);
        ShowMessage(null, null);
        if (_web is { } web)
        {
            _web = null;
            _body.Children.Remove(web);
            try { web.Dispose(); }
            catch (Exception ex) { Log.Write($"Steam {Name}: closing the browser failed: {ex.Message}"); }
        }
        // When nothing else uses it, the browser's own processes quit; the next page starts a fresh one.
        if (--_openCount <= 0)
        {
            _openCount = 0;
            _environment = null;
        }
    }

    /// <summary>Signs out of Steam in the overlay's browser: deletes its saved cookies and data.</summary>
    public static async Task<bool> SignOutAsync(params SteamBrowser?[] open)
    {
        foreach (var browser in open) browser?.Close();
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (Directory.Exists(DataFolder)) Directory.Delete(DataFolder, recursive: true);
                _environment = null;
                Log.Write("Steam: signed out of the overlay's browser.");
                return true;
            }
            catch (IOException) { await Task.Delay(700); } // the browser's processes take a moment to quit
            catch (UnauthorizedAccessException) { await Task.Delay(700); }
        }
        Log.Write("Steam: could not delete the browser's saved data; it is still in use.");
        return false;
    }

    /// <summary>The page fills this height (a web page can't be scrolled with the overlay's own pages).</summary>
    public void SetHeight(double height) => _root.Height = Math.Max(260, height);

    public bool Handle(NavCommand command, IInputElement? focused)
    {
        if (!IsOpen) return false;
        if (_keyboardOpen)
        {
            if (!_keyboard.Contains(focused))
            {
                // The page took focus (a click with the mouse): the keys get it back.
                _keyboard.FocusFirst();
                if (command is NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right or NavCommand.Accept)
                    return true;
            }
            if (_keyboard.Handle(command)) return true; // X delete, Y space, LT shift, RT send, B close
            // D-pad and A move between and press keys the overlay's usual way; LB / RB still change pages.
            return command is not (NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right
                or NavCommand.Accept or NavCommand.PreviousTab or NavCommand.NextTab);
        }

        switch (command)
        {
            case NavCommand.Up: _ = Move("up"); return true;
            case NavCommand.Down: _ = Move("down"); return true;
            case NavCommand.Left: _ = Move("left"); return true;
            case NavCommand.Right: _ = Move("right"); return true;
            case NavCommand.Accept: _ = ActivateAsync(); return true;
            case NavCommand.ColumnLeft when _mode == SteamWebMode.Chat: _ = Script("__co.scroll(-1)"); return true;
            case NavCommand.ColumnRight when _mode == SteamWebMode.Chat: _ = Script("__co.scroll(1)"); return true;
            case NavCommand.Mute:
                _ = TypeAsync();
                return true;
            case NavCommand.Back:
                if (CanGoBack) _web!.CoreWebView2.GoBack();
                else Closed?.Invoke();
                return true;
        }
        return false; // X, LT / RT in the Store, LB / RB: the page around it
    }

    // ---- WebView2 ---------------------------------------------------------------------------

    private string Name => _mode == SteamWebMode.Chat ? "chat" : "Store";

    private static Task<CoreWebView2Environment> SharedEnvironment()
    {
        if (_environment is { IsFaulted: false } running) return running;
        var options = new CoreWebView2EnvironmentOptions(_safeMode ? SafeArguments : LightArguments);
        return _environment = CoreWebView2Environment.CreateAsync(null, DataFolder, options);
    }

    private async Task CreateAsync(int session)
    {
        ShowMessage(_mode == SteamWebMode.Chat ? "Opening Steam chat…" : "Opening the Steam Store…", null);
        _openCount++;
        var timer = Stopwatch.StartNew();
        try
        {
            var web = new WebView2 { DefaultBackgroundColor = PageColor };
            _web = web;
            _body.Children.Add(web);
            var environment = await SharedEnvironment();
            await web.EnsureCoreWebView2Async(environment);
            if (session != _session || _web != web) return; // closed meanwhile
            var core = web.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(WebControllerScript.Source);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(SteamWebStyle.Script);
            if (session != _session) return;

            // Videos (store trailers, animated backgrounds) are never downloaded.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
            core.WebResourceRequested += (_, e) =>
                e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not loaded in the overlay", "");

            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenLink?.Invoke(e.Uri);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (session != _session) return;
                ShowMessage(null, null);
                if (!e.IsSuccess)
                    Log.Write($"Steam {Name}: a page didn't load ({e.WebErrorStatus}).");
                OnPageLoaded(core.Source);
                HintChanged?.Invoke();
            };
            // Pop-ups (payment providers, "open in a new tab") open in place, so B comes back from them.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.Uri.StartsWith("steam:", StringComparison.OrdinalIgnoreCase)) OpenLink?.Invoke(e.Uri);
                else core.Navigate(e.Uri);
            };
            core.DocumentTitleChanged += (_, _) => HintChanged?.Invoke();
            core.WebMessageReceived += (_, e) =>
            {
                // Esc pressed on a real keyboard while the page has focus.
                if (e.TryGetWebMessageAsString() == "escape")
                    Handle(NavCommand.Back, null);
            };
            core.ProcessFailed += (_, e) =>
            {
                Log.Write($"Steam {Name}: a browser process stopped ({e.ProcessFailedKind}, {e.Reason}).");
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.GpuProcessExited
                    or CoreWebView2ProcessFailedKind.BrowserProcessExited && !_safeMode)
                {
                    _safeMode = true; // next time, start with ordinary settings
                    Log.Write("Steam: the browser will start with ordinary settings from now on.");
                }
            };

            web.SizeChanged += (_, _) => ApplyZoom();
            web.Loaded += (_, _) => ApplyZoom();
            _ready = true;
            ApplyZoom();
            Log.Write($"Steam {Name}: browser started in {timer.ElapsedMilliseconds} ms{(_safeMode ? " (ordinary settings)" : "")}.");
            if (_pendingUrl is { } url)
            {
                _pendingUrl = null;
                core.Navigate(url);
            }
            FocusPage();
        }
        catch (Exception ex)
        {
            Log.Write($"Steam {Name}: could not start WebView2: {ex}");
            if (!_safeMode) _safeMode = true;
            var message = _mode == SteamWebMode.Chat ? "Steam chat couldn't open" : "The Steam Store couldn't open";
            Close();
            IsOpen = true; // keep the message on screen; B leaves
            ShowMessage(message, "Press B, then try again.");
        }
    }

    private void OnPageLoaded(string url)
    {
        if (!_memoryLogged)
        {
            _memoryLogged = true;
            _ = LogMemoryAsync(_session);
        }
        if (_mode == SteamWebMode.Chat)
        {
            if (_openChatWith is null) return;
            if (IsChatPage(url)) _ = OpenChatAsync(_openChatWith, _session);
            // Signed in on a page that didn't lead back to the chat: go there.
            else if (!IsSignInPage(url) && _homeUrl is { } home) _web?.CoreWebView2?.Navigate(home);
        }
        else
        {
            _ = Script("__co.home()"); // the ring starts on the page's main button (Add to Cart, Play, Continue)
        }
    }

    /// <summary>
    /// The overlay is scaled up for TVs; the page is zoomed the same so its text is as readable, but never so far that
    /// the page stops fitting (the Store is about 1200 px wide, the chat needs about 640).
    /// </summary>
    private void ApplyZoom()
    {
        if (_web is null || !_ready || _web.ActualWidth <= 0) return;
        var window = Window.GetWindow(_web);
        if (window is null) return;
        try
        {
            var transform = _web.TransformToAncestor(window);
            var a = transform.Transform(new Point(0, 0));
            var b = transform.Transform(new Point(100, 0));
            double scale = (b.X - a.X) / 100;
            double width = _web.ActualWidth * scale;
            double fit = width / (_mode == SteamWebMode.Store ? 1240 : 640);
            // At least 120 % so it reads from the sofa, more on a TV-scaled overlay.
            double wanted = Math.Max(1.2, scale * (_mode == SteamWebMode.Chat ? 1.1 : 1.0));
            double zoom = Math.Clamp(Math.Min(wanted, fit), 0.8, 2.5);
            if (Math.Abs(_web.ZoomFactor - zoom) > 0.01) _web.ZoomFactor = zoom;
        }
        catch (InvalidOperationException) { } // not in the window yet
    }

    private void FocusPage()
    {
        if (_web is null || _keyboardOpen) return;
        _web.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_web is null || _keyboardOpen || !_web.IsVisible) return;
            _web.Focus(); // a real keyboard types straight into the page
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
            Log.Write($"Steam {Name}: script failed: {ex.Message}");
            return null;
        }
    }

    private Task Move(string direction) => Script($"__co.move('{direction}')");

    private async Task ActivateAsync()
    {
        if (await Script("__co.activate()") == "text") ShowKeyboard(true);
    }

    /// <summary>Y: type. In the chat into the message box; in the Store into the field the ring is on, or Search.</summary>
    private async Task TypeAsync()
    {
        string target = _mode == SteamWebMode.Chat ? "__co.focusMessage()" : "__co.focusSearch()";
        if (await Script(target) is "none" or null && _mode == SteamWebMode.Chat) return;
        ShowKeyboard(true);
    }

    private async Task EnterAsync()
    {
        await Script("__co.enter()");
        // In the Store, Enter runs the search: the keys go away so the results can be browsed.
        if (_mode == SteamWebMode.Store) ShowKeyboard(false);
    }

    /// <summary>Steam's web chat loads its friends list after the page: look for the friend for a while.</summary>
    private async Task OpenChatAsync(string name, int session)
    {
        for (int attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(attempt == 0 ? 400 : 750);
            if (session != _session || _openChatWith != name) return;
            if (await Script($"__co.openByName({JsonSerializer.Serialize(name)})") != "opened") continue;
            _openChatWith = null;
            await Task.Delay(500);
            // Only their conversation: the friends list steps aside (the overlay has its own).
            string layout = await Script("__co.chatMode()") ?? "none";
            await Script("__co.focusMessage()");
            Log.Write($"Steam chat: opened the chat with a friend (layout: {layout}).");
            HintChanged?.Invoke();
            return;
        }
        if (session != _session) return;
        _openChatWith = null;
        Log.Write("Steam chat: didn't find the friend in the web chat's list (signed in?); the list stays visible.");
    }

    private void ShowKeyboard(bool show)
    {
        if (_keyboardOpen == show) return;
        _keyboardOpen = show;
        _keyboardPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) _keyboard.FocusFirst();
        else FocusPage();
        HintChanged?.Invoke();
    }

    private async Task LogMemoryAsync(int session)
    {
        await Task.Delay(5000); // once the page has settled
        if (session != _session || _web?.CoreWebView2 is not { } core) return;
        try
        {
            long total = 0;
            int count = 0;
            foreach (var info in core.Environment.GetProcessInfos())
            {
                try
                {
                    using var process = Process.GetProcessById(info.ProcessId);
                    total += process.PrivateMemorySize64;
                    count++;
                }
                catch (ArgumentException) { } // already gone
            }
            Log.Write($"Steam {Name}: the browser uses {count} processes, {total / (1024 * 1024)} MB in total.");
        }
        catch (Exception ex)
        {
            Log.Write($"Steam {Name}: could not measure the browser's memory: {ex.Message}");
        }
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

    private static bool IsChatPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/chat", StringComparison.OrdinalIgnoreCase);

    private static bool IsSignInPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.StartsWith("login.", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("login", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("openid", StringComparison.OrdinalIgnoreCase));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
