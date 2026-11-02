using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ConsoleOverlay.Input;

namespace ConsoleOverlay.Settings;

public sealed class AppSettings
{
    /// <summary>Keyboard shortcut that opens and closes the overlay.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+G";

    /// <summary>
    /// Controller buttons that must be held together to open and close the overlay.
    /// Names: A, B, X, Y, DPadUp, DPadDown, DPadLeft, DPadRight, Start, Back, LeftStick, RightStick,
    /// LeftShoulder, RightShoulder, LeftTrigger, RightTrigger, Guide.
    /// Default is View + Menu (Back + Start).
    /// </summary>
    public List<string> ControllerCombo { get; set; } = ["Back", "Start"];

    /// <summary>
    /// When the game in front is in exclusive fullscreen, minimize it and open the overlay as a normal
    /// window, then bring the game back on close. When false the overlay won't open over those games.
    /// </summary>
    public bool ExclusiveFullscreenFallback { get; set; } = true;

    /// <summary>Process names (for example "oldgame.exe") the exclusive fullscreen fallback should skip.</summary>
    public List<string> ExclusiveFullscreenExcludedGames { get; set; } = [];

    /// <summary>
    /// Client ID of your own (free) Discord app, from the Discord Developer Portal. Needed for the Discord page.
    /// See "Discord voice controls" in the README.
    /// </summary>
    public string DiscordClientId { get; set; } = "";

    /// <summary>Client Secret of the same Discord app. It stays on this PC and is only sent to Discord.</summary>
    public string DiscordClientSecret { get; set; } = "";

    /// <summary>Color of the focus ring and the current page's pill: White, Blue, Green, Purple, Orange or Pink.</summary>
    public string AccentColor { get; set; } = "White";

    /// <summary>What shows behind the overlay: Blur (the game, blurred), Clear (the game, sharp) or Dark.</summary>
    public string Background { get; set; } = "Blur";

    /// <summary>How big everything is: Standard (desk), Big (fits the screen, good for a TV) or Huge.</summary>
    public string UiSize { get; set; } = "Big";

    /// <summary>Clock in the overlay: System (follows Windows), 24h or 12h.</summary>
    public string ClockFormat { get; set; } = "System";

    /// <summary>Show a notification in the corner when Console Overlay starts.</summary>
    public bool StartupNotification { get; set; } = true;

    /// <summary>Apps, games and links on the Shortcuts page, in order.</summary>
    public List<LaunchShortcut> Shortcuts { get; set; } = [];

    public ControllerButtons ParseCombo(out List<string> unknown)
    {
        unknown = [];
        var result = ControllerButtons.None;
        foreach (var name in ControllerCombo)
        {
            if (Enum.TryParse(name.Trim(), ignoreCase: true, out ControllerButtons b) && b != ControllerButtons.None)
                result |= b;
            else
                unknown.Add(name);
        }
        return result;
    }
}

/// <summary>A tile on the Shortcuts page. <see cref="Target"/> is a file path (exe, .lnk, .url) or a link.</summary>
public sealed class LaunchShortcut
{
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
}

/// <summary>Loads and saves settings as JSON in %APPDATA%\ConsoleOverlay\settings.json.</summary>
public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConsoleOverlay");

    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read settings, using defaults: {ex.Message}");
            return new AppSettings();
        }

        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }
}
