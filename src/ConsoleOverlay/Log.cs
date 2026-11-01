using System;
using System.IO;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay;

/// <summary>Tiny append-only log in %APPDATA%\ConsoleOverlay\log.txt, reset on each start (the previous run is kept in log.previous.txt).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(SettingsStore.Folder, "log.txt");
    public static string PreviousFilePath => Path.Combine(SettingsStore.Folder, "log.previous.txt");

    public static void Reset()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            // Keep the last run's log so a crash can still be read after restarting.
            if (File.Exists(FilePath))
                File.Copy(FilePath, PreviousFilePath, overwrite: true);
            File.WriteAllText(FilePath, string.Empty);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
