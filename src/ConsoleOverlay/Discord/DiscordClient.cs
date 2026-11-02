using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace ConsoleOverlay.Discord;

public enum DiscordState
{
    NotSetUp,            // no Client ID / Secret in the settings
    NotRunning,          // the Discord desktop app isn't running
    Connecting,
    NeedsAuthorization,  // connected, but you haven't allowed Console Overlay yet
    WaitingForApproval,  // Discord is showing the "Authorize" prompt
    Ready,
    Failed,
}

public sealed class VoiceMember
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? AvatarUrl { get; init; }
    public bool IsSelf { get; init; }
    /// <summary>How loud you hear them, 0 to 200 (100 is normal).</summary>
    public int Volume { get; set; } = 100;
    /// <summary>You muted them for yourself.</summary>
    public bool MutedForMe { get; set; }
    public bool SelfMuted { get; init; }
    public bool SelfDeafened { get; init; }
    public bool ServerMuted { get; init; }
    public bool ServerDeafened { get; init; }
}

public sealed record AudioDevice(string Id, string Name);

/// <summary>Your Discord voice and audio settings.</summary>
public sealed class VoiceSettings
{
    /// <summary>Microphone volume, 0 to 100.</summary>
    public int InputVolume { get; set; } = 100;
    /// <summary>Speaker / headphone volume, 0 to 200 (100 is normal).</summary>
    public int OutputVolume { get; set; } = 100;
    public bool PushToTalk { get; set; }
    public bool NoiseSuppression { get; set; }
    public bool EchoCancellation { get; set; }
    public bool AutomaticGain { get; set; }
    public string? InputDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public List<AudioDevice> InputDevices { get; set; } = [];
    public List<AudioDevice> OutputDevices { get; set; } = [];
}

public sealed record DiscordServer(string Id, string Name);

/// <summary>A voice channel you could join, and the names of who is in it right now.</summary>
public sealed record JoinableChannel(string Id, string Name, List<string> People);

public sealed class VoiceChannel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? GuildId { get; init; }
    public string? GuildName { get; init; }
    public required List<VoiceMember> Members { get; init; }
}

/// <summary>
/// Talks to the Discord desktop app over its local RPC pipe (\\.\pipe\discord-ipc-N): your mute and deafen,
/// the voice channel you're in, who is talking, and per-person volume.
///
/// Discord only allows this for an app you registered yourself, so the user creates a free app in the Discord
/// Developer Portal and puts its Client ID and Secret in the settings. The first time, Discord asks the user to
/// click Authorize; the resulting sign-in is saved (encrypted) and refreshed, so that is a one-time step.
///
/// The connection is opened the first time the Discord page is shown and then kept: an idle pipe costs nothing.
/// Speaking events, the only frequent ones, are subscribed only while the page is on screen.
/// All events are raised on the UI thread.
/// </summary>
internal sealed class DiscordClient
{
    private const string RedirectUri = "http://localhost";
    private static readonly string[] Scopes = ["rpc", "rpc.voice.read", "rpc.voice.write"];
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Dispatcher _dispatcher;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly HashSet<string> _speaking = [];
    private readonly Dictionary<string, string> _guildNames = [];

    private string _clientId = "";
    private string _clientSecret = "";
    private NamedPipeClientStream? _pipe;
    private TaskCompletionSource<JsonNode?>? _ready;
    private Task? _connecting;
    private int _generation; // bumps on each disconnect so stale work can tell it is stale
    private string? _subscribedChannel;
    private bool _speakingWanted;
    private bool _speakingSubscribed;
    private bool _refreshQueued;
    // Why the last sign-in attempt failed. Kept across reconnects so reopening the overlay still says what went wrong.
    private string? _signInProblem;

