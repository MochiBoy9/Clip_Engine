using System.Text.Json.Nodes;
using ClipEngine.Desktop.Services.Cloud;
using Xunit;

namespace ClipEngine.Desktop.Tests;

// The cloud account as the online service sends it (server/src/library.js, account()).
public class CloudAccountTests
{
    [Fact]
    public void ReadsTheBadgeAndWhetherItsHidden()
    {
        var account = CloudAccount.From(JsonNode.Parse("""
            { "id": "A1B2C3D4", "name": "Enterro", "plan": "ultra", "planLabel": "Ultra", "roleLabel": "Content Creator",
              "badge": { "kind": "creator", "label": "Content Creator" }, "hideBadge": true }
            """));
        Assert.Equal("Content Creator", account.BadgeLabel);
        Assert.True(account.HideBadge);
    }

    [Fact]
    public void FreeAndOlderServicesHaveNoBadge()
    {
        var free = CloudAccount.From(JsonNode.Parse("""{ "id": "A1B2C3D4", "name": "Kai", "plan": "free", "badge": null, "hideBadge": false }"""));
        Assert.Null(free.BadgeLabel);
        Assert.False(free.HideBadge);
        var older = CloudAccount.From(JsonNode.Parse("""{ "id": "A1B2C3D4", "name": "Kai" }"""));
        Assert.Null(older.BadgeLabel);
        Assert.False(older.HideBadge);
    }

    [Fact]
    public void ReadsTheEmailAndWhetherItsConfirmed()
    {
        var account = CloudAccount.From(JsonNode.Parse("""
            { "id": "A1B2C3D4", "name": "Kai", "email": "kai@example.com", "emailVerified": true, "google": true }
            """));
        Assert.Equal("kai@example.com", account.Email);
        Assert.True(account.EmailVerified);
        Assert.True(account.Google);
        var older = CloudAccount.From(JsonNode.Parse("""{ "id": "A1B2C3D4", "name": "Kai", "email": null }"""));
        Assert.Null(older.Email);
        Assert.False(older.EmailVerified);
    }

    [Fact]
    public void GoogleSignInOpensTheServiceWithTheAppsState()
    {
        var api = new CloudApi("https://clip-engine.example.workers.dev/");
        Assert.Equal("https://clip-engine.example.workers.dev/v1/auth/google?app=state-0123456789abcdef", api.GoogleSignInLink("state-0123456789abcdef"));
    }

    // clipengine://cloud/signin/<code>?state=<state>, the service's way back from Sign in with Google.
    [Theory]
    [InlineData("clipengine://cloud/signin/K7QM-2X9D-PA4R-HT?state=abc123", "K7QM-2X9D-PA4R-HT", "abc123")]
    [InlineData("clipengine://cloud/signin/K7QM-2X9D-PA4R-HT", "K7QM-2X9D-PA4R-HT", "")]
    public void ReadsTheLinkBackFromGoogle(string link, string code, string state)
    {
        Assert.Equal((code, state), ClipEngine.Desktop.ViewModels.CloudViewModel.ReadSignInLink(new Uri(link)));
    }

    [Fact]
    public void OtherLinksArentSignIns()
    {
        Assert.Null(ClipEngine.Desktop.ViewModels.CloudViewModel.ReadSignInLink(new Uri("clipengine://squad/join/ABCD")));
    }

    [Fact]
    public void TheHubLinkKeepsTheCodeOutOfTheRequest()
    {
        var api = new CloudApi("https://clip-engine.example.workers.dev");
        Assert.Equal("https://clip-engine.example.workers.dev/hub/signin#code=K7QM-2X9D-PA4R-HT", api.HubSignInLink("K7QM-2X9D-PA4R-HT"));
    }
}
