using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ConsoleOverlay.Steam;

/// <summary>Online, away... as Steam reports it. <see cref="Unknown"/>: no Web API key, so only the name is known.</summary>
internal enum FriendState { Offline, Online, Busy, Away, Snooze, LookingToTrade, LookingToPlay, Unknown }

/// <summary>A Steam friend. <see cref="Game"/> is what they are playing right now, if anything.</summary>
internal sealed record SteamFriend(
    string SteamId, string Name, string? AvatarUrl, FriendState State, string? Game, int GameId,
    string? ServerAddress, string? LobbyId, DateTime? LastOnline)
{
    public bool InGame => Game is not null;
    public bool IsOnline => State is not (FriendState.Offline or FriendState.Unknown);

    /// <summary>
    /// A steam:// link that joins their game, when Steam says it can be joined from outside (a lobby or a server).
    /// The same links the "Join Game" button on a Steam profile uses.
    /// </summary>
    public string? JoinLink =>
        LobbyId is { Length: > 0 } lobby && GameId > 0 ? $"steam://joinlobby/{GameId}/{lobby}/{SteamId}"
        : ServerAddress is { Length: > 0 } server && !server.StartsWith("0.0.0.0", StringComparison.Ordinal) ? $"steam://connect/{server}"
        : null;
}

/// <summary>
/// Your Steam friends, through Valve's official Steam Web API (your own Web API key, from
/// steamcommunity.com/dev/apikey). Without a key, the names Steam keeps on this PC are shown instead, without who
/// is online. Your account is found from Steam's files on this PC, so there is nothing else to sign in to.
///
/// Valve has no public API for sending chat messages or game invites from another app. Chat happens on Valve's own
/// web chat (inside the overlay) or in the Steam app; invites are sent from the Steam chat window, see the links below.
/// </summary>
internal static class SteamFriends
{
    private const long SteamId64Base = 76561197960265728;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public const string ApiKeyPage = "https://steamcommunity.com/dev/apikey";
    public const string WebChat = "https://steamcommunity.com/chat/";

    /// <summary>Opens Steam's chat window with this friend. It has "Invite to Game" while you are playing.</summary>
    public static string ClientChatLink(string steamId) => $"steam://friends/message/{steamId}";
    public static string ProfileLink(string steamId) => $"https://steamcommunity.com/profiles/{steamId}/";

    /// <summary>The 64-bit id of the account signed in to Steam on this PC (or the last one), or null.</summary>
    public static string? CurrentSteamId(string steamFolder)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            if (key?.GetValue("ActiveUser") is int account && account > 0)
                return (SteamId64Base + (uint)account).ToString(CultureInfo.InvariantCulture);
        }
        catch { }

        try
        {
            var file = Path.Combine(steamFolder, "config", "loginusers.vdf");
            if (!File.Exists(file)) return null;
            var users = Vdf.Parse(File.ReadAllText(file)).Block("users");
            if (users is null) return null;
            var best = users.Blocks
                .Where(u => u.Key.All(char.IsAsciiDigit))
                .OrderByDescending(u => u.Value["MostRecent"] == "1" || u.Value["mostrecent"] == "1")
                .ThenByDescending(u => long.TryParse(u.Value["Timestamp"], out long t) ? t : 0)
                .FirstOrDefault();
            return best.Key;
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: could not read loginusers.vdf: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Your friends with who is online and what they play (needs <paramref name="apiKey"/>), or, without a key, the
    /// friends Steam remembers on this PC. Call off the UI thread.
    /// </summary>
    public static async Task<List<SteamFriend>> LoadAsync(string steamFolder, string steamId, string? apiKey, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return LocalFriends(steamFolder, steamId);

        apiKey = apiKey.Trim();
        var list = await GetJsonAsync(
            $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={apiKey}&steamid={steamId}&relationship=friend", cancel);
        var ids = (list?["friendslist"]?["friends"]?.AsArray() ?? [])
            .Select(f => f?["steamid"]?.GetValue<string>())
            .OfType<string>()
            .ToList();

        var friends = new List<SteamFriend>();
        foreach (var batch in ids.Chunk(100))
        {
            var json = await GetJsonAsync(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={string.Join(',', batch)}", cancel);
            foreach (var p in json?["response"]?["players"]?.AsArray() ?? [])
            {
                if (p is null) continue;
                var id = p["steamid"]?.GetValue<string>();
                if (id is null) continue;
                int state = Int(p["personastate"]) ?? 0;
                long lastOff = Int64(p["lastlogoff"]) ?? 0;
                int.TryParse(p["gameid"]?.GetValue<string>(), out int gameId);
                friends.Add(new SteamFriend(
                    id,
                    p["personaname"]?.GetValue<string>() ?? id,
                    p["avatarfull"]?.GetValue<string>() ?? p["avatarmedium"]?.GetValue<string>(),
                    state is >= 0 and <= 6 ? (FriendState)state : FriendState.Online,
                    p["gameextrainfo"]?.GetValue<string>(),
                    gameId,
                    p["gameserverip"]?.GetValue<string>(),
                    p["lobbysteamid"]?.GetValue<string>(),
                    lastOff > 0 ? DateTimeOffset.FromUnixTimeSeconds(lastOff).LocalDateTime : null));
            }
        }
        return Sorted(friends);
    }

    /// <summary>In a game first, then online, then away, then offline; by name inside each.</summary>
    public static List<SteamFriend> Sorted(IEnumerable<SteamFriend> friends) => friends
        .OrderBy(f => f.InGame ? 0 : f.State switch
        {
            FriendState.Online or FriendState.LookingToPlay or FriendState.LookingToTrade => 1,
            FriendState.Busy => 2,
            FriendState.Away or FriendState.Snooze => 3,
            FriendState.Unknown => 4,
            _ => 5,
        })
        .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>
    /// The friends Steam keeps in userdata\{account}\config\localconfig.vdf: names and avatars, no online status.
    /// </summary>
    private static List<SteamFriend> LocalFriends(string steamFolder, string steamId)
    {
        var result = new List<SteamFriend>();
        try
        {
            if (!long.TryParse(steamId, out long id64)) return result;
            long account = id64 - SteamId64Base;
            var file = Path.Combine(steamFolder, "userdata", account.ToString(CultureInfo.InvariantCulture), "config", "localconfig.vdf");
            if (!File.Exists(file)) return result;
            var root = Vdf.Parse(File.ReadAllText(file));
            var friends = root.Block("UserLocalConfigStore")?.Block("friends") ?? root.Block("friends");
            if (friends is null) return result;
            foreach (var (key, block) in friends.Blocks)
            {
                // Friends are listed by account id; other entries are settings and group chats.
                if (!uint.TryParse(key, out uint friendAccount) || friendAccount == account) continue;
                var name = block["name"];
                if (string.IsNullOrWhiteSpace(name)) continue;
                var avatar = block["avatar"];
                result.Add(new SteamFriend(
                    (SteamId64Base + friendAccount).ToString(CultureInfo.InvariantCulture),
                    name,
                    avatar is { Length: 40 } ? $"https://avatars.steamstatic.com/{avatar}_full.jpg" : null,
                    FriendState.Unknown, null, 0, null, null, null));
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Steam: could not read the friends Steam keeps on this PC: {ex.Message}");
        }
        return Sorted(result);
    }

    private static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, cancel);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new UnauthorizedAccessException("Steam didn't accept the Web API key, or your friends list is private to it.");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int i) ? i : null;
    private static long? Int64(JsonNode? node) => node is JsonValue v && v.TryGetValue(out long l) ? l : null;
}
