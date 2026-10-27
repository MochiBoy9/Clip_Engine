using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

public enum Plan { Free, Pro, Founder }

// Where licenses are sold and checked. Keys are sold through Polar (polar.sh), which is the merchant of record:
// it takes the payment, handles sales tax and VAT, emails the key and pays out to the bank account on file.
// Fill these in from the Polar dashboard; while OrganizationId is empty the app shows purchases as not open yet.
internal static class LicenseConfig
{
    // Settings > General in the Polar dashboard.
    public const string OrganizationId = "";

    // The license key benefit attached to each product (Products > Benefits).
    public const string ProBenefitId = "";
    public const string FounderBenefitId = "";

    public const string PricingUrl = "https://clipengine.app/#pricing";
    public const string ManageUrl = "https://polar.sh/clipengine/portal";

    public const string ApiBase = "https://api.polar.sh/v1/customer-portal/license-keys/";

    public static bool IsConfigured => OrganizationId.Length > 0;
}

// The license saved on this PC, in %LOCALAPPDATA%\Clip_Engine\license.json.
internal sealed class LicenseState
{
    public string Key { get; set; } = "";
    public string DisplayKey { get; set; } = "";
    public string ActivationId { get; set; } = "";
    public Plan Plan { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? LastChecked { get; set; }

    public bool HasKey => Key.Length > 0;

    public static LicenseState Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(file), LicenseJson.Default.LicenseState) ?? new();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(this, LicenseJson.Default.LicenseState));
    }

    public void Clear()
    {
        Key = DisplayKey = ActivationId = "";
        Plan = Plan.Free;
        ExpiresAt = LastChecked = null;
    }
}

internal static class LicenseRules
{
    // How long a paid plan keeps working without reaching Polar, so playing offline never loses it.
    public static readonly TimeSpan OfflineGrace = TimeSpan.FromDays(14);

    // How often the key is checked again while the app runs.
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromHours(12);

    public static Plan Effective(LicenseState state, DateTimeOffset now)
    {
        if (!state.HasKey || state.LastChecked is not { } checkedAt) return Plan.Free;
        if (state.ExpiresAt is { } expires && expires <= now) return Plan.Free;
        if (now - checkedAt > OfflineGrace) return Plan.Free;
        return state.Plan;
    }

    public static Plan? PlanFor(string benefitId) =>
        benefitId.Length == 0 ? null
        : benefitId == LicenseConfig.FounderBenefitId ? Plan.Founder
        : benefitId == LicenseConfig.ProBenefitId ? Plan.Pro
        : null;
}

internal enum LicenseOutcome { Valid, Rejected, Offline }

internal sealed record LicenseCheck(LicenseOutcome Outcome, string Message, LicenseKeyInfo? Info = null, string? ActivationId = null,
                                    Plan Plan = Plan.Free);

// Talks to Polar's customer portal license key API, which needs no secret: only the key and the organization id.
internal sealed class PolarLicenseClient(HttpClient http, string organizationId, Func<string, Plan?> planFor)
{
    public PolarLicenseClient() : this(new HttpClient { BaseAddress = new Uri(LicenseConfig.ApiBase), Timeout = TimeSpan.FromSeconds(15) },
                                       LicenseConfig.OrganizationId, LicenseRules.PlanFor)
    {
    }

    // Checks a key someone typed in and, if its product limits devices, uses up one activation for this PC.
    public async Task<LicenseCheck> ActivateAsync(string key, string deviceLabel, CancellationToken cancel = default)
    {
        var activate = await PostAsync("activate", new ActivateRequest(key, organizationId, deviceLabel), cancel);
        if (activate.Error is { } error)
        {
            // Polar refuses to activate keys that have no device limit; those are only validated.
            if (error.Outcome != LicenseOutcome.Rejected) return error;
            var unlimited = await ValidateAsync(key, null, cancel);
            return unlimited.Outcome == LicenseOutcome.Valid && unlimited.Info!.LimitActivations is null ? unlimited : error;
        }
        var activation = activate.Body!.Value.Deserialize(LicenseJson.Default.ActivationInfo);
        if (activation is null) return new(LicenseOutcome.Offline, "Polar sent an answer the app couldn't read.");
        return await ValidateAsync(key, activation.Id, cancel);
    }

