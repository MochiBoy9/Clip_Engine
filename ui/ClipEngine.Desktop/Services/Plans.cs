using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

public enum Plan { Free, Pro, Ultra }

// What each plan gets. The service has the same table (server/src/plans.js) and is what sets the cloud storage.
internal sealed record PlanInfo(Plan Plan, string Id, string Label, decimal Price, int StorageGb, int Lines, IReadOnlyList<string> Perks)
{
    public string PriceText => Price == 0 ? "Free" : string.Create(CultureInfo.InvariantCulture, $"${Price:0.00}/mo");
    public string LinesText => Resolutions.Name(Lines);
}

internal static class Plans
{
    public static readonly PlanInfo Free = new(Plan.Free, "free", "Free", 0, 2, 1080,
        ["Every recording, detection and squad feature", "Records up to 1080p", "2 GB cloud library"]);

    public static readonly PlanInfo Pro = new(Plan.Pro, "pro", "Pro", 2.99m, 100, 1440,
        ["Records up to 1440p", "100 GB cloud library", "All seven colour themes", "Pro badge and your name in the credits"]);

    public static readonly PlanInfo Ultra = new(Plan.Ultra, "ultra", "Ultra", 9.99m, 250, 2160,
        ["Everything in Pro", "Records up to 4K", "250 GB cloud library", "Three Ultra-only colour themes",
         "Ultra badge and top billing in the credits"]);

    public static IReadOnlyList<PlanInfo> All { get; } = [Free, Pro, Ultra];

    public static PlanInfo For(Plan plan) => plan switch { Plan.Ultra => Ultra, Plan.Pro => Pro, _ => Free };

    public static Plan FromId(string? id) => id switch { "ultra" => Plan.Ultra, "pro" or "plus" => Plan.Pro, _ => Plan.Free };
}

// Where subscriptions are bought and managed: Polar checkout links for each product (Products > Checkout links in
// the Polar dashboard) and its customer portal. While a link is empty, its button says it's coming soon.
internal static class PlanConfig
{
    public const string ProCheckoutUrl = "";
    public const string UltraCheckoutUrl = "";
    public const string ManageUrl = "";

    public static string CheckoutUrl(Plan plan) => plan switch
    {
        Plan.Pro => ProCheckoutUrl,
        Plan.Ultra => UltraCheckoutUrl,
        _ => "",
    };
}

// The highest resolution to record at. 0 is the most the plan allows.
internal static class Resolutions
{
    public static readonly IReadOnlyList<int> Choices = [0, 2160, 1440, 1080, 720, 480];

    public static int Effective(int choice, Plan plan)
    {
        var cap = Plans.For(plan).Lines;
        return choice <= 0 ? cap : Math.Min(choice, cap);
    }

    public static Plan Needs(int lines) => lines > Plans.Pro.Lines ? Plan.Ultra : lines > Plans.Free.Lines ? Plan.Pro : Plan.Free;

    public static string Name(int lines) => lines switch
    {
        2160 => "4K",
        <= 0 => "Native",
        _ => string.Create(CultureInfo.InvariantCulture, $"{lines}p"),
    };
}

// The license key on this PC, in %LOCALAPPDATA%\Clip_Engine\license.json, with the key encrypted.
internal sealed class LicenseState
{
    public string Key { get; set; } = "";
    public string Display { get; set; } = "";
    public Plan Plan { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public DateTimeOffset? LastOk { get; set; }

    // The name to show in the credits, and whether to.
    public string CreditName { get; set; } = "";
    public bool InCredits { get; set; } = true;

    [JsonIgnore] public bool HasKey => Key.Length > 0;

    public static LicenseState Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                var state = JsonSerializer.Deserialize(File.ReadAllText(file), PlanJson.Default.LicenseState) ?? new();
                if (state.HasKey) state.Key = Online.Unprotect(state.Key) ?? "";
                return state;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public void Save(string file)
    {
        var stored = new LicenseState
        {
            Key = HasKey ? Online.Protect(Key) : "",
            Display = Display,
            Plan = Plan,
            Expires = Expires,
            LastChecked = LastChecked,
            LastOk = LastOk,
            CreditName = CreditName,
            InCredits = InCredits,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(stored, PlanJson.Default.LicenseState));
    }

    public void Clear()
    {
        Key = Display = "";
        Plan = Plan.Free;
        Expires = LastChecked = LastOk = null;
    }
}

internal static class LicenseRules
{
    // How long a paid plan keeps working without reaching the service, so playing offline never loses it.
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);

    // How often the key is checked again while the app runs, so a cancelled subscription ends.
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(12);

    public static Plan Effective(LicenseState state, DateTimeOffset now)
    {
        if (!state.HasKey || state.LastOk is not { } ok) return Plan.Free;
        if (state.Expires is { } expires && expires <= now) return Plan.Free;
        if (now - ok > OfflineGrace) return Plan.Free;
        return state.Plan;
    }

    public static bool NeedsCheck(LicenseState state, DateTimeOffset now) =>
        state.HasKey && (state.LastChecked is not { } at || now - at >= RecheckEvery);
}

internal enum LicenseOutcome { Valid, Rejected, Offline }

internal sealed record LicenseCheck(LicenseOutcome Outcome, string Message, Plan Plan = Plan.Free, string Display = "",
                                    DateTimeOffset? Expires = null);

internal sealed record Credits(IReadOnlyList<string> Ultra, IReadOnlyList<string> Pro);

// Checks keys with Clip Engine's online service, which asks Polar (server/src/index.js, POST /v1/license).
internal sealed class LicenseClient(string server, HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http = http ?? Shared;
    private readonly string _server = server.TrimEnd('/');

    // credit: a name to list in the credits, "" to take it off, null to leave it as it is.
    public async Task<LicenseCheck> CheckAsync(string key, string? credit, CancellationToken cancel = default)
    {
        if (_server.Length == 0) return new(LicenseOutcome.Offline, "No online service is set. Add its address in Settings > Online.");
        var body = new JsonObject { ["key"] = key.Trim() };
        if (credit is not null) body["credit"] = credit;
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{_server}/v1/license", content, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            var reply = text.Length > 0 ? JsonNode.Parse(text) : null;
            if (!response.IsSuccessStatusCode)
            {
                var message = reply?["error"]?.GetValue<string>() ?? $"The online service answered {(int)response.StatusCode}.";
                var rejected = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.BadRequest;
                return new(rejected ? LicenseOutcome.Rejected : LicenseOutcome.Offline, message);
            }
            var plan = Plans.FromId(reply?["plan"]?.GetValue<string>());
            if (plan == Plan.Free) return new(LicenseOutcome.Rejected, "This key isn't for a Clip Engine plan.");
            DateTimeOffset? expires = reply?["expires"] is JsonValue e && e.TryGetValue<long>(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            return new(LicenseOutcome.Valid, "", plan, reply?["display"]?.GetValue<string>() ?? "", expires);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new(LicenseOutcome.Offline, "Couldn't reach Clip Engine's online service to check the key.");
        }
    }

    public async Task<Credits> CreditsAsync(CancellationToken cancel = default)
    {
        try
        {
            var reply = JsonNode.Parse(await _http.GetStringAsync($"{_server}/v1/credits", cancel));
            static IReadOnlyList<string> Names(JsonNode? n) =>
                (n?.AsArray() ?? []).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
            return new(Names(reply?["ultra"]), Names(reply?["pro"]));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new([], []);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LicenseState))]
internal sealed partial class PlanJson : JsonSerializerContext;
