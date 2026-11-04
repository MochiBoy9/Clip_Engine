using System.Net;
using System.Text;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class PlanTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class FakeServer(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel);
            Bodies.Add(body);
            var (status, text) = answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public void PlansCapTheResolution()
    {
        Assert.Equal(1080, Resolutions.Effective(0, Plan.Free));
        Assert.Equal(1440, Resolutions.Effective(0, Plan.Pro));
        Assert.Equal(2160, Resolutions.Effective(0, Plan.Ultra));
        Assert.Equal(1080, Resolutions.Effective(2160, Plan.Free));
        Assert.Equal(720, Resolutions.Effective(720, Plan.Ultra));
        Assert.Equal(1440, Resolutions.Effective(1440, Plan.Ultra));
        Assert.Equal(Plan.Ultra, Resolutions.Needs(2160));
        Assert.Equal(Plan.Pro, Resolutions.Needs(1440));
        Assert.Equal(Plan.Free, Resolutions.Needs(720));
        Assert.Equal("$2.99/mo", Plans.Pro.PriceText);
        Assert.Equal("$4.99/mo", Plans.Ultra.PriceText);
        Assert.Equal(Plan.Pro, Plans.FromId("plus"));

        var option = new ResolutionOption(1440);
        option.Refresh(Plans.Free.Lines);
        Assert.False(option.IsAvailable);
        Assert.Equal("1440p · Pro", option.Label);
        option.Refresh(Plans.Pro.Lines);
        Assert.True(option.IsAvailable);
        Assert.Equal("1440p", option.Label);
        var best = new ResolutionOption(0);
        best.Refresh(Plans.Ultra.Lines);
        Assert.Equal("Best for plan (4K)", best.Label);
    }

    [Fact]
    public async Task SpecialKeysBringTheirOwnFeatures()
    {
        var server = new FakeServer((_, body) => body.Contains("CEK-FREE")
            ? (HttpStatusCode.OK, """{"plan":"free","lines":1440,"themes":["ember","chrome"],"extraStorage":10737418240,"grant":true,"display":"CEK-FREE…AAAA","expires":4102444800000}""")
            : (HttpStatusCode.OK, """{"plan":"free","lines":1080,"themes":null,"extraStorage":0}"""));
        var client = new LicenseClient("https://service.test/", new HttpClient(server));

        var check = await client.CheckAsync("CEK-FREE-AAAA-AAAA", null, "pc-1");
        Assert.Equal(LicenseOutcome.Valid, check.Outcome);
        Assert.True(check.IsSpecial);
        Assert.Equal(Plan.Free, check.Plan);
        Assert.Equal(1440, check.Lines);
        Assert.Equal(["ember", "chrome"], check.Themes);
        Assert.Equal(10L << 30, check.ExtraStorage);
        Assert.Equal("pc-1", System.Text.Json.Nodes.JsonNode.Parse(server.Bodies[^1])!["device"]!.GetValue<string>());
        Assert.Equal(LicenseOutcome.Rejected, (await client.CheckAsync("PLAIN-FREE-KEY", null)).Outcome);

        var vm = new PlanViewModel(null, null, () => "", []);
        vm.Accept("CEK-FREE-AAAA-AAAA", check);
        Assert.Equal(Plan.Free, vm.Plan);
        Assert.True(vm.IsSpecial);
        Assert.False(vm.ShowKeyBox);
        Assert.Equal(1440, vm.MaxLines);
        Assert.Equal(10L << 30, vm.ExtraStorage);
        Assert.True(vm.IsUnlocked(AccentThemes.Find("chrome")), "a special key can give an Ultra theme");
        Assert.True(vm.IsUnlocked(AccentThemes.Default));
        Assert.False(vm.IsUnlocked(AccentThemes.Find("mint")));
        Assert.Contains("1440p recording, 10 GB more cloud storage and extra colour themes", vm.Detail);
        Assert.Contains("until", vm.Detail);

        // Pro from a special key with the plan's themes: the plan decides, with the key's resolution.
        vm.Accept("CEK-PRO", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Pro, "", null, true, 2160, null, 0));
        Assert.False(vm.CanBeCredited, "only paying supporters are offered the credits");
        Assert.False(vm.SaveCreditCommand.CanExecute(null));
        Assert.Equal(2160, vm.MaxLines);
        Assert.True(vm.IsUnlocked(AccentThemes.Find("mint")));
        Assert.False(vm.IsUnlocked(AccentThemes.Find("chrome")));
        Assert.Equal(1440, Resolutions.Effective(1440, vm.MaxLines));

        // A subscription key never carries a special key's features over.
        vm.Accept("POLAR", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Pro));
        Assert.False(vm.IsSpecial);
        Assert.True(vm.CanBeCredited);
        Assert.Equal(1440, vm.MaxLines);

        vm.Remove();
        Assert.Equal(1080, vm.MaxLines);
        Assert.True(vm.ShowKeyBox);
    }

    [Fact]
    public void ASpecialKeysFeaturesAreKept()
    {
        var file = Path.Combine(_dir, "license.json");
        new LicenseState { Key = "CEK-1", IsSpecial = true, Lines = 1440, Themes = ["arc"], ExtraStorage = 5, LastOk = DateTimeOffset.UtcNow }.Save(file);
        var loaded = LicenseState.Load(file);
        Assert.True(loaded.IsSpecial);
        Assert.Equal(1440, loaded.Lines);
        Assert.Equal(["arc"], loaded.Themes);
        Assert.Equal(5, loaded.ExtraStorage);
        Assert.True(LicenseRules.IsActive(loaded, DateTimeOffset.UtcNow));
        loaded.Clear();
        Assert.False(loaded.IsSpecial);
        Assert.Null(loaded.Themes);
    }

    [Fact]
    public void ThemesFollowThePlan()
    {
        var chrome = AccentThemes.Find("chrome");
        var ember = AccentThemes.Find("ember");
        Assert.True(AccentThemes.Default.IsUnlocked(Plan.Free));
        Assert.False(ember.IsUnlocked(Plan.Free));
        Assert.True(ember.IsUnlocked(Plan.Pro));
        Assert.False(chrome.IsUnlocked(Plan.Pro));
        Assert.True(chrome.IsUnlocked(Plan.Ultra));
        Assert.Equal(10, AccentThemes.All.Count);
    }

    [Fact]
    public void APaidPlanLastsTwoWeeksOffline()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new LicenseState { Key = "KEY", Plan = Plan.Pro, LastOk = now - TimeSpan.FromDays(3), LastChecked = now - TimeSpan.FromHours(13) };
        Assert.Equal(Plan.Pro, LicenseRules.Effective(state, now));
        Assert.True(LicenseRules.NeedsCheck(state, now));
        state.LastOk = now - TimeSpan.FromDays(15);
        Assert.Equal(Plan.Free, LicenseRules.Effective(state, now));
        state.LastOk = now;
        state.Expires = now - TimeSpan.FromMinutes(1);
        Assert.Equal(Plan.Free, LicenseRules.Effective(state, now));
        Assert.Equal(Plan.Free, LicenseRules.Effective(new LicenseState(), now));
    }

    [Fact]
    public void TheLicenseIsKeptOnThisPc()
    {
        var file = Path.Combine(_dir, "license.json");
        var state = new LicenseState { Key = "ABCD-1234", Display = "****-1234", Plan = Plan.Ultra, LastOk = DateTimeOffset.UtcNow, CreditName = "Zoë" };
        state.Save(file);
        var loaded = LicenseState.Load(file);
        Assert.Equal("ABCD-1234", loaded.Key);
        Assert.Equal(Plan.Ultra, loaded.Plan);
        Assert.Equal("Zoë", loaded.CreditName);
        Assert.Contains("\"Ultra\"", File.ReadAllText(file));
    }

    [Fact]
    public async Task AsksTheServiceAboutKeys()
    {
        var server = new FakeServer((request, body) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/credits") return (HttpStatusCode.OK, """{"ultra":["Mira"],"pro":["Kai"]}""");
            Assert.Equal("/v1/license", request.RequestUri.AbsolutePath);
            if (body.Contains("GOOD")) return (HttpStatusCode.OK, """{"plan":"ultra","display":"****-GOOD","expires":null}""");
            if (body.Contains("DOWN")) return (HttpStatusCode.ServiceUnavailable, """{"error":"Subscriptions aren't open yet."}""");
            return (HttpStatusCode.Forbidden, """{"error":"That key wasn't found."}""");
        });
        var client = new LicenseClient("https://service.test/", new HttpClient(server));

        var good = await client.CheckAsync(" GOOD ", "Zoë");
        Assert.Equal(LicenseOutcome.Valid, good.Outcome);
        Assert.Equal(Plan.Ultra, good.Plan);
        Assert.Equal("****-GOOD", good.Display);
        var sent = System.Text.Json.Nodes.JsonNode.Parse(server.Bodies[^1])!;
        Assert.Equal("Zoë", sent["credit"]!.GetValue<string>());
        Assert.Equal("GOOD", sent["key"]!.GetValue<string>());

        await client.CheckAsync("GOOD", null);
        Assert.DoesNotContain("credit", server.Bodies[^1]);

        var bad = await client.CheckAsync("NOPE", null);
        Assert.Equal(LicenseOutcome.Rejected, bad.Outcome);
        Assert.Equal("That key wasn't found.", bad.Message);
        Assert.Equal(LicenseOutcome.Offline, (await client.CheckAsync("DOWN", null)).Outcome);
        Assert.Equal(LicenseOutcome.Offline, (await new LicenseClient("").CheckAsync("GOOD", null)).Outcome);

        var credits = await client.CreditsAsync();
        Assert.Equal(["Mira"], credits.Ultra);
        Assert.Equal(["Kai"], credits.Pro);
    }

    [Fact]
    public void TheEngineGetsTheResolution()
    {
        var file = Path.Combine(_dir, "clip_engine.ini");
        File.WriteAllText(file, "resolution = native\n");
        Assert.Equal(0, EngineSettings.Load(file).Resolution);
        File.WriteAllText(file, "resolution = 1440p\n");
        var settings = EngineSettings.Load(file);
        Assert.Equal(1440, settings.Resolution);
        settings.Resolution = 720;
        settings.Save(file);
        Assert.Contains("resolution = 720", File.ReadAllLines(file));
        settings.Resolution = 0;
        settings.Save(file);
        Assert.Contains("resolution = native", File.ReadAllLines(file));
    }
}
