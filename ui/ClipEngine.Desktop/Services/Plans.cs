using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

public enum Plan { Free, Pro, Ultra }

// What each plan gets. Every plan records at any resolution up to 4K; a plan sets the cloud library: its storage, the
// highest resolution clips go up at (CloudLines), the biggest clip it takes (ClipMb) and the longest (ClipSeconds). The
// service has the same table (server/src/plans.js) and is what enforces it. Clips on the PC can be any length.
internal sealed record PlanInfo(Plan Plan, string Id, string Label, decimal Price, int StorageGb, int CloudLines, int ClipMb,
                                int ClipSeconds, IReadOnlyList<string> Perks)
{
    public string PriceText => Price == 0 ? "Free" : string.Create(CultureInfo.InvariantCulture, $"${Price:0.00}/mo");
}

internal static class Plans
{
    public static readonly PlanInfo Free = new(Plan.Free, "free", "Free", 0, 2, 1080, 100, 40,
        ["Every recording, detection and squad feature", "Records up to 4K", "2 GB cloud library: clips up to 1080p, 100 MB and 40 seconds"]);

    public static readonly PlanInfo Pro = new(Plan.Pro, "pro", "Pro", 2.99m, 100, 2160, 1024, 600,
        ["4K clips in the cloud, up to 1 GB and 10 minutes each", "100 GB cloud library", "Thirteen colour themes", "Pro badge and your name in the credits"]);

    public static readonly PlanInfo Ultra = new(Plan.Ultra, "ultra", "Ultra", 4.99m, 250, 2160, 4096, 1800,
        ["Everything in Pro", "4K clips in the cloud, up to 4 GB and 30 minutes each", "250 GB cloud library",
         "Every colour: three more themes and a colour wheel", "Adaptive themes with gradients and motion",
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
    public const string ManageUrl = "https://polar.sh/clip-engine/portal";

    // The Donation product's checkout link: one-time, pay what you want. Empty: the donate buttons say it's coming soon.
    public const string DonateUrl = "";

    public static string CheckoutUrl(Plan plan) => plan switch
    {
        Plan.Pro => ProCheckoutUrl,
        Plan.Ultra => UltraCheckoutUrl,
        _ => "",
    };

    // The donation checkout with an amount filled in (Polar's amount is in cents); 0 leaves it to the donor.
    public static string DonateLink(decimal dollars) => DonateUrl.Length == 0 ? ""
        : dollars <= 0 ? DonateUrl
        : DonateUrl + (DonateUrl.Contains('?') ? "&" : "?") + string.Create(CultureInfo.InvariantCulture, $"amount={(long)(dollars * 100)}");
}

// What donating gives (server/src/plans.js, DONATION_DEFAULTS): the Supporter badge, a little more cloud storage for
// each dollar given, up to a cap, and from $5 in all, a name in the credits.
internal static class Donations
{
    public const int GbPerDollar = 1;
    public const int MaxGb = 25;
    public const decimal CreditsFrom = 5m;

    // The amounts the plan card offers; any other amount can be typed on Polar's page.
    public static IReadOnlyList<decimal> Amounts { get; } = [3m, 5m, 10m, 25m];

    public static IReadOnlyList<string> Perks { get; } =
    [
        "A Supporter badge",
        $"{GbPerDollar} GB more cloud storage for every dollar, up to {MaxGb} GB, for good",
        $"Your name in the credits from ${CreditsFrom:0} in all",
    ];

    public static string Money(long cents) => cents % 100 == 0
        ? string.Create(CultureInfo.InvariantCulture, $"${cents / 100}")
        : string.Create(CultureInfo.InvariantCulture, $"${cents / 100m:0.00}");
}

// The highest resolution to record at. Every plan records up to 4K; 0 is the most there is, 4K or the monitor's own
// size if that's smaller.
internal static class Resolutions
{
    public const int Max = 2160;

    public static readonly IReadOnlyList<int> Choices = [0, 2160, 1440, 1080, 720, 480];

    public static int Effective(int choice) => choice <= 0 ? Max : Math.Min(choice, Max);

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

    // When a subscription next renews, or ends when it was cancelled (Ending), as Polar said at the last check. Only the
    // date is kept, for the notice a few days before.
    public DateTimeOffset? Renews { get; set; }
    public bool Ending { get; set; }
    public DateTimeOffset? LastOk { get; set; }

    // The name to show in the credits, and whether to.
    public string CreditName { get; set; } = "";
    public bool InCredits { get; set; } = true;

    // What a special key from the key maker changes: the highest resolution clips go up to the cloud at, the colour
    // themes (null = the plan's) and extra cloud storage in bytes.
    public bool IsSpecial { get; set; }
    public int? Lines { get; set; }
    public List<string>? Themes { get; set; }
    public long ExtraStorage { get; set; }

    // A special key's title (Content Creator, Special, Exclusive), its perks (a direct line to the developer, bug
    // reports), whether its name may go in the credits, and why it was given, when the developer shares that.
    public string? Role { get; set; }
    public string? RoleLabel { get; set; }
    public List<string>? Perks { get; set; }
    public bool CreditsAllowed { get; set; }
    public string Reason { get; set; } = "";

    // Whether whoever owns the key donated (on its own, a donation's key is a Free plan with the Supporter role), and
    // how much in all, in cents, when the service could tell.
    public bool Supporter { get; set; }
    public long? Donated { get; set; }

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
            Renews = Renews,
            Ending = Ending,
            LastOk = LastOk,
            CreditName = CreditName,
            InCredits = InCredits,
            IsSpecial = IsSpecial,
            Lines = Lines,
            Themes = Themes,
            ExtraStorage = ExtraStorage,
            Role = Role,
            RoleLabel = RoleLabel,
            Perks = Perks,
            CreditsAllowed = CreditsAllowed,
            Reason = Reason,
            Supporter = Supporter,
            Donated = Donated,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(stored, PlanJson.Default.LicenseState));
    }

