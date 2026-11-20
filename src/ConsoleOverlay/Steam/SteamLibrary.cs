using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace ConsoleOverlay.Steam;

/// <summary>An installed Steam game, read from its app manifest.</summary>
internal sealed record SteamGame(int AppId, string Name, DateTime? LastPlayed, long SizeOnDisk, string? CoverPath, string? HeaderPath);

/// <summary>
/// Reads the Steam library straight from the files Steam keeps on disk, so it needs no account, no API key and no
/// network: libraryfolders.vdf lists the library folders, each game has an appmanifest_*.acf, and Steam's own
/// cache holds the cover art. Games are launched through steam:// links, the way the Steam client does it.
/// </summary>
internal static class SteamLibrary
{
    private const string SteamKey = @"Software\Valve\Steam";

    /// <summary>Steam's install folder, or null when Steam isn't installed.</summary>
    public static string? SteamFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SteamKey);
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: could not read the install folder: {ex.Message}");
        }
        return null;
    }

    /// <summary>The Steam game running right now (Steam keeps this in the registry), or 0.</summary>
    public static int RunningAppId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SteamKey);
            return key?.GetValue("RunningAppID") is int id ? id : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Every installed game in every library folder. Slow-ish (reads files): call it off the UI thread.</summary>
    public static List<SteamGame> InstalledGames(string steamFolder)
    {
        var games = new Dictionary<int, SteamGame>();
        var artIndex = ArtIndex(steamFolder);
        foreach (var library in LibraryFolders(steamFolder))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in SafeFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var state = Vdf.Parse(File.ReadAllText(manifest)).Block("AppState");
                    if (state is null || !int.TryParse(state["appid"], out int appId)) continue;
                    var name = state["name"];
                    if (string.IsNullOrWhiteSpace(name) || IsTool(appId, name)) continue;

                    DateTime? lastPlayed = long.TryParse(state["LastPlayed"], out long seconds) && seconds > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(seconds).LocalDateTime
                        : null;
                    long.TryParse(state["SizeOnDisk"], out long size);
                    artIndex.TryGetValue(appId, out var art);
                    games[appId] = new SteamGame(appId, name, lastPlayed, size, art.Cover, art.Header);
                }
                catch (Exception ex)
                {
                    Log.Write($"Steam: skipped {Path.GetFileName(manifest)}: {ex.Message}");
                }
            }
        }
        return [.. games.Values];
    }

    public static string RunLink(int appId) => $"steam://rungameid/{appId}";
    public static string GamePageLink(int appId) => $"steam://nav/games/details/{appId}";
    public const string FriendsLink = "steam://open/friends";

    private static IEnumerable<string> LibraryFolders(string steamFolder)
    {
        var folders = new List<string> { steamFolder };
        var file = Path.Combine(steamFolder, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(file))
            {
                var root = Vdf.Parse(File.ReadAllText(file));
                var list = root.Block("libraryfolders") ?? root.Block("LibraryFolders");
                if (list is not null)
                {
                    // New format: "0" { "path" "D:\\SteamLibrary" ... }. Old format: "1" "D:\\SteamLibrary".
                    foreach (var block in list.Blocks.Values)
                        if (block["path"] is { } path) folders.Add(path);
                    foreach (var (key, value) in list.Values)
                        if (int.TryParse(key, out _)) folders.Add(value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: could not read libraryfolders.vdf: {ex.Message}");
        }
        return folders.Select(f => f.Replace(@"\\", @"\")).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds the cover (portrait, library_600x900) and header art Steam has cached. Steam has stored these as
    /// librarycache\{appid}_library_600x900.jpg and, since 2024, under librarycache\{appid}\ (sometimes one folder
    /// deeper), so all layouts are looked at.
    /// </summary>
    private static Dictionary<int, (string? Cover, string? Header)> ArtIndex(string steamFolder)
    {
        var index = new Dictionary<int, (string? Cover, string? Header)>();
        var cache = Path.Combine(steamFolder, "appcache", "librarycache");
        if (!Directory.Exists(cache)) return index;

        void Note(int appId, string file)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            index.TryGetValue(appId, out var art);
            if (name.EndsWith("library_600x900") || name.EndsWith("library_600x900_2x") || name == "library_capsule")
                art.Cover ??= file;
            else if (name.EndsWith("header") || name == "library_header")
                art.Header ??= file;
            index[appId] = art;
        }

        foreach (var file in SafeFiles(cache, "*.jpg").Concat(SafeFiles(cache, "*.png")))
        {
            var name = Path.GetFileName(file);
            int underscore = name.IndexOf('_');
            if (underscore > 0 && int.TryParse(name[..underscore], out int appId))
                Note(appId, file);
        }
        foreach (var folder in SafeDirectories(cache))
        {
            if (!int.TryParse(Path.GetFileName(folder), out int appId)) continue;
            foreach (var file in SafeFiles(folder, "*.jpg", SearchOption.AllDirectories).Concat(SafeFiles(folder, "*.png", SearchOption.AllDirectories)))
                Note(appId, file);
        }
        return index;
    }

    /// <summary>Steamworks redistributables, Proton and other tools that show up as apps but aren't games.</summary>
    private static bool IsTool(int appId, string name) =>
        appId == 228980 // Steamworks Common Redistributables
        || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> SafeFiles(string folder, string pattern, SearchOption option = SearchOption.TopDirectoryOnly)
    {
        try { return Directory.EnumerateFiles(folder, pattern, new EnumerationOptions { RecurseSubdirectories = option == SearchOption.AllDirectories, IgnoreInaccessible = true, MaxRecursionDepth = 2 }).ToList(); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder).ToList(); }
        catch { return []; }
    }
}
