using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleOverlay.Steam;

/// <summary>A game on a store shelf. Prices are in the smallest unit (cents, kopecks); null when Steam gave none.</summary>
internal sealed record StoreItem(int AppId, string Name, string ImageUrl, int? FinalPrice, int? OriginalPrice, int DiscountPercent, string? Currency)
{
    public static string HeaderUrl(int appId) => $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";
}

/// <summary>What the store page says about one game, shown when it is focused.</summary>
internal sealed record StoreDetails(int AppId, string Name, string? Description, string? Price, string? OriginalPrice, int DiscountPercent,
    bool IsFree, string? ReleaseDate, bool ComingSoon, IReadOnlyList<string> Genres, string? Developer);

/// <summary>
/// Reads Steam's public store data (the same JSON the store web pages load): the front page shelves, search, and
/// one game's details. No account and no key: it only reads. Buying always happens on Valve's own store pages,
/// signed in with your Steam account, see <see cref="StoreLink"/>.
/// </summary>
internal static class SteamStore
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<int, StoreDetails> Details = [];
    private static (DateTime At, Dictionary<string, List<StoreItem>> Shelves)? _featured;

    /// <summary>Shelves on the store's front page, by id, in the order they are shown.</summary>
    public static readonly (string Id, string Name)[] Shelves =
    [
        ("specials", "Specials"),
        ("top_sellers", "Top sellers"),
        ("new_releases", "New releases"),
        ("coming_soon", "Coming soon"),
    ];

    public static string StoreLink(int appId) => $"https://store.steampowered.com/app/{appId}/";
    public const string StoreHome = "https://store.steampowered.com/";
    public const string CartLink = "https://store.steampowered.com/cart/";

    /// <summary>The game's page in the Steam app (where your payment details are already saved).</summary>
    public static string ClientStoreLink(int appId) => $"steam://store/{appId}";

    /// <summary>Front page shelves, read again after ten minutes.</summary>
    public static async Task<Dictionary<string, List<StoreItem>>> FeaturedAsync(CancellationToken cancel)
    {
        if (_featured is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            return cached.Shelves;

        var json = await GetJsonAsync($"https://store.steampowered.com/api/featuredcategories/?{Region()}", cancel);
        var shelves = new Dictionary<string, List<StoreItem>>();
        foreach (var (id, _) in Shelves)
        {
            var list = new List<StoreItem>();
            foreach (var item in json?[id]?["items"]?.AsArray() ?? [])
            {
                if (item is null || Int(item["type"]) is not (0 or null)) continue; // 0 = a game; skip bundles and packages
                if (Int(item["id"]) is not { } appId || list.Any(i => i.AppId == appId)) continue;
                var name = item["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(name)) continue;
                int discount = Int(item["discount_percent"]) ?? 0;
                int? final = Int(item["final_price"]), original = Int(item["original_price"]);
                if (final == 0 && original is null) final = null; // not priced yet (coming soon), rather than free
                list.Add(new StoreItem(appId, name,
                    item["large_capsule_image"]?.GetValue<string>() ?? item["header_image"]?.GetValue<string>() ?? StoreItem.HeaderUrl(appId),
                    final, discount > 0 ? original : null, discount,
                    item["currency"]?.GetValue<string>()));
            }
            shelves[id] = list;
        }
        _featured = (DateTime.UtcNow, shelves);
        return shelves;
    }

    /// <summary>Games matching <paramref name="term"/>, as the store's search box suggests them.</summary>
    public static async Task<List<StoreItem>> SearchAsync(string term, CancellationToken cancel)
    {
        var json = await GetJsonAsync(
            $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(term)}&{Region()}", cancel);
        var list = new List<StoreItem>();
        foreach (var item in json?["items"]?.AsArray() ?? [])
        {
            if (item is null || item["type"]?.GetValue<string>() != "app") continue;
            if (Int(item["id"]) is not { } appId) continue;
            var price = item["price"];
            int? final = Int(price?["final"]), initial = Int(price?["initial"]);
            int discount = final is { } f && initial is { } i && i > 0 && f < i ? (int)Math.Round(100.0 * (i - f) / i) : 0;
            list.Add(new StoreItem(appId, item["name"]?.GetValue<string>() ?? $"App {appId}", StoreItem.HeaderUrl(appId),
                final, discount > 0 ? initial : null, discount, price?["currency"]?.GetValue<string>()));
        }
        return list;
    }

    /// <summary>Price, description and release date from the game's store page data. Kept for the session.</summary>
    public static async Task<StoreDetails?> DetailsAsync(int appId, CancellationToken cancel)
    {
        lock (Details)
            if (Details.TryGetValue(appId, out var cached)) return cached;

        var json = await GetJsonAsync($"https://store.steampowered.com/api/appdetails/?appids={appId}&{Region()}", cancel);
        var entry = json?[appId.ToString(CultureInfo.InvariantCulture)];
        if (entry?["success"]?.GetValue<bool>() != true) return null;
        var data = entry["data"];
        if (data is null) return null;

        var price = data["price_overview"];
        int discount = Int(price?["discount_percent"]) ?? 0;
        var details = new StoreDetails(
            appId,
            data["name"]?.GetValue<string>() ?? "",
            System.Net.WebUtility.HtmlDecode(data["short_description"]?.GetValue<string>() ?? "").Trim(),
            price?["final_formatted"]?.GetValue<string>(),
            discount > 0 ? price?["initial_formatted"]?.GetValue<string>() : null,
            discount,
            data["is_free"]?.GetValue<bool>() == true,
            data["release_date"]?["date"]?.GetValue<string>(),
            data["release_date"]?["coming_soon"]?.GetValue<bool>() == true,
            (data["genres"]?.AsArray() ?? []).Select(g => g?["description"]?.GetValue<string>()).OfType<string>().Take(3).ToList(),
            data["developers"]?.AsArray().FirstOrDefault()?.GetValue<string>());
        lock (Details)
            Details[appId] = details;
        return details;
    }

    /// <summary>A shelf price from cents, for when the details haven't loaded yet ("₽ 599", "€19.99").</summary>
    public static string? FormatPrice(int? amount, string? currency)
    {
        if (amount is not { } value) return null;
        if (value == 0) return "Free";
        var culture = CultureFor(currency);
        double units = value / 100.0;
        if (culture is null)
            return $"{units.ToString("0.00", CultureInfo.CurrentCulture)} {currency}".Trim();
        return units.ToString(units == Math.Floor(units) ? "C0" : "C", culture);
    }

    // ---- Helpers ----------------------------------------------------------------------------

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ConsoleOverlay/1.0");
        return client;
    }

    private static async Task<JsonNode?> GetJsonAsync(string url, CancellationToken cancel)
    {
        using var response = await Http.GetAsync(url, cancel);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancel);
        return JsonNode.Parse(text);
    }

    /// <summary>Country and language for prices and text: Windows' region and display language.</summary>
    private static string Region()
    {
        string cc = "US";
        try { cc = RegionInfo.CurrentRegion.TwoLetterISORegionName; } catch { }
        return $"cc={cc}&l={Language()}";
    }

    /// <summary>Steam's names for languages ("russian", "german").</summary>
    private static string Language() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "ru" => "russian",
        "uk" => "ukrainian",
        "de" => "german",
        "fr" => "french",
        "es" => "spanish",
        "it" => "italian",
        "pl" => "polish",
        "pt" => "brazilian",
        "tr" => "turkish",
        "cs" => "czech",
        "nl" => "dutch",
        "sv" => "swedish",
        "fi" => "finnish",
        "da" => "danish",
        "no" or "nb" => "norwegian",
        "hu" => "hungarian",
        "ja" => "japanese",
        "ko" => "koreana",
        "zh" => "schinese",
        _ => "english",
    };

    private static readonly Dictionary<string, CultureInfo?> CurrencyCultures = [];

    private static CultureInfo? CultureFor(string? currency)
    {
        if (string.IsNullOrEmpty(currency)) return null;
        lock (CurrencyCultures)
        {
            if (CurrencyCultures.TryGetValue(currency, out var known)) return known;
            CultureInfo? found = null;
            try
            {
                // Prefer the user's own culture when it uses this currency, so the number looks familiar.
                if (new RegionInfo(CultureInfo.CurrentCulture.Name).ISOCurrencySymbol == currency)
                    found = CultureInfo.CurrentCulture;
            }
            catch { }
            found ??= CultureInfo.GetCultures(CultureTypes.SpecificCultures).FirstOrDefault(c =>
            {
                try { return new RegionInfo(c.Name).ISOCurrencySymbol == currency; }
                catch { return false; }
            });
            CurrencyCultures[currency] = found;
            return found;
        }
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue(out int i)) return i;
        if (value.TryGetValue(out long l)) return (int)l;
        if (value.TryGetValue(out string? s) && int.TryParse(s, out int parsed)) return parsed;
        return null;
    }
}
