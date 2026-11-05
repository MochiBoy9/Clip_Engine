using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

// Inviting friends: the app's side of server/src/referrals.js.
public class ReferralTests
{
    private sealed class FakeServer(Func<HttpRequestMessage, JsonNode?, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, JsonNode? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancel);
            var body = text.Length > 0 ? JsonNode.Parse(text) : null;
            Calls.Add((request, body));
            var (status, reply) = answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private const string Status =
        """{"code":"K7QM-2X9D","link":"https://service.test/r/K7QM2X9D","friends":2,"notCounted":1,"needed":3,"toGo":1,"rewardDays":30,"rewards":[],"done":false}""";

    [Fact]
    public async Task TheClientSendsThePcAndReadsTheAnswers()
    {
        var server = new FakeServer((request, body) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/referrals" => (HttpStatusCode.OK, Status.Replace("\"done\":false", "\"done\":false,\"secret\":\"s3cret\"")),
            "/v1/referrals/activate" when body?["code"]?.GetValue<string>() == "NOPE" =>
                (HttpStatusCode.NotFound, """{"error":"That invite code wasn't found. Check it with your friend."}"""),
            "/v1/referrals/activate" => (HttpStatusCode.OK, """{"counted":true,"reason":"","inviter":"Enterro","code":"K7QM-2X9D"}"""),
            _ => (HttpStatusCode.OK, Status),
        });
        var client = new ReferralClient("https://service.test/", new HttpClient(server));

        var (made, error) = await client.CreateAsync("machine-1", "device-1", "Kai");
        Assert.Equal("", error);
        Assert.Equal("K7QM-2X9D", made!.Code);
        Assert.Equal("s3cret", made.Secret);
        Assert.Equal(2, made.Friends);
        Assert.Equal(1, made.ToGo);
        Assert.Equal("machine-1", server.Calls[^1].Body!["pc"]!.GetValue<string>());
        Assert.Equal("Kai", server.Calls[^1].Body!["name"]!.GetValue<string>());

        var (status, _) = await client.StatusAsync("K7QM-2X9D", "s3cret", "machine-1", "device-1");
        Assert.Equal(3, status!.Needed);
        Assert.Equal("Bearer", server.Calls[^1].Request.Headers.Authorization!.Scheme);
        Assert.Equal("s3cret", server.Calls[^1].Request.Headers.Authorization!.Parameter);
        Assert.Contains("pc=machine-1", server.Calls[^1].Request.RequestUri!.Query);

        var joined = await client.ActivateAsync("machine-2", "device-2", true, " K7QM-2X9D ");
        Assert.True(joined.Ok);
        Assert.True(joined.Counted);
        Assert.Equal("Enterro", joined.Inviter);
        Assert.Equal("K7QM-2X9D", server.Calls[^1].Body!["code"]!.GetValue<string>());
        Assert.True(server.Calls[^1].Body!["fresh"]!.GetValue<bool>());

        var plain = await client.ActivateAsync("machine-3", "device-3", false, null);
        Assert.Null(server.Calls[^1].Body!["code"]);
        Assert.True(plain.Ok);

        var wrong = await client.ActivateAsync("machine-3", "device-3", true, "NOPE");
        Assert.False(wrong.Ok);
        Assert.Contains("wasn't found", wrong.Error);

        Assert.False((await new ReferralClient("").ActivateAsync("a", "b", true, null)).Ok);
    }

    [Fact]
    public void TheMachineIdIsAHashOfWindowsOwn()
    {
        var id = MachineId.Hash("6F0E3C1A-1111-2222-3333-444455556666");
        Assert.Equal(64, id.Length);
        Assert.Matches("^[0-9a-f]{64}$", id);
        Assert.Equal(id, MachineId.Hash(" 6f0e3c1a-1111-2222-3333-444455556666 "));
        Assert.NotEqual(id, MachineId.Hash("6F0E3C1A-1111-2222-3333-444455556667"));
    }

    [Fact]
    public async Task TheInviteCardShowsTheLinkProgressAndRewards()
    {
        var rewards = "[]";
        var server = new FakeServer((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/v1/referrals" => (HttpStatusCode.OK, Status.Replace("\"rewards\":[]", $"\"rewards\":{rewards}").Replace("\"done\":false", "\"done\":false,\"secret\":\"s3cret\"")),
            _ => (HttpStatusCode.OK, Status.Replace("\"rewards\":[]", $"\"rewards\":{rewards}")),
        });
        var desktop = new DesktopSettings();
        desktop.Squad.PlayerName = "Enterro";
        var plan = new PlanViewModel(null, null, () => "", []);
        var notes = new List<string>();
        var invite = new InviteViewModel(desktop, () => "https://service.test", plan, freshInstall: false, notify: notes.Add,
                                         http: new HttpClient(server));
        Assert.True(invite.NoLink);
        Assert.False(invite.ShowCodeBox, "Clip Engine was already on this PC");
        Assert.Equal("Invite 3 friends, get a month of Pro", invite.Headline);

        await invite.GetLinkAsync();
        Assert.True(invite.HasLink);
        Assert.Equal("https://service.test/r/K7QM2X9D", invite.Link);
        Assert.Equal("K7QM-2X9D", desktop.Referral.Code);
        Assert.Equal("s3cret", Online.Unprotect(desktop.Referral.Secret));
        Assert.Equal("2 of 3 friends · 1 didn't count", invite.ProgressText);
        Assert.Equal(2 / 3.0, invite.Progress, 3);
        Assert.Equal("Enterro", server.Calls[0].Body!["name"]!.GetValue<string>());
        Assert.Contains(notes, n => n.Contains("2 friends installed Clip Engine"));

        // The third friend earns a key. A PC with a working key of its own (a subscription) keeps it; the reward waits.
        plan.Accept("POLAR-KEY", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Pro));
        rewards = """[{"key":"CEK-AAAA-BBBB-CCCC","at":1}]""";
        await invite.TakeStatusAsync(ReferralClient.Parse(JsonNode.Parse(Status.Replace("\"rewards\":[]", $"\"rewards\":{rewards}")
            .Replace("\"friends\":2", "\"friends\":3").Replace("\"toGo\":1", "\"toGo\":0").Replace("\"done\":false", "\"done\":true"))!));
        Assert.True(invite.HasRewards);
        Assert.Equal(["CEK-AAAA-BBBB-CCCC"], invite.Rewards);
        Assert.Equal(1.0, invite.Progress);
        Assert.Equal(["CEK-AAAA-BBBB-CCCC"], desktop.Referral.SeenRewards);
        Assert.Contains(notes, n => n.Contains("Your key is on the Friends page"));
        Assert.Contains(notes, n => n.Contains("A friend installed Clip Engine"));
        Assert.Equal(Plan.Pro, plan.Plan);

        // Seen once, announced once.
        var count = notes.Count;
        await invite.TakeStatusAsync(ReferralClient.Parse(JsonNode.Parse(Status.Replace("\"rewards\":[]", $"\"rewards\":{rewards}")
            .Replace("\"friends\":2", "\"friends\":3"))!));
        Assert.Equal(count, notes.Count);
    }

    [Fact]
    public async Task ANewInstallCanAddAFriendsCodeForAWeek()
    {
        var server = new FakeServer((_, body) => body?["code"]?.GetValue<string>() switch
        {
            "MINE-0000" => (HttpStatusCode.OK, """{"counted":false,"reason":"That's your own invite code. Send it to a friend.","inviter":"","code":"M1NE-0000"}"""),
            _ => (HttpStatusCode.OK, """{"counted":true,"reason":"","inviter":"Enterro","code":"K7QM-2X9D"}"""),
        });
        var desktop = new DesktopSettings();
        var invite = new InviteViewModel(desktop, () => "https://service.test", null, freshInstall: true, http: new HttpClient(server));
        Assert.True(desktop.Referral.FreshInstall);
        Assert.True(invite.ShowCodeBox);

        invite.PrefillCode("MINE-0000");
        Assert.True(invite.AddCodeCommand.CanExecute(null));
        await invite.AddCodeAsync();
        Assert.True(invite.MessageIsError);
        Assert.Contains("your own invite code", invite.Message!);
        Assert.True(invite.ShowCodeBox, "still free for a friend's code");

        invite.CodeInput = "k7qm 2x9d";
        await invite.AddCodeAsync();
        Assert.False(invite.MessageIsError);
        Assert.False(invite.ShowCodeBox);
        Assert.Equal("You joined from Enterro's invite.", invite.InvitedText);
        Assert.Equal("", invite.CodeInput);
        Assert.True(server.Calls[^1].Body!["fresh"]!.GetValue<bool>());

        // A week on, the box is gone.
        var later = new DesktopSettings();
        later.Referral.FirstStart = DateTimeOffset.UtcNow - TimeSpan.FromDays(8);
        later.Referral.FreshInstall = true;
        Assert.False(new InviteViewModel(later, () => "", null, freshInstall: true).ShowCodeBox);
    }
}