    public void Clear()
    {
        Key = Display = "";
        Plan = Plan.Free;
        Expires = LastChecked = LastOk = Renews = null;
        Ending = false;
        IsSpecial = false;
        Lines = null;
        Themes = null;
        ExtraStorage = 0;
        Role = RoleLabel = null;
        Perks = null;
        CreditsAllowed = false;
        Reason = "";
        Supporter = false;
        Donated = null;
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
                                    IReadOnlyList<string>? Themes = null, long ExtraStorage = 0, string? Role = null,
                                    string? RoleLabel = null, IReadOnlyList<string>? Perks = null, bool CreditsAllowed = false,
                                    string Reason = "", bool Supporter = false, long? Donated = null,
                                    DateTimeOffset? Renews = null, bool Ending = false);

// What a special key can let its holder do besides the plan (server/src/plans.js, PERKS).
internal static class PerkIds
{
    public const string DirectLine = "contact";
    public const string BugReports = "bugs";
}

// The names in the credits: subscribers, supporters who donated $5 or more, then people the developer thanked with a
// special role.
internal sealed record Credits(IReadOnlyList<string> Ultra, IReadOnlyList<string> Pro)
{
    public IReadOnlyList<string> Supporter { get; init; } = [];
    public IReadOnlyList<string> Exclusive { get; init; } = [];
    public IReadOnlyList<string> Creator { get; init; } = [];
    public IReadOnlyList<string> Special { get; init; } = [];
}

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
            var supporter = reply?["supporter"] is JsonValue sv && sv.TryGetValue<bool>(out var isSupporter) && isSupporter;
            if (plan == Plan.Free && !special && !supporter) return new(LicenseOutcome.Rejected, "This key isn't for a Clip Engine plan.");
            DateTimeOffset? expires = reply?["expires"] is JsonValue e && e.TryGetValue<long>(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            // A subscription's next renewal, or its end once it's cancelled (server/src/plans.js, subscriptionRenewal).
            DateTimeOffset? renews = reply?["renews"] is JsonValue r && r.TryGetValue<long>(out var renewMs)
                ? DateTimeOffset.FromUnixTimeMilliseconds(renewMs)
                : null;
            var ending = reply?["ending"] is JsonValue en && en.TryGetValue<bool>(out var isEnding) && isEnding;
            long? donated = reply?["donated"] is JsonValue dv && dv.TryGetValue<long>(out var cents) ? cents : null;
            var credited = reply?["credits"] is JsonValue c && c.TryGetValue<bool>(out var credited0) && credited0;
            var extra = reply?["extraStorage"] is JsonValue xs && xs.TryGetValue<long>(out var bytes) ? bytes : 0;
            // A subscription or a donation (Polar): the plan, and a donor's storage bonus, role and credits.
            if (!special && supporter)
            {
                return new LicenseCheck(LicenseOutcome.Valid, "", plan, reply?["display"]?.GetValue<string>() ?? "", expires, false, null, null,
                                        extra, Text(reply?["role"]), Text(reply?["roleLabel"]), [], credited, "", true, donated,
                                        renews, ending);
            }
            if (!special)
            {
                return new(LicenseOutcome.Valid, "", plan, reply?["display"]?.GetValue<string>() ?? "", expires, CreditsAllowed: credited,
                           Renews: renews, Ending: ending);
            }
            int? lines = reply?["lines"] is JsonValue l && l.TryGetValue<int>(out var n) && n != Plans.For(plan).CloudLines ? n : null;
            var themes = reply?["themes"] is JsonArray t
                ? t.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
                : null;
            var perks = reply?["perks"] is JsonArray p
                ? p.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(x => x.Length > 0).ToList()
                : [];
            return new(LicenseOutcome.Valid, "", plan, reply?["display"]?.GetValue<string>() ?? "", expires, true, lines, themes, extra,
                       Text(reply?["role"]), Text(reply?["roleLabel"]), perks, credited, Text(reply?["reason"]) ?? "", supporter, donated);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new(LicenseOutcome.Offline, "Couldn't reach Clip Engine's online service to check the key.");
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public async Task<Credits> CreditsAsync(CancellationToken cancel = default)
    {
        try
        {
            var reply = JsonNode.Parse(await _http.GetStringAsync($"{_server}/v1/credits", cancel));
            static IReadOnlyList<string> Names(JsonNode? n) =>
                (n as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
            return new(Names(reply?["ultra"]), Names(reply?["pro"]))
            {
                Supporter = Names(reply?["supporter"]),
                Exclusive = Names(reply?["exclusive"]),
                Creator = Names(reply?["creator"]),
                Special = Names(reply?["special"]),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new([], []);
        }
    }
}

// A message or bug report a special key's holder sent the developer, with the developer's answer.
internal sealed record ContactMessage(long Id, string Kind, string Text, DateTimeOffset Sent, string? Reply, DateTimeOffset? Replied)
{
    public static ContactMessage? From(JsonNode? n)
    {
        if (n is not JsonObject) return null;
        static DateTimeOffset? Time(JsonNode? t) =>
            t is JsonValue v && v.TryGetValue<long>(out var ms) && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        return new(n["id"] is JsonValue i && i.TryGetValue<long>(out var id) ? id : 0,
                   n["kind"]?.GetValue<string>() ?? "message",
                   n["text"]?.GetValue<string>() ?? "",
                   Time(n["created"]) ?? DateTimeOffset.UnixEpoch,
                   n["reply"] is JsonValue r && r.TryGetValue<string>(out var reply) && reply.Length > 0 ? reply : null,
                   Time(n["replied"]));
    }
}

internal sealed record ContactResult(bool Ok, string Message, IReadOnlyList<ContactMessage> Messages);

// A special key's direct line to the developer and its bug reports (server/src/index.js, POST /v1/contact). The key
// and this PC's id are what let a message through; the developer answers in the key maker.
internal sealed class ContactClient(string server, HttpClient? http = null)
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HttpClient _http = http ?? Shared;
    private readonly string _server = server.TrimEnd('/');

    public const string Message = "message";
    public const string Bug = "bug";

    public Task<ContactResult> SendAsync(string key, string device, string kind, string text, string name, CancellationToken cancel = default) =>
        PostAsync("/v1/contact", new JsonObject { ["key"] = key, ["device"] = device, ["kind"] = kind, ["text"] = text, ["name"] = name }, cancel);

    public Task<ContactResult> ListAsync(string key, string device, CancellationToken cancel = default) =>
        PostAsync("/v1/contact/list", new JsonObject { ["key"] = key, ["device"] = device }, cancel);

    private async Task<ContactResult> PostAsync(string path, JsonObject body, CancellationToken cancel)
    {
        if (_server.Length == 0) return new(false, "No online service is set. Add its address in Settings > Online.", []);
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_server + path, content, cancel);
            var text = await response.Content.ReadAsStringAsync(cancel);
            var reply = text.Length > 0 ? JsonNode.Parse(text) : null;
            if (!response.IsSuccessStatusCode)
                return new(false, reply?["error"]?.GetValue<string>() ?? $"The online service answered {(int)response.StatusCode}.", []);
            var messages = (reply?["messages"] as JsonArray ?? []).Select(ContactMessage.From).OfType<ContactMessage>().ToList();
            return new(true, "", messages);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new(false, "Couldn't reach Clip Engine's online service. Try again in a minute.", []);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LicenseState))]
internal sealed partial class PlanJson : JsonSerializerContext;
