using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace ClipEngine.Desktop.Services.Squad;

internal sealed class SquadException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
    public bool NotAMember => Status is HttpStatusCode.Unauthorized;
}

internal sealed record SquadMemberInfo(string Id, string Name, bool Owner, bool Online);

internal sealed record SquadInfo(string Id, string Name, string Invite, string You, IReadOnlyList<SquadMemberInfo> Members,
                                 long StorageUsed, long StorageLimit)
{
    public static SquadInfo From(JsonNode? n) => new(
        n?["id"]?.GetValue<string>() ?? "",
        n?["name"]?.GetValue<string>() ?? "",
        n?["invite"]?.GetValue<string>() ?? "",
        n?["you"]?.GetValue<string>() ?? "",
        (n?["members"]?.AsArray() ?? []).Select(m => new SquadMemberInfo(
            m?["id"]?.GetValue<string>() ?? "", m?["name"]?.GetValue<string>() ?? "",
            m?["owner"]?.GetValue<bool>() ?? false, m?["online"]?.GetValue<bool>() ?? false)).ToList(),
        n?["storage"]?["used"]?.GetValue<long>() ?? 0,
        n?["storage"]?["limit"]?.GetValue<long>() ?? 0);
}

internal sealed record SquadMoment(string Id, string Game, string Type, long At, string By, string ByName, string Text, bool Star,
                                   IReadOnlyList<SquadView> Views)
{
    public static SquadMoment From(JsonNode? n) => new(
        n?["id"]?.GetValue<string>() ?? "",
        n?["game"]?.GetValue<string>() ?? "",
        n?["type"]?.GetValue<string>() ?? "",
        n?["at"]?.GetValue<long>() ?? 0,
        n?["by"]?.GetValue<string>() ?? "",
        n?["byName"]?.GetValue<string>() ?? "",
        n?["text"]?.GetValue<string>() ?? "",
        n?["star"]?.GetValue<bool>() ?? false,
        (n?["views"]?.AsArray() ?? []).Select(v => new SquadView(
            v?["member"]?.GetValue<string>() ?? "", v?["name"]?.GetValue<string>() ?? "",
            v?["utc_start"]?.GetValue<long>() ?? 0, v?["seconds"]?.GetValue<double>() ?? 0,
            v?["uploaded"]?.GetValue<bool>() ?? false, v?["mine"]?.GetValue<bool>() ?? false)).ToList());
}

// The squad service's HTTP side (server/src/index.js): creating and joining squads, the library,
// and uploading and downloading views.
internal sealed class SquadApi(string server, HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient _http = http ?? Shared;

    public string Server { get; } = server.TrimEnd('/');

    public async Task<(string Token, SquadInfo Squad)> CreateAsync(string name, string device, string? squadName, CancellationToken cancel = default)
    {
        var body = new JsonObject { ["name"] = name, ["device"] = device };
        if (!string.IsNullOrWhiteSpace(squadName)) body["squad"] = squadName;
        var reply = await SendAsync(HttpMethod.Post, "/v1/squads", null, body, cancel);
        return (reply?["token"]?.GetValue<string>() ?? "", SquadInfo.From(reply?["squad"]));
    }

    public async Task<(string Token, SquadInfo Squad)> JoinAsync(string invite, string name, string device, CancellationToken cancel = default)
    {
        var reply = await SendAsync(HttpMethod.Post, "/v1/join", null, new JsonObject { ["invite"] = invite, ["name"] = name, ["device"] = device }, cancel);
        return (reply?["token"]?.GetValue<string>() ?? "", SquadInfo.From(reply?["squad"]));
    }

    public async Task<SquadInfo> InfoAsync(string squad, string token, CancellationToken cancel = default) =>
        SquadInfo.From(await SendAsync(HttpMethod.Get, $"/v1/squads/{squad}/squad", token, null, cancel));

    public async Task<SquadInfo> RotateInviteAsync(string squad, string token) =>
        SquadInfo.From(await SendAsync(HttpMethod.Post, $"/v1/squads/{squad}/invite/rotate", token, new JsonObject()));

    public async Task<SquadInfo> RenameAsync(string squad, string token, string name) =>
        SquadInfo.From(await SendAsync(HttpMethod.Patch, $"/v1/squads/{squad}/members/me", token, new JsonObject { ["name"] = name }));

    public Task LeaveAsync(string squad, string token) => SendAsync(HttpMethod.Delete, $"/v1/squads/{squad}/members/me", token, null);

    public Task RemoveAsync(string squad, string token, string member) => SendAsync(HttpMethod.Delete, $"/v1/squads/{squad}/members/{member}", token, null);

    public Task StarAsync(string squad, string token, string moment, bool star) =>
        SendAsync(HttpMethod.Post, $"/v1/squads/{squad}/moments/{moment}/star", token, new JsonObject { ["star"] = star });

    public async Task<(IReadOnlyList<SquadMoment> Moments, long? Next)> LibraryAsync(string squad, string token, long? before = null,
                                                                                    CancellationToken cancel = default)
    {
        var path = $"/v1/squads/{squad}/library?limit=40" + (before is { } b ? $"&before={b}" : "");
        var reply = await SendAsync(HttpMethod.Get, path, token, null, cancel);
        var moments = (reply?["moments"]?.AsArray() ?? []).Select(SquadMoment.From).ToList();
        var next = reply?["next"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : (long?)null;
        return (moments, next);
    }

    public async Task UploadAsync(string squad, string token, string moment, string kind, string file, string contentType,
                                  CancellationToken cancel = default)
    {
        await using var stream = File.OpenRead(file);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Server}/v1/squads/{squad}/moments/{moment}/views/me/{kind}")
        {
            Content = UploadPace.Content(stream),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Content.Headers.ContentLength = stream.Length;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, cancel);
        await ThrowIfFailed(response, cancel);
    }

    // Downloads a view (or its thumbnail) to file. False when it isn't in the library.
    public async Task<bool> DownloadAsync(string squad, string token, string moment, string member, string kind, string file,
                                          CancellationToken cancel = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Server}/v1/squads/{squad}/moments/{moment}/views/{member}/{kind}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        await ThrowIfFailed(response, cancel);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var partial = file + ".part";
        await using (var output = File.Create(partial))
        {
            await response.Content.CopyToAsync(output, cancel);
        }
        File.Move(partial, file, overwrite: true);
        return true;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, string? token, JsonObject? body, CancellationToken cancel = default)
    {
        if (Server.Length == 0) throw new SquadException("No squad service is set. Add its address in the squad settings.");
        using var request = new HttpRequestMessage(method, Server + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancel);
        }
        catch (HttpRequestException e)
        {
            throw new SquadException($"Couldn't reach the squad service: {e.Message}");
        }
        using (response)
        {
            await ThrowIfFailed(response, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
    }

    private static async Task ThrowIfFailed(HttpResponseMessage response, CancellationToken cancel)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            message = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancel))?["error"]?.GetValue<string>();
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
        }
        throw new SquadException(message ?? $"The squad service answered {(int)response.StatusCode}.", response.StatusCode);
    }
}
