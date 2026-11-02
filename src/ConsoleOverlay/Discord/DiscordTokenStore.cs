using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ConsoleOverlay.Settings;

namespace ConsoleOverlay.Discord;

internal sealed class DiscordToken
{
    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Keeps the Discord sign-in in %APPDATA%\ConsoleOverlay\discord-signin.dat, encrypted with Windows (DPAPI)
/// so only this Windows account can read it. Delete the file to sign out.
/// </summary>
internal static class DiscordTokenStore
{
    private static readonly byte[] Entropy = "ConsoleOverlay.Discord"u8.ToArray();

    public static string FilePath { get; } = Path.Combine(SettingsStore.Folder, "discord-signin.dat");

    public static DiscordToken? Load(string clientId)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            var token = JsonSerializer.Deserialize<DiscordToken>(bytes);
            // A sign-in belongs to one Discord app; ignore it if the settings now name another.
            return token is not null && token.ClientId == clientId && token.AccessToken.Length > 0 ? token : null;
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not read the saved sign-in, signing in again: {ex.Message}");
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            File.Delete(FilePath);
            Log.Write("Discord: signed out.");
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not delete the saved sign-in: {ex.Message}");
        }
    }

    public static void Save(DiscordToken token)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, bytes);
        }
        catch (Exception ex)
        {
            Log.Write($"Discord: could not save the sign-in: {ex.Message}");
        }
    }
}
