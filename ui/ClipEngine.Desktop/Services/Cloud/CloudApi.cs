using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ClipEngine.Desktop.Services.Squad;

namespace ClipEngine.Desktop.Services.Cloud;

internal sealed record CloudAccount(string Id, string Name, string? Handle, string Plan, string PlanLabel, long Used, long Limit, int Clips,
                                    string? PlanKey = null, string FriendCode = "", DateTimeOffset? HandleChangeAt = null,
                                    string? RoleLabel = null)
{
    public static CloudAccount From(JsonNode? n) => new(
        n?["id"]?.GetValue<string>() ?? "",
        n?["name"]?.GetValue<string>() ?? "",
        n?["handle"]?.GetValue<string>(),
        n?["plan"]?.GetValue<string>() ?? "free",
        n?["planLabel"]?.GetValue<string>() ?? "Free",
        n?["storage"]?["used"]?.GetValue<long>() ?? 0,
        n?["storage"]?["limit"]?.GetValue<long>() ?? 0,
        n?["clips"]?.GetValue<int>() ?? 0,
        n?["planKey"]?.GetValue<string>(),
        n?["friendCode"]?.GetValue<string>() ?? "",
        n?["handleChangeAt"] is JsonValue at && at.TryGetValue<long>(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null,
        n?["roleLabel"] is JsonValue role && role.TryGetValue<string>(out var label) && label.Length > 0 ? label : null);
}

internal sealed record CloudClip(string Id, string Code, string? LocalId, string Title, string Game, long Recorded, double Seconds,
                                 long Size, bool Uploaded, bool Thumb, string Visibility, bool Featured, int Views)
{
    public static CloudClip From(JsonNode? n) => new(
        n?["id"]?.GetValue<string>() ?? "",
        n?["code"]?.GetValue<string>() ?? "",
        n?["localId"]?.GetValue<string>(),
        n?["title"]?.GetValue<string>() ?? "",
        n?["game"]?.GetValue<string>() ?? "",
        n?["recorded"]?.GetValue<long>() ?? 0,
        n?["seconds"]?.GetValue<double>() ?? 0,
        n?["size"]?.GetValue<long>() ?? 0,
        n?["uploaded"]?.GetValue<bool>() ?? false,
        n?["thumb"]?.GetValue<bool>() ?? false,
        n?["visibility"]?.GetValue<string>() ?? "private",
        n?["featured"]?.GetValue<bool>() ?? false,
        n?["views"]?.GetValue<int>() ?? 0);
}

// The cloud library's HTTP side (server/src/library.js).
internal sealed class CloudApi(string server, HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(15) };
    private readonly HttpClient _http = http ?? Shared;

    public string Server { get; } = server.TrimEnd('/');

    public string ShareLink(string code) => $"{Server}/c/{code}";
    public string ProfileLink(string handle) => $"{Server}/u/{handle}";

    // Names a clip on this PC so uploading it again finds the copy already in the cloud.
    public static string LocalId(string path, long bytes) => $"{Path.GetFileName(path)}|{bytes}";

    public async Task<(string Token, CloudAccount Account)> CreateAsync(string name, string device)
    {
        var reply = await SendAsync(HttpMethod.Post, "/v1/accounts", null, new JsonObject { ["name"] = name, ["device"] = device });
        return (reply?["token"]?.GetValue<string>() ?? "", CloudAccount.From(reply?["account"]));
    }

    public async Task<(string Token, CloudAccount Account)> SignInAsync(string code, string device)
    {
        var reply = await SendAsync(HttpMethod.Post, "/v1/accounts/signin", null, new JsonObject { ["code"] = code, ["device"] = device });
        return (reply?["token"]?.GetValue<string>() ?? "", CloudAccount.From(reply?["account"]));
    }

    public async Task<CloudAccount> AccountAsync(string account, string token) =>
        CloudAccount.From(await SendAsync(HttpMethod.Get, $"/v1/accounts/{account}/account", token, null));

    public async Task<CloudAccount> UpdateAccountAsync(string account, string token, string? name, string? handle)
    {
        var body = new JsonObject();
        if (name is not null) body["name"] = name;
        if (handle is not null) body["handle"] = handle;
        return CloudAccount.From(await SendAsync(HttpMethod.Patch, $"/v1/accounts/{account}/account", token, body));
    }

    // Puts a subscription's license key on the account, which sets its storage to the key's plan.
    public async Task<CloudAccount> SetPlanAsync(string account, string token, string key) =>
        CloudAccount.From(await SendAsync(HttpMethod.Post, $"/v1/accounts/{account}/plan", token, new JsonObject { ["key"] = key }));

    public async Task<string> LinkCodeAsync(string account, string token) =>
        (await SendAsync(HttpMethod.Post, $"/v1/accounts/{account}/link", token, new JsonObject()))?["code"]?.GetValue<string>() ?? "";

    public async Task<(IReadOnlyList<CloudClip> Clips, CloudAccount Account)> ClipsAsync(string account, string token)
    {
        var reply = await SendAsync(HttpMethod.Get, $"/v1/accounts/{account}/clips", token, null);
        return ((reply?["clips"]?.AsArray() ?? []).Select(CloudClip.From).ToList(), CloudAccount.From(reply?["account"]));
    }

    public async Task<CloudClip> AddClipAsync(string account, string token, JsonObject clip) =>
        CloudClip.From(await SendAsync(HttpMethod.Post, $"/v1/accounts/{account}/clips", token, clip));

    public async Task<CloudClip> UpdateClipAsync(string account, string token, string clip, JsonObject changes) =>
        CloudClip.From(await SendAsync(HttpMethod.Patch, $"/v1/accounts/{account}/clips/{clip}", token, changes));

    public Task DeleteClipAsync(string account, string token, string clip) =>
        SendAsync(HttpMethod.Delete, $"/v1/accounts/{account}/clips/{clip}", token, null);

    public async Task UploadAsync(string account, string token, string clip, string kind, string file, string contentType,
                                  CancellationToken cancel = default)
    {
        await using var stream = File.OpenRead(file);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Server}/v1/accounts/{account}/clips/{clip}/{kind}")
        {
            Content = new StreamContent(stream),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content.Headers.ContentLength = stream.Length;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancel);
        await ThrowIfFailed(response);
    }

    internal Task<JsonNode?> CallAsync(HttpMethod method, string path, string token, JsonObject? body) =>
        SendAsync(method, path, token, body);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, string? token, JsonObject? body)
    {
        if (Server.Length == 0) throw new SquadException("No online service is set. Add its address in Settings > Online.");
        using var request = new HttpRequestMessage(method, Server + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request);
        }
        catch (HttpRequestException e)
        {
            throw new SquadException($"Couldn't reach Clip Engine's online service: {e.Message}");
        }
        using (response)
        {
            await ThrowIfFailed(response);
            var text = await response.Content.ReadAsStringAsync();
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            message = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["error"]?.GetValue<string>();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        throw new SquadException(message ?? $"The online service answered {(int)response.StatusCode}.",
                                 response.StatusCode);
    }
}
