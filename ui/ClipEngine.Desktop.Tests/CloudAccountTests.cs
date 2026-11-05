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
    public void TheHubLinkKeepsTheCodeOutOfTheRequest()
    {
        var api = new CloudApi("https://clip-engine.example.workers.dev");
        Assert.Equal("https://clip-engine.example.workers.dev/hub/signin#code=K7QM-2X9D-PA4R-HT", api.HubSignInLink("K7QM-2X9D-PA4R-HT"));
    }
}
