using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using WindowsMediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace ConsoleOverlay.Media;

/// <summary>One app that is playing (or paused): what Windows shows in its own media flyout.</summary>
internal sealed class PlayingApp
{
    public required string Id { get; init; }          // the app's id (AppUserModelId)
    public required string AppName { get; init; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public bool Playing { get; set; }
    public bool CanPrevious { get; set; }
    public bool CanNext { get; set; }
    public bool CanPlayPause { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public DateTimeOffset PositionTakenAt { get; set; }
    public BitmapSource? Cover { get; set; }
    internal string CoverFor { get; set; } = ""; // the track the cover belongs to, so it is only read once per track
    internal WindowsMediaSession? Session { get; set; }

    /// <summary>Where the track is now: the last position Windows reported, moved on by the time since, while playing.</summary>
    public TimeSpan PositionNow
    {
        get
        {
            if (!Playing || PositionTakenAt == default) return Position;
            var now = Position + (DateTimeOffset.Now - PositionTakenAt);
            return Duration > TimeSpan.Zero && now > Duration ? Duration : now;
        }
    }
}

/// <summary>
/// Reads and controls what is playing through Windows' media controls (the same ones the volume flyout and the
/// media keys use), so it works with Spotify, browsers, the Media Player app and anything else that supports them.
/// </summary>
internal sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly Dictionary<string, PlayingApp> _players = [];
    private bool _failedLogged;

    /// <summary>The player Windows considers current first, then the others (playing before paused).</summary>
    public IReadOnlyList<PlayingApp> Players { get; private set; } = [];

    public async Task RefreshAsync()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = _manager.GetSessions();
            var current = _manager.GetCurrentSession()?.SourceAppUserModelId;
            var seen = new HashSet<string>();
            var list = new List<PlayingApp>();

            foreach (var session in sessions)
            {
                string id = session.SourceAppUserModelId ?? "";
                if (!seen.Add(id)) continue;
                if (!_players.TryGetValue(id, out var player))
                {
                    player = new PlayingApp { Id = id, AppName = AppName(id) };
                    _players[id] = player;
                }
                player.Session = session;
                await ReadAsync(session, player);
                list.Add(player);
            }

            foreach (var gone in _players.Keys.Where(k => !seen.Contains(k)).ToList())
                _players.Remove(gone);

            Players = list
                .OrderByDescending(p => p.Id == current)
                .ThenByDescending(p => p.Playing)
                .ToList();
        }
        catch (Exception ex)
        {
            if (!_failedLogged)
            {
                _failedLogged = true;
                Log.Write($"Media: could not read what is playing: {ex.Message}");
            }
            Players = [];
        }
    }

    private static async Task ReadAsync(WindowsMediaSession session, PlayingApp player)
    {
        try
        {
            var playback = session.GetPlaybackInfo();
            player.Playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            player.CanNext = playback.Controls.IsNextEnabled;
            player.CanPrevious = playback.Controls.IsPreviousEnabled;
            player.CanPlayPause = playback.Controls.IsPlayPauseToggleEnabled || playback.Controls.IsPlayEnabled || playback.Controls.IsPauseEnabled;

            var timeline = session.GetTimelineProperties();
            player.Duration = timeline.EndTime - timeline.StartTime;
            player.Position = timeline.Position - timeline.StartTime;
            player.PositionTakenAt = timeline.LastUpdatedTime;

            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null) return;
            player.Title = properties.Title ?? "";
            player.Artist = properties.Artist ?? "";
            player.Album = properties.AlbumTitle ?? "";

            string track = player.Title + "|" + player.Artist;
            if (track != player.CoverFor)
            {
                player.CoverFor = track;
                player.Cover = await ReadCoverAsync(properties.Thumbnail);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Media: could not read {player.AppName}: {ex.Message}");
        }
    }

    private static async Task<BitmapSource?> ReadCoverAsync(Windows.Storage.Streams.IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var source = stream.AsStreamForRead();
            var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = memory;
            image.DecodePixelWidth = 600;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public Task PlayPauseAsync(PlayingApp player) => Run(player, s => s.TryTogglePlayPauseAsync().AsTask());
    public Task NextAsync(PlayingApp player) => Run(player, s => s.TrySkipNextAsync().AsTask());
    public Task PreviousAsync(PlayingApp player) => Run(player, s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>Jumps forward or back in the track.</summary>
    public Task SeekAsync(PlayingApp player, TimeSpan by)
    {
        var target = player.PositionNow + by;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (player.Duration > TimeSpan.Zero && target > player.Duration) target = player.Duration;
        player.Position = target;
        player.PositionTakenAt = DateTimeOffset.Now;
        return Run(player, s => s.TryChangePlaybackPositionAsync(target.Ticks).AsTask());
    }

    private static async Task Run(PlayingApp player, Func<WindowsMediaSession, Task<bool>> action)
    {
        if (player.Session is not { } session) return;
        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            Log.Write($"Media: {player.AppName} didn't take the command: {ex.Message}");
        }
    }

    /// <summary>A readable name from the app's id: "Spotify.exe" → Spotify, "msedge" → Microsoft Edge.</summary>
    private static string AppName(string id)
    {
        string name = id;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];            // packaged apps: Family.Name_hash!App
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        name = Path.GetFileNameWithoutExtension(name);
        if (name.Contains('.')) name = name[(name.LastIndexOf('.') + 1)..]; // Microsoft.ZuneMusic → ZuneMusic

        return name.ToLowerInvariant() switch
        {
            "spotify" => "Spotify",
            "chrome" => "Google Chrome",
            "msedge" or "microsoftedge" => "Microsoft Edge",
            "firefox" => "Firefox",
            "opera" => "Opera",
            "brave" => "Brave",
            "zunemusic" => "Media Player",
            "zunevideo" => "Movies & TV",
            "vlc" => "VLC",
            "applemusic" or "itunes" => "Apple Music",
            "discord" => "Discord",
            "" => "Media",
            _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name),
        };
    }
}
