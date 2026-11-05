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
    public async Task ASpecialKeyCanGiveARoleCreditsAndADirectLine()
    {
        var server = new FakeServer((request, _) => request.RequestUri!.AbsolutePath == "/v1/credits"
            ? (HttpStatusCode.OK, """{"ultra":["Mira"],"pro":["Kai"],"exclusive":["Rook"],"creator":["Vex"],"special":[]}""")
            : (HttpStatusCode.OK, """{"plan":"ultra","grant":true,"display":"CEK-AAAA…AAAA","role":"creator","roleLabel":"Content Creator","perks":["contact","bugs"],"credits":true,"reason":"Streams every week"}"""));
        var client = new LicenseClient("https://service.test/", new HttpClient(server));

        var check = await client.CheckAsync("CEK-AAAA-AAAA-AAAA", "Vex", "pc-1");
        Assert.Equal("creator", check.Role);
        Assert.Equal("Content Creator", check.RoleLabel);
        Assert.Equal(["contact", "bugs"], check.Perks);
        Assert.True(check.CreditsAllowed);
        Assert.Equal("Streams every week", check.Reason);

        var vm = new PlanViewModel(null, null, () => "", []);
        vm.Accept("CEK-AAAA-AAAA-AAAA", check);
        Assert.Equal("CONTENT CREATOR", vm.Badge);
        Assert.True(vm.ShowBadge);
        Assert.Equal("Clip Engine Content Creator", vm.Title);
        Assert.StartsWith("Ultra features: 4K recording", vm.Detail);
        Assert.Equal("Given for: Streams every week", vm.Reason);
        Assert.True(vm.CanBeCredited, "the developer let this key into the credits");
        Assert.True(vm.HasDirectLine);
        Assert.True(vm.HasBugReports);
        Assert.Equal("Talk to the developer", vm.ContactTitle);
        Assert.Equal("Your Content Creator key is on. Welcome!", vm.Message);
        Assert.False(vm.HasManage, "a special key has no subscription to manage");
        Assert.False(vm.SendMessageCommand.CanExecute(null), "nothing written yet");
        vm.ContactInput = "Hello!";
        Assert.True(vm.SendMessageCommand.CanExecute(null));
        Assert.True(vm.SendBugCommand.CanExecute(null));

        // A role on Free still shows its badge; no perks means no direct line.
        vm.Accept("CEK-BBBB", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Free, "", null, true, RoleLabel: "Special", Role: "special"));
        Assert.True(vm.ShowBadge);
        Assert.Equal("SPECIAL", vm.Badge);
        Assert.False(vm.HasContact);
        Assert.False(vm.SendMessageCommand.CanExecute(null));
        Assert.False(vm.CanBeCredited);
        Assert.False(vm.HasReason);

        vm.Remove();
        Assert.False(vm.ShowBadge);
        Assert.False(vm.HasRole);

        var credits = await client.CreditsAsync();
        Assert.Equal(["Rook"], credits.Exclusive);
        Assert.Equal(["Vex"], credits.Creator);
        Assert.Empty(credits.Special);
    }

    [Fact]
    public void ARolesExtrasAreKept()
    {
        var file = Path.Combine(_dir, "license.json");
        new LicenseState
        {
            Key = "CEK-1", IsSpecial = true, Role = "exclusive", RoleLabel = "Exclusive", Perks = ["bugs"], CreditsAllowed = true,
            Reason = "Beta tester", LastOk = DateTimeOffset.UtcNow,
        }.Save(file);
        var loaded = LicenseState.Load(file);
        Assert.Equal("Exclusive", loaded.RoleLabel);
        Assert.Equal(["bugs"], loaded.Perks);
        Assert.True(loaded.CreditsAllowed);
        Assert.Equal("Beta tester", loaded.Reason);
        loaded.Clear();
        Assert.Null(loaded.RoleLabel);
        Assert.Null(loaded.Perks);
        Assert.False(loaded.CreditsAllowed);
    }

    [Fact]
    public async Task MessagesGoToTheDeveloperAndAnswersComeBack()
    {
        var server = new FakeServer((request, body) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/contact" when body.Contains("\"kind\":\"bug\"") => (HttpStatusCode.Forbidden, """{"error":"Your key doesn't include bug reports."}"""),
            "/v1/contact" or "/v1/contact/list" => (HttpStatusCode.OK,
                """{"messages":[{"id":2,"kind":"message","text":"Bigger squads?","created":1767225600000,"reply":"Soon!","replied":1767229200000},{"id":1,"kind":"bug","text":"Crash","created":1767225000000,"reply":null,"replied":null}]}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var client = new ContactClient("https://service.test/", new HttpClient(server));

        var sent = await client.SendAsync("CEK-AAAA", "pc-1", ContactClient.Message, "Bigger squads?", "Mira");
        Assert.True(sent.Ok);
        var body = System.Text.Json.Nodes.JsonNode.Parse(server.Bodies[^1])!;
        Assert.Equal("pc-1", body["device"]!.GetValue<string>());
        Assert.Equal("message", body["kind"]!.GetValue<string>());
        Assert.Equal(2, sent.Messages.Count);
        Assert.Equal("Soon!", sent.Messages[0].Reply);
        Assert.Null(sent.Messages[1].Reply);
        var view = new ContactMessageView(sent.Messages[1]);
        Assert.Equal("BUG REPORT", view.Kind);
        Assert.False(view.HasReply);

        var refused = await client.SendAsync("CEK-AAAA", "pc-1", ContactClient.Bug, "Crash", "");
        Assert.False(refused.Ok);
        Assert.Equal("Your key doesn't include bug reports.", refused.Message);
        Assert.True((await client.ListAsync("CEK-AAAA", "pc-1")).Ok);
        Assert.False((await new ContactClient("").ListAsync("CEK-AAAA", "pc-1")).Ok);
    }

    [Fact]
    public async Task ADonationMakesASupporter()
    {
        var server = new FakeServer((request, body) => request.RequestUri!.AbsolutePath == "/v1/credits"
            ? (HttpStatusCode.OK, """{"ultra":[],"pro":["Kai"],"supporter":["Mira","Rook"],"exclusive":[],"creator":[],"special":[]}""")
            : body.Contains("GIFT-SMALL")
                ? (HttpStatusCode.OK, """{"plan":"free","grant":false,"display":"****-SMAL","role":"supporter","roleLabel":"Supporter","extraStorage":3221225472,"credits":false,"supporter":true,"donated":300,"expires":null}""")
                : (HttpStatusCode.OK, """{"plan":"free","grant":false,"display":"****-BIG1","role":"supporter","roleLabel":"Supporter","extraStorage":10737418240,"credits":true,"supporter":true,"donated":1000,"expires":null}"""));
        var client = new LicenseClient("https://service.test/", new HttpClient(server));

        var small = await client.CheckAsync("GIFT-SMALL", null, "pc-1");
        Assert.Equal(LicenseOutcome.Valid, small.Outcome);
        Assert.False(small.IsSpecial);
        Assert.True(small.Supporter);
        Assert.Equal(300L, small.Donated!.Value);
        Assert.Equal(3L << 30, small.ExtraStorage);
        Assert.Equal("Supporter", small.RoleLabel);

        var vm = new PlanViewModel(null, null, () => "", []);
        vm.Accept("GIFT-SMALL", small);
        Assert.Equal(Plan.Free, vm.Plan);
        Assert.True(vm.IsSupporter);
        Assert.Equal("SUPPORTER", vm.Badge);
        Assert.True(vm.ShowBadge);
        Assert.Equal("Clip Engine Supporter", vm.Title);
        Assert.Equal(3L << 30, vm.ExtraStorage);
        Assert.Contains("your $3 in donations", vm.Detail);
        Assert.Contains("3 GB", vm.Detail);
        Assert.False(vm.CanBeCredited, "under $5: not in the credits yet");
        Assert.Equal("You've given $3 so far. From $5 in all, your name can go in the credits.", vm.DonateStatus);
        Assert.False(vm.HasManage, "a donation has no subscription to manage");
        Assert.False(vm.ShowKeyBox);
        Assert.StartsWith("You're a Supporter", vm.Message);

        var big = await client.CheckAsync("GIFT-BIG", "Mira", "pc-1");
        vm.Accept("GIFT-BIG", big);
        Assert.True(vm.CanBeCredited, "from $5 in all");
        Assert.False(vm.HasDonateStatus);
        Assert.Equal(10L << 30, vm.ExtraStorage);

        // A subscriber who donated keeps their plan's badge, with the storage bonus on top.
        vm.Accept("POLAR-PRO", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Pro, "****-PRO1", null, ExtraStorage: 10L << 30,
                                                Supporter: true, Donated: 1000));
        Assert.Equal("PRO", vm.Badge);
        Assert.True(vm.IsSupporter);
        Assert.True(vm.CanBeCredited);
        Assert.Contains("110 GB in the cloud", vm.Detail);
        Assert.Contains("and for your $10 in donations", vm.Detail);

        // A plain subscription has no bonus.
        vm.Accept("POLAR-PRO", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Pro));
        Assert.False(vm.IsSupporter);
        Assert.Equal(0, vm.ExtraStorage);
        vm.Remove();
        Assert.False(vm.IsSupporter);
        Assert.False(vm.ShowBadge);

        var credits = await client.CreditsAsync();
        Assert.Equal(["Mira", "Rook"], credits.Supporter);
    }

    [Fact]
    public void DonateLinksFillInTheAmount()
    {
        Assert.Equal("$5", Donations.Money(500));
        Assert.Equal("$2.50", Donations.Money(250));
        Assert.Equal(["$3", "$5", "$10", "$25", "Any amount"], new PlanViewModel(null, null, () => "", []).DonateOptions.Select(o => o.Label).ToList());
        if (PlanConfig.DonateUrl.Length == 0)
        {
            Assert.Equal("", PlanConfig.DonateLink(5));
            return;
        }
        Assert.EndsWith("amount=500", PlanConfig.DonateLink(5));
        Assert.Equal(PlanConfig.DonateUrl, PlanConfig.DonateLink(0));
    }

    [Fact]
    public void ADonationIsKept()
    {
        var file = Path.Combine(_dir, "license.json");
        new LicenseState { Key = "GIFT", Supporter = true, Donated = 1200, ExtraStorage = 12L << 30, LastOk = DateTimeOffset.UtcNow }.Save(file);
        var loaded = LicenseState.Load(file);
        Assert.True(loaded.Supporter);
        Assert.Equal(1200L, loaded.Donated!.Value);
        loaded.Clear();
        Assert.False(loaded.Supporter);
        Assert.Null(loaded.Donated);
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
