using System.Net;
using System.Text;
using System.Text.Json;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class LicensingTests : IDisposable
{
    private const string ProBenefit = "benefit-pro";
    private const string FounderBenefit = "benefit-founder";

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"clip_engine_license_{Guid.NewGuid():N}.json");
    private readonly DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void PaidPlanLastsThroughTheOfflineGraceOnly()
    {
        var state = new LicenseState { Key = "KEY", Plan = Plan.Pro, LastChecked = _now.AddDays(-13) };
        Assert.Equal(Plan.Pro, LicenseRules.Effective(state, _now));

        state.LastChecked = _now.AddDays(-15);
        Assert.Equal(Plan.Free, LicenseRules.Effective(state, _now));
    }

    [Fact]
    public void ExpiredOrMissingKeyIsFree()
    {
        Assert.Equal(Plan.Free, LicenseRules.Effective(new LicenseState { Plan = Plan.Pro, LastChecked = _now }, _now));

        var expired = new LicenseState { Key = "KEY", Plan = Plan.Pro, LastChecked = _now, ExpiresAt = _now.AddMinutes(-1) };
        Assert.Equal(Plan.Free, LicenseRules.Effective(expired, _now));
    }

    [Fact]
    public void StateSurvivesASaveAndLoad()
    {
        var state = new LicenseState { Key = "KEY", DisplayKey = "****-KEY", ActivationId = "act", Plan = Plan.Founder, LastChecked = _now };
        state.Save(_file);

        var loaded = LicenseState.Load(_file);
        Assert.Equal(("KEY", "act", Plan.Founder, _now), (loaded.Key, loaded.ActivationId, loaded.Plan, loaded.LastChecked));
        Assert.Contains("\"Founder\"", File.ReadAllText(_file));
    }

    [Fact]
    public async Task ActivatesKeysWithADeviceLimit()
    {
        var polar = new FakePolar();
        polar.Respond("activate", HttpStatusCode.OK, """{"id":"act-1","license_key_id":"lk"}""");
        polar.Respond("validate", HttpStatusCode.OK, KeyJson(ProBenefit, limit: 3, activation: "act-1"));

        var check = await Client(polar).ActivateAsync("KEY", "GAMING-PC");

        Assert.Equal(LicenseOutcome.Valid, check.Outcome);
        Assert.Equal((Plan.Pro, "act-1"), (check.Plan, check.ActivationId));
        Assert.Equal("GAMING-PC", polar.Body("activate").GetProperty("label").GetString());
        Assert.Equal("org", polar.Body("validate").GetProperty("organization_id").GetString());
        Assert.Equal("act-1", polar.Body("validate").GetProperty("activation_id").GetString());
    }

    [Fact]
    public async Task KeysWithoutADeviceLimitAreOnlyValidated()
    {
        var polar = new FakePolar();
        polar.Respond("activate", HttpStatusCode.Forbidden, """{"error":"NotPermitted","detail":"This license key does not support activations."}""");
        polar.Respond("validate", HttpStatusCode.OK, KeyJson(FounderBenefit, limit: null));

        var check = await Client(polar).ActivateAsync("KEY", "PC");

        Assert.Equal((LicenseOutcome.Valid, Plan.Founder), (check.Outcome, check.Plan));
        Assert.False(polar.Body("validate").TryGetProperty("activation_id", out _));
    }

    [Fact]
    public async Task ExplainsWhyAKeyWasRefused()
    {
        var polar = new FakePolar();
        polar.Respond("activate", HttpStatusCode.Forbidden, """{"error":"NotPermitted","detail":"License key activation limit already reached"}""");
        polar.Respond("validate", HttpStatusCode.OK, KeyJson(ProBenefit, limit: 2));
        var full = await Client(polar).ActivateAsync("KEY", "PC");
        Assert.Equal(LicenseOutcome.Rejected, full.Outcome);
        Assert.Contains("maximum number of PCs", full.Message);

        polar = new FakePolar();
        polar.Respond("validate", HttpStatusCode.NotFound, """{"error":"ResourceNotFound","detail":"Not found"}""");
        Assert.Contains("wasn't found", (await Client(polar).ValidateAsync("NOPE", null)).Message);

        polar = new FakePolar();
        polar.Respond("validate", HttpStatusCode.OK, KeyJson(ProBenefit, limit: null, status: "revoked"));
        Assert.Contains("revoked", (await Client(polar).ValidateAsync("KEY", null)).Message);

        polar = new FakePolar();
        polar.Respond("validate", HttpStatusCode.OK, KeyJson("someone-elses-benefit", limit: null));
        Assert.Equal(LicenseOutcome.Rejected, (await Client(polar).ValidateAsync("KEY", null)).Outcome);
    }

    [Fact]
    public async Task ServerTroubleCountsAsOffline()
    {
        var polar = new FakePolar();
        polar.Respond("validate", HttpStatusCode.BadGateway, "");
        Assert.Equal(LicenseOutcome.Offline, (await Client(polar).ValidateAsync("KEY", null)).Outcome);

        polar = new FakePolar { Throw = true };
        Assert.Equal(LicenseOutcome.Offline, (await Client(polar).ValidateAsync("KEY", null)).Outcome);
    }

    [Fact]
    public async Task ActivatingSavesThePlanAndRemovingFreesIt()
    {
        var polar = new FakePolar();
        polar.Respond("activate", HttpStatusCode.OK, """{"id":"act-1"}""");
        polar.Respond("validate", HttpStatusCode.OK, KeyJson(ProBenefit, limit: 3, activation: "act-1"));
        polar.Respond("deactivate", HttpStatusCode.NoContent, "");
        var vm = new LicenseViewModel(new LicenseState(), _file, Client(polar), () => _now, purchasesOpen: true);
        Assert.Equal(Plan.Free, vm.Plan);

        vm.KeyInput = "  KEY  ";
        await vm.ActivateAsync();

        Assert.Equal(Plan.Pro, vm.Plan);
        Assert.Equal("", vm.KeyInput);
        Assert.False(vm.MessageIsError);
        Assert.Equal(("KEY", "act-1", Plan.Pro), (LicenseState.Load(_file).Key, LicenseState.Load(_file).ActivationId, LicenseState.Load(_file).Plan));

        await vm.DeactivateAsync();
        Assert.Equal(Plan.Free, vm.Plan);
        Assert.Equal("act-1", polar.Body("deactivate").GetProperty("activation_id").GetString());
        Assert.False(LicenseState.Load(_file).HasKey);
    }

    [Fact]
    public async Task ACancelledSubscriptionEndsOnTheNextCheckButOfflineDoesNot()
    {
        var state = new LicenseState { Key = "KEY", Plan = Plan.Pro, LastChecked = _now.AddDays(-1) };

        var offline = new FakePolar { Throw = true };
        var vm = new LicenseViewModel(state, _file, Client(offline), () => _now, purchasesOpen: true);
        await vm.RecheckAsync();
        Assert.Equal(Plan.Pro, vm.Plan);

        var polar = new FakePolar();
        polar.Respond("validate", HttpStatusCode.OK, KeyJson(ProBenefit, limit: null, status: "revoked"));
        vm = new LicenseViewModel(state, _file, Client(polar), () => _now, purchasesOpen: true);
        await vm.RecheckAsync();
        Assert.Equal(Plan.Free, vm.Plan);
        Assert.True(vm.HasKey);
        Assert.True(vm.MessageIsError);
    }

    private static PolarLicenseClient Client(FakePolar polar) =>
        new(new HttpClient(polar) { BaseAddress = new Uri("https://polar.test/v1/customer-portal/license-keys/") }, "org",
            id => id == ProBenefit ? Plan.Pro : id == FounderBenefit ? Plan.Founder : null);

    private static string KeyJson(string benefit, int? limit, string status = "granted", string? activation = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = "lk", ["organization_id"] = "org", ["benefit_id"] = benefit, ["key"] = "KEY", ["display_key"] = "****-KEY",
            ["status"] = status, ["limit_activations"] = limit, ["usage"] = 0, ["expires_at"] = null,
            ["activation"] = activation is null ? null : new Dictionary<string, object?> { ["id"] = activation },
        });

    private sealed class FakePolar : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _responses = [];
        private readonly Dictionary<string, JsonElement> _requests = [];

        public bool Throw { get; init; }

        public void Respond(string path, HttpStatusCode status, string body) => _responses[path] = (status, body);
        public JsonElement Body(string path) => _requests[path];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (Throw) throw new HttpRequestException("offline");
            var path = request.RequestUri!.Segments[^1];
            _requests[path] = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancel)).RootElement.Clone();
            var (status, body) = _responses.TryGetValue(path, out var r) ? r : (HttpStatusCode.NotFound, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
