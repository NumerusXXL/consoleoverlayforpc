using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows;
using ConsoleOverlay.Input;
using ConsoleOverlay.Overlay;
using ConsoleOverlay.Settings;
using Forms = System.Windows.Forms;

namespace ConsoleOverlay;

public partial class App : Application, ISettingsHost
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private HotkeyService? _hotkey;
    private ControllerService? _controller;
    private OverlayController? _overlay;
    private AppSettings _settings = new();
    private OverlayWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HookCrashHandlers();

        _singleInstance = new Mutex(initiallyOwned: true, "ConsoleOverlay.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Console Overlay is already running. Look for it in the system tray.", "Console Overlay");
            Shutdown();
            return;
        }

        Log.Reset();
        Log.Write("Starting.");
        _settings = SettingsStore.Load();

        var window = new OverlayWindow();
        _controller = new ControllerService(Dispatcher, ControllerButtons.None);
        _overlay = new OverlayController(window, _controller, _settings);
        _hotkey = new HotkeyService();

        _hotkey.Pressed += () => _overlay.Toggle();
        _controller.ComboPressed += () => _overlay.Toggle();
        window.SetSettingsHost(this);
        _window = window;

        CreateTray();
        ApplySettings(_settings, announce: false);
        Autostart.RepairPath();

        bool autostarted = Array.IndexOf(e.Args, Autostart.Argument) >= 0;
        Log.Write($"Version {typeof(App).Assembly.GetName().Version}, started {(autostarted ? "with Windows" : "by hand")}.");
        if (_settings.StartupNotification || !autostarted)
            ShowBalloon($"Running. Press {_settings.Hotkey} or {ButtonNames.Friendly(_settings.ParseCombo(out _))} on your controller.");
    }

    private void HookCrashHandlers()
    {
        // Errors on the UI thread: log them and keep running instead of vanishing from the tray.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Error: {args.Exception}");
            ShowBalloon($"Something went wrong: {args.Exception.Message} (details in the log)");
            _overlay?.Close(restorePrevious: true);
            args.Handled = true;
        };
        // Errors on other threads can't be recovered, but at least they end up in the log.
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write($"Fatal error: {args.ExceptionObject}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write($"Background error: {args.Exception}");
            args.SetObserved();
        };
    }

    private void ApplySettings(AppSettings settings, bool announce, bool quiet = false)
    {
        _settings = settings;
        _overlay!.UpdateSettings(settings);

        var combo = settings.ParseCombo(out var unknown);
        _controller!.Combo = combo;
        if (unknown.Count > 0 && !quiet)
            Warn($"Unknown controller buttons in settings: {string.Join(", ", unknown)}.");
        if (combo == ControllerButtons.None && !quiet)
            Warn("No controller combo set, so the controller can't open the overlay.");

        // The Settings page registers the shortcut itself; re-registering on every change would only flicker it.
        if (!quiet && !_hotkey!.Register(settings.Hotkey, out var error))
            Warn($"Keyboard shortcut not set: {error}");

        Log.Write($"Settings applied. Hotkey={settings.Hotkey}, Combo={combo}, ExclusiveFallback={settings.ExclusiveFullscreenFallback}.");
        if (announce)
            ShowBalloon("Settings reloaded.");
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open overlay", null, (_, _) => _overlay!.Open());
        menu.Items.Add("Settings", null, (_, _) => OpenSettingsPage());
        menu.Items.Add("Edit settings file", null, (_, _) => EditSettings());
        menu.Items.Add("Reload settings", null, (_, _) => ApplySettings(SettingsStore.Load(), announce: true));
        menu.Items.Add("Open log", null, (_, _) => OpenFile(Log.FilePath));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Console Overlay",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => _overlay!.Open();
    }

    private void OpenSettingsPage()
    {
        _overlay!.Open();
        _window?.ShowSettings();
    }

    // ---- ISettingsHost (the Settings page) ----------------------------------------------------

    AppSettings ISettingsHost.Settings => _settings;

    void ISettingsHost.Save()
    {
        SettingsStore.Save(_settings);
        ApplySettings(_settings, announce: false, quiet: true);
    }

    bool ISettingsHost.TrySetHotkey(string hotkey, out string? error)
    {
        if (!_hotkey!.Register(hotkey, out error))
        {
            _hotkey.Register(_settings.Hotkey, out _); // keep the old one working
            return false;
        }
        _settings.Hotkey = hotkey;
        SettingsStore.Save(_settings);
        Log.Write($"Keyboard shortcut is now {hotkey}.");
        return true;
    }

    void ISettingsHost.SuspendHotkey() => _hotkey?.Unregister();

    void ISettingsHost.ResumeHotkey() => _hotkey?.Register(_settings.Hotkey, out _);

    void ISettingsHost.CaptureCombo(Action<ControllerButtons> progress, Action<ControllerButtons?> done) =>
        _controller?.BeginCapture(progress, done);

    void ISettingsHost.CancelComboCapture() => _controller?.CancelCapture();

    void ISettingsHost.OpenSettingsFile() => EditSettings();

    void ISettingsHost.OpenLog()
    {
        _overlay?.Close(restorePrevious: false);
        OpenFile(Log.FilePath);
    }

    void ISettingsHost.SignOutOfDiscord() => _window?.SignOutOfDiscord();

    void ISettingsHost.Quit() => ExitApp();

    private void EditSettings()
    {
        _overlay?.Close(restorePrevious: false);
        SettingsStore.Save(_settings); // make sure the file exists and lists every option
        OpenFile(SettingsStore.FilePath);
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open {path}: {ex.Message}");
        }
    }

    private void Warn(string message)
    {
        Log.Write(message);
        ShowBalloon(message);
    }

    private void ShowBalloon(string message) =>
        _tray?.ShowBalloonTip(4000, "Console Overlay", message, Forms.ToolTipIcon.None);

    private void ExitApp()
    {
        _overlay?.Close(restorePrevious: true);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _controller?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
