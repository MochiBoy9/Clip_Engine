using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class MomentNoticesTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CountsKillsAndMultiKills()
    {
        var n = new MomentNotices();
        Assert.Equal("Kill", n.OnMoment("kill", T0, 1, 0));
        Assert.Equal("Kill · 2 this match", n.OnMoment("kill", T0.AddSeconds(10), 2, 0));
        Assert.Equal("Double kill", n.OnMoment("kill", T0.AddSeconds(12), 3, 0));
        Assert.Equal("Triple kill", n.OnMoment("kill", T0.AddSeconds(15), 4, 0));
        Assert.Equal("5 kills without dying", n.OnMoment("kill", T0.AddSeconds(30), 5, 0));
        Assert.Null(n.OnMoment("death", T0.AddSeconds(31), 5, 0));
        Assert.Equal("Kill · 6 this match", n.OnMoment("kill", T0.AddSeconds(32), 6, 0));
        Assert.Equal("Revive", n.OnMoment("revive", T0.AddSeconds(40), 6, 0));
    }

    [Fact]
    public void AnnouncesANewBestOncePerMatch()
    {
        var n = new MomentNotices();
        n.OnMoment("kill", T0, 3, 3);
        Assert.Equal("New personal best · 4 kills", n.OnMoment("kill", T0.AddSeconds(20), 4, 3));
        Assert.Equal("Kill · 5 this match", n.OnMoment("kill", T0.AddSeconds(40), 5, 3));
        // No best yet is no record to beat.
        Assert.Equal("Kill", new MomentNotices().OnMoment("kill", T0, 1, 0));
    }

    [Fact]
    public void SumsUpTheMatchAndStartsAgain()
    {
        var n = new MomentNotices();
        n.OnMoment("kill", T0, 1, 0);
        n.OnMoment("assist", T0.AddSeconds(10), 1, 0);
        n.OnMoment("death", T0.AddSeconds(20), 1, 0);
        Assert.Equal("Match over · 1 / 1 / 1", n.OnMoment("match_end", T0.AddSeconds(30), 1, 0));
        // The old match's count is ignored until a new one starts.
        Assert.Equal("Kill", n.OnMoment("kill", T0.AddSeconds(60), 1, 0));
        Assert.Equal("Match started", n.OnMoment("match_start", T0.AddSeconds(90), 0, 0));
    }
}
