using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClipEngine.Desktop.Services;

// A newer Clip Engine on GitHub Releases, and the zip that installs it.
internal sealed record UpdateRelease(Version Version, string Tag, string Notes, string PageUrl, string AssetName,
                                     string AssetUrl, long AssetBytes);

// Finds and downloads releases. They're the zips the Release workflow publishes, from the repository the app was
// built from (or the public downloads repository, when the build names one). A private repository needs a GitHub
// token that can read it.
internal static class UpdateFeed
{
    // Set by the build (see ClipEngine.Desktop.csproj); the source repository otherwise.
    public static string DefaultRepo { get; } =
        typeof(UpdateFeed).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepo" && !string.IsNullOrWhiteSpace(a.Value))?.Value ?? "MochiBoy9/Clip_Engine";

    public static Version CurrentVersion { get; } = Normalize(typeof(UpdateFeed).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    // "v0.5.1" or "0.5.1" as 0.5.1; null for anything else.
    public static Version? ParseVersion(string? tag)
    {
        var text = (tag ?? "").Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0) text = text[..dash];
        return Version.TryParse(text, out var version) && text.Contains('.') ? Normalize(version) : null;
    }

    // The release in GitHub's /releases/latest answer, when it has a Windows zip.
    public static UpdateRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = String(root, "tag_name");
        if (ParseVersion(tag) is not { } version) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = String(asset, "name");
            if (!name.StartsWith("Clip_Engine-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith("-windows-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            var url = String(asset, "url");
            if (url.Length == 0) continue;
            var bytes = asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0;
            return new UpdateRelease(version, tag, String(root, "body"), String(root, "html_url"), name, url, bytes);
        }
        return null;
    }

    // The latest release when it's newer than this copy, else null. Throws UpdateException when GitHub can't be asked.
    public static async Task<UpdateRelease?> CheckAsync(string repo, string? token, CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
        Authorize(request, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cancel);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"Couldn't reach GitHub: {e.Message}");
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new UpdateException(string.IsNullOrWhiteSpace(token)
                    ? $"No releases found for {repo}. If the repository is private, add a GitHub token below."
                    : $"No releases found for {repo}, or the token can't read it.");
            }
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                throw new UpdateException("GitHub didn't accept the token. Check it hasn't expired.");
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}. Try again later.");
            var release = ParseRelease(await response.Content.ReadAsStringAsync(cancel));
            return release is not null && release.Version > CurrentVersion ? release : null;
        }
    }

    // Downloads the release's zip to file, reporting progress from 0 to 1.
    public static async Task DownloadAsync(UpdateRelease release, string? token, string file, IProgress<double>? progress,
                                           CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, release.AssetUrl);
        // GitHub answers with a redirect to the file itself; HttpClient doesn't send the token there.
        Authorize(request, token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed: GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var total = response.Content.Headers.ContentLength ?? release.AssetBytes;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var partial = file + ".part";
            await using (var source = await response.Content.ReadAsStreamAsync(cancel))
            await using (var target = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancel)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
            }
            File.Move(partial, file, true);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"The download failed: {e.Message}");
        }
    }

    private static void Authorize(HttpRequestMessage request, string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClipEngine", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

internal sealed class UpdateException(string message) : Exception(message);

// Puts a release's files in place of the running copy's. Windows lets a running program be renamed but not
// overwritten, so each file is moved aside as <name>.old, the new one copied in, and the old ones deleted the next
// time the app starts. If anything fails part way, the files moved aside are put back.
internal static class UpdateInstaller
{
    public const string AppExe = "ClipEngine.exe";
    private const string OldSuffix = ".old";

    // Unzips a release into folder (emptied first) and returns the folder inside it that holds ClipEngine.exe.
    public static string Unpack(string zip, string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        try
        {
            ZipFile.ExtractToDirectory(zip, folder);
        }
        catch (InvalidDataException)
        {
            throw new UpdateException("That isn't a Clip Engine zip, or it didn't download completely.");
        }
        var exe = Directory.EnumerateFiles(folder, AppExe, SearchOption.AllDirectories)
            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        if (exe is null) throw new UpdateException($"The zip has no {AppExe} in it, so it isn't a Clip Engine release.");
        var root = Path.GetDirectoryName(exe)!;
        if (!File.Exists(Path.Combine(root, "clip_engine.exe")))
            throw new UpdateException("The zip has no clip_engine.exe next to ClipEngine.exe, so it isn't a complete release.");
        return root;
    }

    // The version of a ClipEngine.exe, or null when it can't be read (or off Windows, for the single-file app).
    public static Version? VersionOf(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            return info.FileMajorPart + info.FileMinorPart + info.FileBuildPart > 0
                ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Copies everything in source over target, keeping what's replaced as .old until Install succeeds.
    public static void Install(string source, string target)
    {
        var moved = new List<string>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    var aside = destination + OldSuffix;
                    DeleteIfPossible(aside);
                    if (File.Exists(aside)) aside = $"{destination}.{Guid.NewGuid():N}{OldSuffix}";
                    File.Move(destination, aside);
                    moved.Add(destination);
                    moved.Add(aside);
                }
                File.Copy(file, destination);
                added.Add(destination);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var file in added) DeleteIfPossible(file);
            for (var i = 0; i + 1 < moved.Count; i += 2)
            {
                try
                {
                    File.Move(moved[i + 1], moved[i], true);
                }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                {
                }
            }
            throw new UpdateException(e is UnauthorizedAccessException
                ? $"Windows didn't allow replacing the files in {target}. Move Clip Engine to a folder you own, like Documents, or run it as administrator once."
                : $"Couldn't replace the files in {target}: {e.Message}");
        }
    }

    // Deletes what an earlier update moved aside, now that nothing runs from it.
    public static void CleanUp(string target)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(target, "*" + OldSuffix, SearchOption.AllDirectories)) DeleteIfPossible(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteIfPossible(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// The GitHub token for a private repository, kept encrypted for this Windows account.
internal static class UpdateToken
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ClipEngine.UpdateToken");

    public static string Protect(string token)
    {
        if (!OperatingSystem.IsWindows() || token.Length == 0) return "";
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }

    public static string? Unprotect(string? stored)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(stored)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
