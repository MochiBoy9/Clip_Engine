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

    public static readonly PlanInfo Ultra = new(Plan.Ultra, "ultra", "Ultra", 4.99m, 250, 2160,
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
    public const string ProCheckoutUrl = "https://buy.polar.sh/polar_cl_1Od5CaTH339XapxgyZW8RjFstHRfWZbdkvncB4J3E2h";
    public const string UltraCheckoutUrl = "https://buy.polar.sh/polar_cl_Bjr9r9ZyxK5Hca0xqYuvaTtzfJynMI1NepwwG4CrrUp";
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

    public static int Effective(int choice, Plan plan) => Effective(choice, Plans.For(plan).Lines);

    // cap: the most the plan allows, or what a special key sets.
    public static int Effective(int choice, int cap) => choice <= 0 ? cap : Math.Min(choice, cap);

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

    // What a special key from the key maker changes: the highest resolution, the colour themes (null = the
    // plan's) and extra cloud storage in bytes.
    public bool IsSpecial { get; set; }
    public int? Lines { get; set; }
    public List<string>? Themes { get; set; }
    public long ExtraStorage { get; set; }

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
            IsSpecial = IsSpecial,
            Lines = Lines,
            Themes = Themes,
            ExtraStorage = ExtraStorage,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(stored, PlanJson.Default.LicenseState));
    }

    public void Clear()
    {
        Key = Display = "";
        Plan = Plan.Free;
        Expires = LastChecked = LastOk = null;
        IsSpecial = false;
        Lines = null;
        Themes = null;
        ExtraStorage = 0;
    }
}

internal static class LicenseRules
{
    // How long a paid plan keeps working without reaching the service, so playing offline never loses it.
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);

    // How often the key is checked again while the app runs, so a cancelled subscription ends.
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(12);

    public static Plan Effective(LicenseState state, DateTimeOffset now) => IsActive(state, now) ? state.Plan : Plan.Free;

    // Whether the key gives anything now: checked, not run out, and not offline for too long.
    public static bool IsActive(LicenseState state, DateTimeOffset now)
    {
        if (!state.HasKey || state.LastOk is not { } ok) return false;
        if (state.Expires is { } expires && expires <= now) return false;
        return now - ok <= OfflineGrace;
    }

    public static bool NeedsCheck(LicenseState state, DateTimeOffset now) =>
        state.HasKey && (state.LastChecked is not { } at || now - at >= RecheckEvery);
}

internal enum LicenseOutcome { Valid, Rejected, Offline }

internal sealed record LicenseCheck(LicenseOutcome Outcome, string Message, Plan Plan = Plan.Free, string Display = "",
                                    DateTimeOffset? Expires = null, bool IsSpecial = false, int? Lines = null,
                                    IReadOnlyList<string>? Themes = null, long ExtraStorage = 0);

internal sealed record Credits(IReadOnlyList<string> Ultra, IReadOnlyList<string> Pro);

// Checks keys with Clip Engine's online service, which asks Polar or, for a special key ("CEK-..."), its own list
// (server/src/index.js, POST /v1/license).
internal sealed class LicenseClient(string server, HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http = http ?? Shared;
    private readonly string _server = server.TrimEnd('/');

    // credit: a name to list in the credits, "" to take it off, null to leave it as it is. device: this PC's id,
    // which special keys count against how many PCs they allow.
    public async Task<LicenseCheck> CheckAsync(string key, string? credit, string device = "", CancellationToken cancel = default)
    {
        if (_server.Length == 0) return new(LicenseOutcome.Offline, "No online service is set. Add its address in Settings > Online.");
        var body = new JsonObject { ["key"] = key.Trim(), ["device"] = device };
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
            var special = reply?["grant"] is JsonValue g && g.TryGetValue<bool>(out var isGrant) && isGrant;
            if (plan == Plan.Free && !special) return new(LicenseOutcome.Rejected, "This key isn't for a Clip Engine plan.");
            DateTimeOffset? expires = reply?["expires"] is JsonValue e && e.TryGetValue<long>(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            if (!special) return new(LicenseOutcome.Valid, "", plan, reply?["display"]?.GetValue<string>() ?? "", expires);
            int? lines = reply?["lines"] is JsonValue l && l.TryGetValue<int>(out var n) && n != Plans.For(plan).Lines ? n : null;
            var themes = reply?["themes"] is JsonArray t
                ? t.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
                : null;
            var extra = reply?["extraStorage"] is JsonValue x && x.TryGetValue<long>(out var bytes) ? bytes : 0;
            return new(LicenseOutcome.Valid, "", plan, reply?["display"]?.GetValue<string>() ?? "", expires, true, lines, themes, extra);
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
