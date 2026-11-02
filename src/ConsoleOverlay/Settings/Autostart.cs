using System;
using Microsoft.Win32;

namespace ConsoleOverlay.Settings;

/// <summary>
/// Starts Console Overlay when you sign in to Windows, through the usual per-user Run key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run). No admin rights needed, and it shows up
/// in Task Manager's Startup apps where it can be turned off too.
/// </summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ConsoleOverlay";
    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch (Exception ex)
            {
                Log.Write($"Could not read autostart: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Turns autostart on or off. Returns false (and logs why) if Windows refused.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
                key.SetValue(ValueName, Command);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Write($"Autostart {(enabled ? "on" : "off")}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not change autostart: {ex.Message}");
            return false;
        }
    }

    /// <summary>If autostart is on but points at an old copy (the app was moved or updated elsewhere), point it here.</summary>
    public static void RepairPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && current.Length > 0
                && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, Command);
                Log.Write("Autostart now points at this copy of the app.");
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not check autostart: {ex.Message}");
        }
    }
}