    public async Task<LicenseCheck> ValidateAsync(string key, string? activationId, CancellationToken cancel = default)
    {
        var result = await PostAsync("validate", new ValidateRequest(key, organizationId, activationId), cancel);
        if (result.Error is { } error) return error;

        var info = result.Body!.Value.Deserialize(LicenseJson.Default.LicenseKeyInfo);
        if (info is null) return new(LicenseOutcome.Offline, "Polar sent an answer the app couldn't read.");
        if (info.Status != "granted")
        {
            return new(LicenseOutcome.Rejected, info.Status == "revoked"
                ? "This key was revoked, usually because the subscription ended or the purchase was refunded."
                : "This key is turned off. Contact support if that's a mistake.");
        }
        if (planFor(info.BenefitId) is not { } plan) return new(LicenseOutcome.Rejected, "This key isn't for Clip Engine.");
        return new(LicenseOutcome.Valid, "", info, activationId ?? info.Activation?.Id, plan);
    }

    // Frees this PC's activation so the key can be used on another one.
    public async Task<bool> DeactivateAsync(string key, string activationId, CancellationToken cancel = default)
    {
        var result = await PostAsync("deactivate", new DeactivateRequest(key, organizationId, activationId), cancel);
        return result.Error is null;
    }

    private async Task<(JsonElement? Body, LicenseCheck? Error)> PostAsync<T>(string path, T request, CancellationToken cancel)
    {
        try
        {
            using var response = await http.PostAsync(path, JsonContent.Create(request, LicenseJson.Default.GetTypeInfo(typeof(T))!), cancel);
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent) return (default(JsonElement), null);
                return (await response.Content.ReadFromJsonAsync(LicenseJson.Default.JsonElement, cancel), null);
            }

            var detail = await ReadDetailAsync(response, cancel);
            return (null, response.StatusCode switch
            {
                HttpStatusCode.NotFound => new(LicenseOutcome.Rejected, "That key wasn't found. Check it matches the one in your receipt email."),
                HttpStatusCode.Forbidden when detail.Contains("limit", StringComparison.OrdinalIgnoreCase) =>
                    new(LicenseOutcome.Rejected, "This key is already active on its maximum number of PCs. Deactivate it on another PC first."),
                HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                    new(LicenseOutcome.Rejected, detail.Length > 0 ? detail : "Polar didn't accept this key."),
                _ => new(LicenseOutcome.Offline, $"Couldn't check the license right now ({(int)response.StatusCode})."),
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return (null, new(LicenseOutcome.Offline, "Couldn't reach the license server. Check your connection."));
        }
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync(LicenseJson.Default.JsonElement, cancel);
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String) return detail.GetString() ?? "";
                if (detail.ValueKind == JsonValueKind.Array && detail.GetArrayLength() > 0 &&
                    detail[0].TryGetProperty("msg", out var msg))
                {
                    return msg.GetString() ?? "";
                }
            }
        }
        catch (JsonException)
        {
        }
        return "";
    }
}

internal sealed record ValidateRequest(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("activation_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ActivationId);

internal sealed record ActivateRequest(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("label")] string Label);

internal sealed record DeactivateRequest(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("activation_id")] string ActivationId);

internal sealed class LicenseKeyInfo
{
    [JsonPropertyName("benefit_id")] public string BenefitId { get; set; } = "";
    [JsonPropertyName("display_key")] public string DisplayKey { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("limit_activations")] public int? LimitActivations { get; set; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("activation")] public ActivationInfo? Activation { get; set; }
}

internal sealed class ActivationInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LicenseState))]
[JsonSerializable(typeof(LicenseKeyInfo))]
[JsonSerializable(typeof(ActivationInfo))]
[JsonSerializable(typeof(ValidateRequest))]
[JsonSerializable(typeof(ActivateRequest))]
[JsonSerializable(typeof(DeactivateRequest))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class LicenseJson : JsonSerializerContext;
