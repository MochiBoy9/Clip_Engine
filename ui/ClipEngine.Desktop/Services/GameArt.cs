using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace ClipEngine.Desktop.Services;

// A game's pictures: its wide store banner, and its program's icon. Either can be missing.
internal sealed record GameArtwork(Bitmap? Banner, Bitmap? Icon)
{
    public bool IsEmpty => Banner is null && Icon is null;
}

// Pictures for each game, in place of its initials. They come from this PC first: Steam's own cache of its library
// art, and the icon of the game's program. When the setting allows it, banners missing from the PC are fetched once
// from the Steam store (also for games bought elsewhere) and kept in DataDir/art.
internal static class GameArt
{
    private static readonly ConcurrentDictionary<string, GameArtwork> Art = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = CreateClient();
    private static readonly TimeSpan RetryMissing = TimeSpan.FromDays(7);

    // A game's art arrived or changed. Raised on whichever thread loaded it.
    public static event Action<string>? Changed;

    public static string CacheDir => Path.Combine(AppPaths.DataDir, "art");

    public static GameArtwork? Get(string? game) =>
        game is not null && Art.TryGetValue(game, out var art) && !art.IsEmpty ? art : null;

    public static void Set(string game, GameArtwork art)
    {
        Art[game] = art;
        Changed?.Invoke(game);
    }

    // Loads art for each game: what's already in the cache first, so it shows at once, then whatever is missing.
    public static async Task LoadAsync(IEnumerable<string> games, GameScan scan, bool online, CancellationToken cancel = default)
    {
        var list = games.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var game in list)
        {
            cancel.ThrowIfCancellationRequested();
            var banner = Decode(CacheFile(game, "banner.jpg"), 460);
            var icon = Decode(CacheFile(game, "icon.png"), 128);
            if (banner is not null || icon is not null) Set(game, new GameArtwork(banner, icon));
        }

        foreach (var game in list)
        {
            cancel.ThrowIfCancellationRequested();
            var current = Get(game);
            var banner = current?.Banner;
            var icon = current?.Icon;
            var install = scan.Find(game);

            if (banner is null)
            {
                var file = await FindBannerAsync(game, install, scan.SteamPath, online, cancel);
                banner = file is null ? null : Decode(file, 460);
            }
            if (icon is null && install?.Exe is { } exe && OperatingSystem.IsWindows())
                icon = await IconAsync(game, exe, cancel);
            if (banner != current?.Banner || icon != current?.Icon) Set(game, new GameArtwork(banner, icon));
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<Bitmap?> IconAsync(string game, string exe, CancellationToken cancel)
    {
        var file = CacheFile(game, "icon.png");
        return await Task.Run(() => ShellIcon.SavePng(exe, file, 256), cancel) ? Decode(file, 128) : null;
    }

    // DataDir/art/<game>.banner.jpg, with the name made safe for a file.
    internal static string CacheFile(string game, string kind)
    {
        var safe = string.Concat(game.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_')).Trim('_');
        return Path.Combine(CacheDir, $"{(safe.Length == 0 ? "game" : safe)}.{kind}");
    }

    private static async Task<string?> FindBannerAsync(string game, GameInstall? install, string? steamPath, bool online,
                                                       CancellationToken cancel)
    {
        var target = CacheFile(game, "banner.jpg");
        var appId = install?.SteamAppId ?? GameLibrary.SteamIds.GetValueOrDefault(game);

        // Steam keeps the art of every game in its library.
        if (appId > 0 && steamPath is not null && SteamCacheBanner(steamPath, appId) is { } local)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.Copy(local, target, overwrite: true);
                return target;
            }
            catch (IOException) { return local; }
            catch (UnauthorizedAccessException) { return local; }
        }
        if (!online) return null;

        // Asked once a week at most when Steam has nothing.
        var missing = CacheFile(game, "missing");
        if (File.Exists(missing) && DateTime.UtcNow - File.GetLastWriteTimeUtc(missing) < RetryMissing) return null;
        try
        {
            if (appId <= 0) appId = await FindSteamAppAsync(game, cancel) ?? 0;
            var url = appId > 0 ? await HeaderImageAsync(appId, cancel) : null;
            if (url is not null)
            {
                var bytes = await Http.GetByteArrayAsync(url, cancel);
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllBytesAsync(target, bytes, cancel);
                File.Delete(missing);
                return target;
            }
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(missing, "", cancel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException
                                      or UnauthorizedAccessException)
        {
            // Offline or Steam is down: try again next time the app starts.
        }
        return null;
    }

    // Steam's library cache: <steam>/appcache/librarycache/<id>/header.jpg in newer clients (sometimes a folder deeper),
    // <id>_header.jpg in older ones.
    internal static string? SteamCacheBanner(string steamPath, int appId)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        var old = Path.Combine(cache, $"{appId}_header.jpg");
        if (File.Exists(old)) return old;
        var dir = Path.Combine(cache, appId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!Directory.Exists(dir)) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(dir, "header.jpg", options).FirstOrDefault();
    }

    private static async Task<int?> FindSteamAppAsync(string game, CancellationToken cancel)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(game)}&l=english&cc=US";
        return ParseStoreSearch(await Http.GetStringAsync(url, cancel), game);
    }

    private static async Task<string?> HeaderImageAsync(int appId, CancellationToken cancel)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
        return ParseHeaderImage(await Http.GetStringAsync(url, cancel), appId);
    }

    // The app whose name is the game's, ignoring case, spaces and symbols. Nothing for a near miss, so a game added in
    // Detection Studio never gets some other game's picture.
    internal static int? ParseStoreSearch(string json, string game)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return null;
        var key = GameLibrary.Normalize(game);
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && GameLibrary.Normalize(name.GetString() ?? "") == key
                && item.TryGetProperty("id", out var id) && id.TryGetInt32(out var appId))
                return appId;
        }
        return null;
    }

    internal static string? ParseHeaderImage(string json, int appId)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(appId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var app)) return null;
        if (!app.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
        if (!app.TryGetProperty("data", out var data) || !data.TryGetProperty("header_image", out var header)) return null;
        var url = header.GetString();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;
    }

    private static Bitmap? Decode(string path, int width)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("ClipEngine", UpdateFeed.CurrentVersion.ToString()));
        return client;
    }
}
