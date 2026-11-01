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

public partial class App : Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private HotkeyService? _hotkey;
    private ControllerService? _controller;
    private OverlayController? _overlay;
    private AppSettings _settings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        window.EditSettingsRequested += EditSettings;
        window.ReloadSettingsRequested += () => ApplySettings(SettingsStore.Load(), announce: true);
        window.ExitRequested += ExitApp;

        CreateTray();
        ApplySettings(_settings, announce: false);
        ShowBalloon($"Running. Press {_settings.Hotkey} or {string.Join(" + ", _settings.ControllerCombo)} on your controller.");
    }

    private void ApplySettings(AppSettings settings, bool announce)
    {
        _settings = settings;
        _overlay!.UpdateSettings(settings);

        var combo = settings.ParseCombo(out var unknown);
        _controller!.Combo = combo;
        if (unknown.Count > 0)
            Warn($"Unknown controller buttons in settings: {string.Join(", ", unknown)}.");
        if (combo == ControllerButtons.None)
            Warn("No controller combo set, so the controller can't open the overlay.");

        if (!_hotkey!.Register(settings.Hotkey, out var error))
            Warn($"Keyboard shortcut not set: {error}");

        Log.Write($"Settings applied. Hotkey={settings.Hotkey}, Combo={combo}, ExclusiveFallback={settings.ExclusiveFullscreenFallback}.");
        if (announce)
            ShowBalloon("Settings reloaded.");
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open overlay", null, (_, _) => _overlay!.Open());
        menu.Items.Add("Edit settings", null, (_, _) => EditSettings());
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
