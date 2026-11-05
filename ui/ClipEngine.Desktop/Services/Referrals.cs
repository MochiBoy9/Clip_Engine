using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ClipEngine.Desktop.Services;

// Inviting friends, kept in desktop.json: this PC's invite code, the secret that shows its friends (encrypted), and
// whether Clip Engine was new on this PC when it first started, which is what lets it count for a friend's invite.
internal sealed class ReferralSettings
{
    public string Code { get; set; } = "";
    public string Link { get; set; } = "";
    public string Secret { get; set; } = "";

    // When this version first started here, and whether nothing of Clip Engine was on the PC before it.
    public DateTimeOffset? FirstStart { get; set; }
    public bool FreshInstall { get; set; }

    // The service heard this PC start (so a link opened before installing is credited), and the invite it counted for.
    public bool Announced { get; set; }
    public string InvitedBy { get; set; } = "";
    public string InviteCode { get; set; } = "";

    // Reward keys already offered, so each one is turned on or announced once.
    public List<string> SeenRewards { get; set; } = [];

    // The friends counted at the last look, to say when another one joins.
    public int Friends { get; set; }
}

// This PC's id for invites: a hash of Windows' machine id, which stays when Clip Engine's own files are wiped, so
// reinstalling doesn't make a PC look new. Empty when it can't be read; the app's own id stands in.
internal static class MachineId
{
    private static string? _cached;

    public static string Get()
    {
        if (_cached is not null) return _cached;
        var guid = OperatingSystem.IsWindows() ? ReadWindows() : "";
        return _cached = guid.Length == 0 ? "" : Hash(guid);
    }

    internal static string Hash(string guid) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"clip-engine-invite:{guid.Trim().ToLowerInvariant()}"))).ToLowerInvariant();

    [SupportedOSPlatform("windows")]
    private static string ReadWindows()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string ?? "";
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return "";
        }
    }
}

internal sealed record ReferralStatus(string Code, string Link, int Friends, int NotCounted, int Needed, int ToGo, int RewardDays,
                                      IReadOnlyList<string> Rewards, bool Done, string Secret = "");

// What the service said about this PC's start: whether it counted for an invite, and why not.
internal sealed record ReferralActivation(bool Ok, bool Counted, string Reason, string Inviter, string Code, string Error = "");

// Talks to the online service's invite routes (server/src/referrals.js).
internal sealed class ReferralClient(string server, HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http = http ?? Shared;
    private readonly string _server = server.TrimEnd('/');

    public bool HasServer => _server.Length > 0;

    // This PC's invite code, made the first time. Null with a message when the service can't be reached.
    public async Task<(ReferralStatus? Status, string Error)> CreateAsync(string pc, string device, string name, CancellationToken cancel = default)
    {
        var body = new JsonObject { ["pc"] = pc, ["device"] = device, ["name"] = name };
        var (reply, error) = await SendAsync(HttpMethod.Post, "/v1/referrals", body, null, cancel);
        return reply is null ? (null, error) : (Parse(reply), "");
    }

    public async Task<(ReferralStatus? Status, string Error)> StatusAsync(string code, string secret, string pc, string device,
                                                                          CancellationToken cancel = default)
    {
        var path = $"/v1/referrals/{Uri.EscapeDataString(code)}?pc={Uri.EscapeDataString(pc)}&device={Uri.EscapeDataString(device)}";
        var (reply, error) = await SendAsync(HttpMethod.Get, path, null, secret, cancel);
        return reply is null ? (null, error) : (Parse(reply), "");
    }

    // Clip Engine started on this PC: fresh says it's a new install, code is a friend's invite code if one was typed.
    public async Task<ReferralActivation> ActivateAsync(string pc, string device, bool fresh, string? code, CancellationToken cancel = default)
    {
        var body = new JsonObject { ["pc"] = pc, ["device"] = device, ["fresh"] = fresh };
        if (!string.IsNullOrWhiteSpace(code)) body["code"] = code.Trim();
        var (reply, error) = await SendAsync(HttpMethod.Post, "/v1/referrals/activate", body, null, cancel);
        if (reply is null) return new(false, false, "", "", "", error);
        return new(true, Bool(reply["counted"]), Text(reply["reason"]), Text(reply["inviter"]), Text(reply["code"]));
    }

    private async Task<(JsonNode? Reply, string Error)> SendAsync(HttpMethod method, string path, JsonNode? body, string? token,
                                                                  CancellationToken cancel)
    {
        if (_server.Length == 0) return (null, "No online service is set. Add its address in Settings > Online.");
        try
        {
            using var request = new HttpRequestMessage(method, _server + path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            if (token is not null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            var reply = text.Length > 0 ? JsonNode.Parse(text) : null;
            if (response.IsSuccessStatusCode && reply is not null) return (reply, "");
            var message = reply?["error"] is JsonValue e && e.TryGetValue<string>(out var m) ? m : "";
            return (null, message.Length > 0 ? message
                : response.StatusCode == HttpStatusCode.TooManyRequests ? "Too many tries. Wait a minute and try again."
                : $"The online service answered {(int)response.StatusCode}.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return (null, "Couldn't reach Clip Engine's online service.");
        }
    }

    internal static ReferralStatus Parse(JsonNode reply) => new(
        Text(reply["code"]), Text(reply["link"]), Number(reply["friends"]), Number(reply["notCounted"]), Math.Max(1, Number(reply["needed"])),
        Number(reply["toGo"]), Number(reply["rewardDays"]),
        (reply["rewards"] as JsonArray ?? new JsonArray()).Select(r => Text(r?["key"])).Where(k => k.Length > 0).ToList(),
        Bool(reply["done"]), Text(reply["secret"]));

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static int Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