    public DiscordClient(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>The connection state, or what to show instead of the controls.</summary>
    public event Action? StateChanged;

    /// <summary>Your mute or deafen, the channel, or the people in it changed.</summary>
    public event Action? VoiceChanged;

    /// <summary>Someone started (true) or stopped (false) talking.</summary>
    public event Action<string, bool>? SpeakingChanged;

    public DiscordState State { get; private set; } = DiscordState.NotSetUp;

    /// <summary>Extra detail for <see cref="DiscordState.Failed"/> and <see cref="DiscordState.NeedsAuthorization"/>.</summary>
    public string? Problem { get; private set; }

    public bool Muted { get; private set; }
    public VoiceSettings Settings { get; } = new();
    public bool Deafened { get; private set; }
    public VoiceChannel? Channel { get; private set; }
    public string? SelfId { get; private set; }

    public bool IsSpeaking(string userId) => _speaking.Contains(userId);

    public void Configure(string clientId, string clientSecret)
    {
        clientId = clientId.Trim();
        clientSecret = clientSecret.Trim();
        if (clientId == _clientId && clientSecret == _clientSecret) return;

        _clientId = clientId;
        _clientSecret = clientSecret;
        _signInProblem = null;
        Disconnect(); // a different app means a different sign-in
        SetState(IsConfigured ? DiscordState.NotRunning : DiscordState.NotSetUp, null);
    }

    /// <summary>Forgets the saved sign-in and drops the connection. Discord asks to authorize again next time.</summary>
    public void SignOut()
    {
        DiscordTokenStore.Delete();
        Disconnect();
        SetState(IsConfigured ? DiscordState.NotRunning : DiscordState.NotSetUp, null);
    }

    private bool IsConfigured => _clientId.Length > 0 && _clientSecret.Length > 0;

    // ---- Connecting -------------------------------------------------------------------------

    /// <summary>
    /// Connects and signs in if needed. Without <paramref name="allowPrompt"/> it stops at
    /// <see cref="DiscordState.NeedsAuthorization"/> instead of popping up Discord's Authorize prompt.
    /// </summary>
    public Task ConnectAsync(bool allowPrompt)
    {
        if (!IsConfigured)
        {
            SetState(DiscordState.NotSetUp, null);
            return Task.CompletedTask;
        }
        if (State == DiscordState.Ready && _pipe is { IsConnected: true })
            return Task.CompletedTask;
        if (_connecting is { IsCompleted: false })
            return _connecting;
        _connecting = ConnectCoreAsync(allowPrompt);
        return _connecting;
    }

    private async Task ConnectCoreAsync(bool allowPrompt)
    {
        try
        {
            if (_pipe is not { IsConnected: true })
            {
                SetState(DiscordState.Connecting, null);
                if (!await OpenPipeAsync())
                {
                    SetState(DiscordState.NotRunning, null);
                    return;
                }
            }

            if (!await SignInAsync(allowPrompt))
                return; // state already says why

            await LoadVoiceAsync();
            SetState(DiscordState.Ready, null);
        }
        catch (DiscordException ex)
        {
            Log.Write($"Discord: {ex.Message} (code {ex.Code})");
            _signInProblem = ex.Message;
            Disconnect();
            SetState(DiscordState.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
        {
            Log.Write($"Discord connection lost: {ex.Message}");
            Disconnect();
            SetState(DiscordState.NotRunning, null);
        }
        catch (Exception ex)
        {
            Log.Write($"Discord error: {ex}");
            Disconnect();
            SetState(DiscordState.Failed, ex.Message);
        }
    }

    /// <summary>Finds Discord's pipe (stable, PTB and Canary each take the next free number) and says hello.</summary>
    private async Task<bool> OpenPipeAsync()
    {
        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(150);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                pipe.Dispose();
                continue;
            }

            _pipe = pipe;
            _ready = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            int generation = ++_generation;
            _ = Task.Run(() => ReadLoopAsync(pipe, generation));

            await WriteAsync(Opcode.Handshake, new JsonObject { ["v"] = 1, ["client_id"] = _clientId });
            var ready = await _ready.Task.WaitAsync(RequestTimeout);
            SelfId = ready?["user"]?["id"]?.GetValue<string>();
            Log.Write($"Discord: connected on pipe {i}.");
            return true;
        }
        return false;
    }

    private async Task<bool> SignInAsync(bool allowPrompt)
    {
        var token = DiscordTokenStore.Load(_clientId);

        if (token is not null && token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1) && await TryAuthenticateAsync(token.AccessToken))
            return true;

        if (token?.RefreshToken is { Length: > 0 } refresh)
        {
            var refreshed = await ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
            });
            if (refreshed.Token is not null && await TryAuthenticateAsync(refreshed.Token.AccessToken))
                return true;
        }

        if (!allowPrompt)
        {
            SetState(DiscordState.NeedsAuthorization, _signInProblem);
            return false;
        }

        // Discord shows its own "Authorize" prompt in the Discord window and answers once the user clicks.
        SetState(DiscordState.WaitingForApproval, null);
        JsonNode? answer;
        try
        {
            answer = await SendAsync("AUTHORIZE", new JsonObject
            {
                ["client_id"] = _clientId,
                // No redirect_uri here: Discord refuses one in the RPC flow and instead uses the app's only Redirect.
                // With none, or more than one, on the app's OAuth2 page it answers "Missing redirect_uri".
                ["scopes"] = new JsonArray(Scopes.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            }, timeout: TimeSpan.FromMinutes(5));
        }
        catch (DiscordException ex)
        {
            Log.Write($"Discord: authorization not given (code {ex.Code}): {ex.Message}");
            _signInProblem = ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase)
                ? $"Discord said: \"{ex.Message}\". On your app's OAuth2 page in the Discord Developer Portal, keep exactly one Redirect: http://localhost. Save, then try again."
                : $"Discord said no: \"{ex.Message}\". Try again and click Authorize in Discord.";
            SetState(DiscordState.NeedsAuthorization, _signInProblem);
            return false;
        }
        catch (TimeoutException)
        {
            _signInProblem = "Nobody clicked Authorize in Discord in time. Try again.";
            SetState(DiscordState.NeedsAuthorization, _signInProblem);
            return false;
        }
        Log.Write("Discord: Authorize was clicked; exchanging the code for a sign-in.");

        var code = answer?["code"]?.GetValue<string>();
        if (code is null)
            throw new DiscordException(0, "Discord didn't send an authorization code.");

        var fresh = await ExchangeAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
        });
        // A redirect that doesn't match the app's list is rejected before the code is used, so try once without it.
        if (fresh.Token is null && fresh.Error?.Contains("redirect", StringComparison.OrdinalIgnoreCase) == true)
            fresh = await ExchangeAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
            });

        if (fresh.Token is null)
        {
            _signInProblem = $"You clicked Authorize, but Discord turned down the sign-in: {fresh.Error ?? "no answer"}. " +
                "Check the Client Secret in the settings file and that http://localhost is a Redirect on your app's OAuth2 page, then reload settings and try again.";
            SetState(DiscordState.NeedsAuthorization, _signInProblem);
            return false;
        }

        if (!await TryAuthenticateAsync(fresh.Token.AccessToken))
        {
            _signInProblem = "Discord gave a sign-in but then didn't accept it. Try again; the log has details.";
            SetState(DiscordState.NeedsAuthorization, _signInProblem);
            return false;
        }
        _signInProblem = null;
        Log.Write("Discord: signed in.");
        return true;
    }

    private async Task<bool> TryAuthenticateAsync(string accessToken)
    {
        try
        {
            var result = await SendAsync("AUTHENTICATE", new JsonObject { ["access_token"] = accessToken });
            SelfId = result?["user"]?["id"]?.GetValue<string>() ?? SelfId;
            return true;
        }
        catch (DiscordException ex)
        {
            Log.Write($"Discord: saved sign-in not accepted ({ex.Message}).");
            return false;
        }
    }

    /// <summary>Trades a code or refresh token for an access token at Discord's OAuth2 endpoint, and saves it.</summary>
    private async Task<(DiscordToken? Token, string? Error)> ExchangeAsync(Dictionary<string, string> fields)
    {
        fields["client_id"] = _clientId;
        fields["client_secret"] = _clientSecret;
        try
        {
            using var response = await Http.PostAsync("https://discord.com/api/oauth2/token", new FormUrlEncodedContent(fields));
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"Discord: token request failed ({(int)response.StatusCode}): {body}");
                string? reason = null;
                try
                {
                    var error = JsonNode.Parse(body);
                    reason = error?["error_description"]?.ToString() ?? error?["error"]?.ToString();
                }
                catch (System.Text.Json.JsonException)
                {
                }
                return (null, reason ?? $"error {(int)response.StatusCode}");
            }

            var json = JsonNode.Parse(body);
            var access = json?["access_token"]?.GetValue<string>();
            if (access is null) return (null, "no access token in Discord's answer");
            var token = new DiscordToken
            {
                ClientId = _clientId,
                AccessToken = access,
                RefreshToken = json?["refresh_token"]?.GetValue<string>(),
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(json?["expires_in"]?.GetValue<int>() ?? 3600),
            };
            DiscordTokenStore.Save(token);
            return (token, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write($"Discord: could not reach discord.com: {ex.Message}");
            return (null, "couldn't reach discord.com");
        }
    }

    // ---- Voice ------------------------------------------------------------------------------

    private async Task LoadVoiceAsync()
    {
        var settings = await SendAsync("GET_VOICE_SETTINGS");
        ApplyVoiceSettings(settings);
        await SendAsync("SUBSCRIBE", null, "VOICE_SETTINGS_UPDATE");
        await SendAsync("SUBSCRIBE", null, "VOICE_CHANNEL_SELECT");
        await RefreshChannelAsync();
    }

    /// <summary>Re-reads the channel you're in and who is in it, and keeps event subscriptions on that channel.</summary>
    private async Task RefreshChannelAsync()
    {
        var data = await SendAsync("GET_SELECTED_VOICE_CHANNEL");
        var id = data?["id"]?.GetValue<string>();

        if (id != _subscribedChannel)
        {
            await UnsubscribeChannelAsync();
            _speaking.Clear();
            if (id is not null)
            {
                foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE" })
                    await SendAsync("SUBSCRIBE", new JsonObject { ["channel_id"] = id }, evt);
                _subscribedChannel = id;
            }
        }
        await SyncSpeakingSubscriptionAsync();

        if (data is null || id is null)
        {
            Channel = null;
            VoiceChanged?.Invoke();
            return;
        }

        string? guildName = null;
        var guildId = data["guild_id"]?.GetValue<string>();
        if (guildId is not null && !_guildNames.TryGetValue(guildId, out guildName))
        {
            try
            {
                guildName = (await SendAsync("GET_GUILD", new JsonObject { ["guild_id"] = guildId }))?["name"]?.GetValue<string>();
                if (guildName is not null) _guildNames[guildId] = guildName;
            }
            catch (DiscordException)
            {
                // The server name is a nice-to-have.
            }
        }

        var members = new List<VoiceMember>();
        foreach (var state in data["voice_states"]?.AsArray() ?? [])
        {
            if (state is null) continue;
            var user = state["user"];
            var userId = user?["id"]?.GetValue<string>();
            if (userId is null) continue;
            var voice = state["voice_state"];
            var avatar = user?["avatar"]?.GetValue<string>();
            members.Add(new VoiceMember
            {
                Id = userId,
                Name = FirstNonEmpty(state["nick"], user?["global_name"], user?["username"]) ?? "Someone",
                AvatarUrl = avatar is null ? null : $"https://cdn.discordapp.com/avatars/{userId}/{avatar}.png?size=64",
                IsSelf = userId == SelfId,
                Volume = (int)Math.Round(state["volume"]?.GetValue<double>() ?? 100),
                MutedForMe = state["mute"]?.GetValue<bool>() ?? false,
                SelfMuted = voice?["self_mute"]?.GetValue<bool>() ?? false,
                SelfDeafened = voice?["self_deaf"]?.GetValue<bool>() ?? false,
                ServerMuted = (voice?["mute"]?.GetValue<bool>() ?? false) || (voice?["suppress"]?.GetValue<bool>() ?? false),
                ServerDeafened = voice?["deaf"]?.GetValue<bool>() ?? false,
            });
        }
        // You first, then everyone else in the order Discord lists them.
        members = members.OrderByDescending(m => m.IsSelf).ToList();

        Channel = new VoiceChannel
        {
            Id = id,
            Name = data["name"]?.GetValue<string>() is { Length: > 0 } name ? name : "Call",
            GuildId = guildId,
            GuildName = guildId is null ? "Private call" : guildName,
            Members = members,
        };
        VoiceChanged?.Invoke();
    }

    private static string? FirstNonEmpty(params JsonNode?[] values) =>
        values.Select(v => v?.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null)
              .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    private async Task UnsubscribeChannelAsync()
    {
        if (_subscribedChannel is null) return;
        var args = new JsonObject { ["channel_id"] = _subscribedChannel };
        var channel = _subscribedChannel;
        _subscribedChannel = null;
        try
        {
            foreach (var evt in new[] { "VOICE_STATE_CREATE", "VOICE_STATE_UPDATE", "VOICE_STATE_DELETE" })
                await SendAsync("UNSUBSCRIBE", new JsonObject { ["channel_id"] = channel }, evt);
            if (_speakingSubscribed)
            {
                await SendAsync("UNSUBSCRIBE", args.DeepClone().AsObject(), "SPEAKING_START");
                await SendAsync("UNSUBSCRIBE", args.DeepClone().AsObject(), "SPEAKING_STOP");
            }
        }
        catch (DiscordException)
        {
            // Discord drops subscriptions for a channel you left anyway.
        }
        _speakingSubscribed = false;
    }

    /// <summary>Speaking events only flow while the Discord page is on screen.</summary>
    public async void SetSpeakingWanted(bool wanted)
    {
        _speakingWanted = wanted;
        if (State != DiscordState.Ready) return;
        try
        {
            await SyncSpeakingSubscriptionAsync();
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not change speaking updates: {ex.Message}");
        }
    }

    private async Task SyncSpeakingSubscriptionAsync()
    {
        bool want = _speakingWanted && _subscribedChannel is not null;
        if (want == _speakingSubscribed) return;
        _speakingSubscribed = want;
        if (_subscribedChannel is null) return;

        string cmd = want ? "SUBSCRIBE" : "UNSUBSCRIBE";
        await SendAsync(cmd, new JsonObject { ["channel_id"] = _subscribedChannel }, "SPEAKING_START");
        await SendAsync(cmd, new JsonObject { ["channel_id"] = _subscribedChannel }, "SPEAKING_STOP");
        if (!want)
        {
            _speaking.Clear();
            VoiceChanged?.Invoke();
        }
    }

    private DateTime _volumeChangedAt;

    private void ApplyVoiceSettings(JsonNode? settings)
    {
        if (settings is null) return;
        if (settings["mute"] is { } mute) Muted = mute.GetValue<bool>();
        if (settings["deaf"] is { } deaf) Deafened = deaf.GetValue<bool>();

        var s = Settings;
        // While a volume is being held left/right, Discord's answers lag behind; don't let them pull it back.
        bool recentVolumeChange = DateTime.UtcNow - _volumeChangedAt < TimeSpan.FromSeconds(1);
        if (settings["input"] is JsonObject input)
        {
            if (!recentVolumeChange && input["volume"] is { } v) s.InputVolume = (int)Math.Round(v.GetValue<double>());
            if (input["device_id"] is { } d) s.InputDeviceId = d.ToString();
            if (input["available_devices"] is JsonArray list) s.InputDevices = ReadDevices(list);
        }
        if (settings["output"] is JsonObject output)
        {
            if (!recentVolumeChange && output["volume"] is { } v) s.OutputVolume = (int)Math.Round(v.GetValue<double>());
            if (output["device_id"] is { } d) s.OutputDeviceId = d.ToString();
            if (output["available_devices"] is JsonArray list) s.OutputDevices = ReadDevices(list);
        }
        if (settings["mode"]?["type"] is { } mode) s.PushToTalk = mode.ToString() == "PUSH_TO_TALK";
        if (settings["noise_suppression"] is { } ns) s.NoiseSuppression = ns.GetValue<bool>();
        if (settings["echo_cancellation"] is { } ec) s.EchoCancellation = ec.GetValue<bool>();
        if (settings["automatic_gain_control"] is { } agc) s.AutomaticGain = agc.GetValue<bool>();
    }

    private static List<AudioDevice> ReadDevices(JsonArray list) =>
        list.Select(d => (Id: d?["id"]?.ToString(), Name: d?["name"]?.ToString()))
            .Where(d => d.Id is not null)
            .Select(d => new AudioDevice(d.Id!, string.IsNullOrWhiteSpace(d.Name) ? "Device" : d.Name!))
            .ToList();

    // ---- Actions (fire and forget from the UI; failures are logged) -------------------------

    public void SetMuted(bool muted)
    {
        Muted = muted;
        if (!muted) Deafened = false; // Discord undeafens when you unmute
        VoiceChanged?.Invoke();
        Run("SET_VOICE_SETTINGS", new JsonObject { ["mute"] = muted });
    }

    public void SetDeafened(bool deafened)
    {
        Deafened = deafened;
        VoiceChanged?.Invoke();
        Run("SET_VOICE_SETTINGS", new JsonObject { ["deaf"] = deafened });
    }

    public void LeaveChannel() => Run("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = null });

    /// <summary>Joins a voice channel, moving you out of the one you're in.</summary>
    public void JoinChannel(string channelId) =>
        Run("SELECT_VOICE_CHANNEL", new JsonObject { ["channel_id"] = channelId, ["force"] = true });

    public void SetInputVolume(int volume)
    {
        Settings.InputVolume = Math.Clamp(volume, 0, 100);
        _volumeChangedAt = DateTime.UtcNow;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["input"] = new JsonObject { ["volume"] = Settings.InputVolume } });
    }

    public void SetOutputVolume(int volume)
    {
        Settings.OutputVolume = Math.Clamp(volume, 0, 200);
        _volumeChangedAt = DateTime.UtcNow;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["output"] = new JsonObject { ["volume"] = Settings.OutputVolume } });
    }

    public void SetPushToTalk(bool on)
    {
        Settings.PushToTalk = on;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["mode"] = new JsonObject { ["type"] = on ? "PUSH_TO_TALK" : "VOICE_ACTIVITY" } });
    }

    public void SetNoiseSuppression(bool on)
    {
        Settings.NoiseSuppression = on;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["noise_suppression"] = on });
    }

    public void SetEchoCancellation(bool on)
    {
        Settings.EchoCancellation = on;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["echo_cancellation"] = on });
    }

    public void SetAutomaticGain(bool on)
    {
        Settings.AutomaticGain = on;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["automatic_gain_control"] = on });
    }

    public void SetInputDevice(string id)
    {
        Settings.InputDeviceId = id;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["input"] = new JsonObject { ["device_id"] = id } });
    }

    public void SetOutputDevice(string id)
    {
        Settings.OutputDeviceId = id;
        Run("SET_VOICE_SETTINGS", new JsonObject { ["output"] = new JsonObject { ["device_id"] = id } });
    }

    // ---- Browsing servers and channels ------------------------------------------------------

    public async Task<List<DiscordServer>> GetServersAsync()
    {
        var data = await SendAsync("GET_GUILDS");
        var list = new List<DiscordServer>();
        foreach (var g in data?["guilds"]?.AsArray() ?? [])
        {
            var id = g?["id"]?.ToString();
            if (id is null) continue;
            var name = g?["name"]?.ToString() ?? "Server";
            _guildNames[id] = name;
            list.Add(new DiscordServer(id, name));
        }
        return list;
    }

    /// <summary>The voice channels of a server, with who is in each (read in parallel; servers have a handful).</summary>
    public async Task<List<JoinableChannel>> GetVoiceChannelsAsync(string guildId)
    {
        var data = await SendAsync("GET_CHANNELS", new JsonObject { ["guild_id"] = guildId });
        var voice = (data?["channels"]?.AsArray() ?? [])
            .Where(c => c?["type"]?.GetValue<int>() is 2 or 13) // voice and stage channels
            .Select(c => (Id: c!["id"]!.ToString(), Name: c["name"]?.ToString() ?? "Voice"))
            .ToList();

        var details = await Task.WhenAll(voice.Select(async c =>
        {
            try
            {
                var channel = await SendAsync("GET_CHANNEL", new JsonObject { ["channel_id"] = c.Id });
                return (channel?["voice_states"]?.AsArray() ?? [])
                    .Select(v => FirstNonEmpty(v?["nick"], v?["user"]?["global_name"], v?["user"]?["username"]) ?? "Someone")
                    .ToList();
            }
            catch (DiscordException)
            {
                return [];
            }
        }));
        return voice.Select((c, i) => new JoinableChannel(c.Id, c.Name, details[i])).ToList();
    }

    public string? ServerName(string guildId) => _guildNames.TryGetValue(guildId, out var name) ? name : null;

    public void SetUserVolume(VoiceMember member, int volume)
    {
        member.Volume = Math.Clamp(volume, 0, 200);
        Run("SET_USER_VOICE_SETTINGS", new JsonObject { ["user_id"] = member.Id, ["volume"] = member.Volume });
    }

    public void SetUserMutedForMe(VoiceMember member, bool muted)
    {
        member.MutedForMe = muted;
        Run("SET_USER_VOICE_SETTINGS", new JsonObject { ["user_id"] = member.Id, ["mute"] = muted });
    }

    private async void Run(string cmd, JsonObject args)
    {
        if (State != DiscordState.Ready) return;
        try
        {
            var result = await SendAsync(cmd, args);
            if (cmd == "SET_VOICE_SETTINGS")
            {
                ApplyVoiceSettings(result);
                VoiceChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: {cmd} failed: {ex.Message}");
        }
    }

    // ---- Events from Discord ----------------------------------------------------------------

    private void OnEvent(string evt, JsonNode? data)
    {
        switch (evt)
        {
            case "VOICE_SETTINGS_UPDATE":
                ApplyVoiceSettings(data);
                VoiceChanged?.Invoke();
                break;
            case "VOICE_CHANNEL_SELECT":
            case "VOICE_STATE_CREATE":
            case "VOICE_STATE_UPDATE":
            case "VOICE_STATE_DELETE":
                QueueChannelRefresh();
                break;
            case "SPEAKING_START":
            case "SPEAKING_STOP":
                var userId = data?["user_id"]?.GetValue<string>();
                if (userId is null) break;
                bool speaking = evt == "SPEAKING_START";
                if (speaking ? _speaking.Add(userId) : _speaking.Remove(userId))
                    SpeakingChanged?.Invoke(userId, speaking);
                break;
        }
    }

    /// <summary>People joining or changing mute come in bursts; re-read the channel once per burst.</summary>
    private async void QueueChannelRefresh()
    {
        if (_refreshQueued || State != DiscordState.Ready) return;
        _refreshQueued = true;
        try
        {
            await Task.Delay(150);
            _refreshQueued = false;
            await RefreshChannelAsync();
        }
        catch (Exception ex)
        {
            _refreshQueued = false;
            Log.Write($"Discord: could not refresh the voice channel: {ex.Message}");
        }
    }

    // ---- Wire protocol ----------------------------------------------------------------------
    // Each message: opcode (int32, little endian), length (int32), then that many bytes of JSON.

    private enum Opcode { Handshake = 0, Frame = 1, Close = 2, Ping = 3, Pong = 4 }

    private async Task<JsonNode?> SendAsync(string cmd, JsonObject? args = null, string? evt = null, TimeSpan? timeout = null)
    {
        var nonce = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[nonce] = tcs;
        try
        {
            var message = new JsonObject { ["cmd"] = cmd, ["args"] = args ?? new JsonObject(), ["nonce"] = nonce };
            if (evt is not null) message["evt"] = evt;
            await WriteAsync(Opcode.Frame, message);
            return await tcs.Task.WaitAsync(timeout ?? RequestTimeout);
        }
        finally
        {
            _pending.TryRemove(nonce, out _);
        }
    }

    private async Task WriteAsync(Opcode op, JsonNode payload)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to Discord.");
        var body = Encoding.UTF8.GetBytes(payload.ToJsonString());
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), (int)op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);

        await _writeLock.WaitAsync();
        try
        {
            await pipe.WriteAsync(frame);
            await pipe.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Runs on a background thread. Replies complete their request; events go to the UI thread.</summary>
    private async Task ReadLoopAsync(NamedPipeClientStream pipe, int generation)
    {
        var header = new byte[8];
        string? closeReason = null;
        try
        {
            while (true)
            {
                await pipe.ReadExactlyAsync(header);
                var op = (Opcode)BitConverter.ToInt32(header, 0);
                int length = BitConverter.ToInt32(header, 4);
                if (length < 0 || length > 16 * 1024 * 1024)
                    throw new IOException("Discord sent a message that doesn't make sense.");
                var body = new byte[length];
                await pipe.ReadExactlyAsync(body);

                switch (op)
                {
                    case Opcode.Frame:
                        HandleFrame(JsonNode.Parse(body));
                        break;
                    case Opcode.Ping:
                        await WriteAsync(Opcode.Pong, JsonNode.Parse(body) ?? new JsonObject());
                        break;
                    case Opcode.Close:
                        closeReason = JsonNode.Parse(body)?["message"]?.GetValue<string>();
                        throw new IOException(closeReason ?? "Discord closed the connection.");
                }
            }
        }
        catch (Exception ex)
        {
            var error = closeReason is not null ? new DiscordException(0, closeReason) : ex;
            _ready?.TrySetException(error);
            foreach (var pending in _pending.Values)
                pending.TrySetException(error is DiscordException ? error : new IOException("Lost the connection to Discord.", ex));

            _ = _dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return; // already replaced
                Log.Write($"Discord: disconnected ({closeReason ?? ex.Message}).");
                bool wasReady = State == DiscordState.Ready;
                Disconnect();
                if (wasReady || State is DiscordState.WaitingForApproval)
                    SetState(closeReason is null ? DiscordState.NotRunning : DiscordState.Failed, closeReason);
            });
        }
    }

    private void HandleFrame(JsonNode? message)
    {
        if (message is null) return;
        var nonce = message["nonce"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? message["nonce"]!.GetValue<string>() : null;
        var evt = message["evt"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? message["evt"]!.GetValue<string>() : null;
        var data = message["data"];

        if (nonce is not null && _pending.TryGetValue(nonce, out var request))
        {
            if (evt == "ERROR")
                request.TrySetException(new DiscordException(
                    data?["code"]?.GetValue<int>() ?? 0,
                    data?["message"]?.GetValue<string>() ?? "Discord reported an error."));
            else
                request.TrySetResult(data);
            return;
        }

        if (message["cmd"]?.GetValue<string>() != "DISPATCH" || evt is null) return;
        if (evt == "READY")
        {
            _ready?.TrySetResult(data);
            return;
        }
        // Detach the data from the message so the UI thread owns it.
        var copy = data?.DeepClone();
        _dispatcher.BeginInvoke(() => OnEvent(evt, copy));
    }

    private void Disconnect()
    {
        _generation++;
        var pipe = _pipe;
        _pipe = null;
        pipe?.Dispose();
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("Disconnected from Discord."));
        _pending.Clear();
        _subscribedChannel = null;
        _speakingSubscribed = false;
        _speaking.Clear();
        Channel = null;
    }

    private void SetState(DiscordState state, string? problem)
    {
        if (State == state && Problem == problem) return;
        State = state;
        Problem = problem;
        StateChanged?.Invoke();
    }
}

internal sealed class DiscordException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}
