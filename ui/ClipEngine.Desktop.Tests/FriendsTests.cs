using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class FriendsTests
{
    private static Dictionary<string, FriendSeen> Roster(params (string Id, string Name, bool Online, string Game)[] rows) =>
        rows.ToDictionary(r => r.Id, r => new FriendSeen(r.Name, r.Online, r.Game));

    [Fact]
    public void FriendsComingOnlineOrStartingAGameAreAnnounced()
    {
        var before = Roster(("a", "@kai", false, ""), ("b", "@mira", true, ""), ("c", "@rook", true, "Valorant"), ("d", "@vex", true, "Apex"));
        var now = Roster(("a", "@kai", true, "The Finals"), ("b", "@mira", true, "Overwatch"), ("c", "@rook", true, "valorant"),
                         ("d", "@vex", false, ""), ("e", "@nova", true, ""));
        Assert.Equal(["@kai came online · playing The Finals", "@mira started playing Overwatch", "@nova came online"],
                     FriendAlerts.Changes(before, now));
        Assert.Empty(FriendAlerts.Changes(now, now));
    }

    [Fact]
    public void HowLongReadsShort()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("just now", FriendAlerts.Since(now.AddSeconds(-20), now));
        Assert.Equal("25 min", FriendAlerts.Since(now.AddMinutes(-25), now));
        Assert.Equal("2 h", FriendAlerts.Since(now.AddHours(-2), now));
        Assert.Equal("1 h 5 min", FriendAlerts.Since(now.AddMinutes(-65), now));
    }
}
