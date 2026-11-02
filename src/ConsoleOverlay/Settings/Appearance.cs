using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ConsoleOverlay.Input;

namespace ConsoleOverlay.Settings;

/// <summary>The choices offered on the Settings page, with the names people see.</summary>
public static class Appearance
{
    /// <summary>Accent colors. All light enough that dark text on the pill stays readable.</summary>
    public static readonly (string Value, string Label, Color Color)[] Accents =
    [
        ("White", "White", Color.FromRgb(0xF2, 0xF4, 0xF8)),
        ("Blue", "Blue", Color.FromRgb(0x8F, 0xB8, 0xFF)),
        ("Green", "Green", Color.FromRgb(0x86, 0xE0, 0xA8)),
        ("Purple", "Purple", Color.FromRgb(0xC4, 0xA8, 0xFF)),
        ("Orange", "Orange", Color.FromRgb(0xFF, 0xB8, 0x80)),
        ("Pink", "Pink", Color.FromRgb(0xFF, 0xA3, 0xCB)),
    ];

    public static readonly (string Value, string Label)[] Backgrounds =
    [
        ("Blur", "Blurred game"),
        ("Clear", "Game, not blurred"),
        ("Dark", "Dark"),
    ];

    public static readonly (string Value, string Label)[] ClockFormats =
    [
        ("System", "Like Windows"),
        ("24h", "24-hour"),
        ("12h", "12-hour"),
    ];

    public static Color AccentColor(string name)
    {
        foreach (var accent in Accents)
            if (Is(name, accent.Value))
                return accent.Color;
        return Accents[0].Color;
    }

    public static bool Is(string setting, string value) => string.Equals(setting, value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Controller button names as printed on an Xbox controller.</summary>
public static class ButtonNames
{
    private static readonly (ControllerButtons Button, string Label)[] Labels =
    [
        (ControllerButtons.Guide, "Xbox"),
        (ControllerButtons.Back, "View"),
        (ControllerButtons.Start, "Menu"),
        (ControllerButtons.LeftShoulder, "LB"),
        (ControllerButtons.RightShoulder, "RB"),
        (ControllerButtons.LeftTrigger, "LT"),
        (ControllerButtons.RightTrigger, "RT"),
        (ControllerButtons.LeftStick, "Left stick"),
        (ControllerButtons.RightStick, "Right stick"),
        (ControllerButtons.A, "A"),
        (ControllerButtons.B, "B"),
        (ControllerButtons.X, "X"),
        (ControllerButtons.Y, "Y"),
        (ControllerButtons.DPadUp, "Up"),
        (ControllerButtons.DPadDown, "Down"),
        (ControllerButtons.DPadLeft, "Left"),
        (ControllerButtons.DPadRight, "Right"),
    ];

    public static IEnumerable<ControllerButtons> Split(ControllerButtons buttons) =>
        Labels.Select(l => l.Button).Where(b => buttons.HasFlag(b));

    public static string Friendly(ControllerButtons buttons) =>
        buttons == ControllerButtons.None ? "None" : string.Join(" + ", Split(buttons).Select(b => Labels.First(l => l.Button == b).Label));

    /// <summary>The names used in settings.json.</summary>
    public static List<string> ToSettingNames(ControllerButtons buttons) => Split(buttons).Select(b => b.ToString()).ToList();
}

/// <summary>What the Settings page needs from the app: saving, the shortcut, controller capture, leaving.</summary>
public interface ISettingsHost
{
    AppSettings Settings { get; }

    /// <summary>Saves <see cref="Settings"/> to disk and applies it.</summary>
    void Save();

    /// <summary>Registers a new keyboard shortcut and saves it. Keeps the old one and returns false if Windows won't take it.</summary>
    bool TrySetHotkey(string hotkey, out string? error);

    /// <summary>Lets go of the shortcut while a new one is being typed, so pressing the old one doesn't close the overlay.</summary>
    void SuspendHotkey();

    void ResumeHotkey();

    /// <summary>Waits for a held button combo. <paramref name="done"/> gets null when cancelled or timed out.</summary>
    void CaptureCombo(Action<ControllerButtons> progress, Action<ControllerButtons?> done);

    void CancelComboCapture();

    void OpenSettingsFile();

    void OpenLog();

    void SignOutOfDiscord();

    void Quit();
}
