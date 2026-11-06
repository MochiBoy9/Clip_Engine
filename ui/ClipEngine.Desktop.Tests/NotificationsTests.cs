using System.Net;
using System.Text;
using System.Text.Json;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class NotificationsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void EachKindCanBeTurnedOffOrAllAtOnce()
    {
        var settings = new NoticeSettings();
        Assert.All(NoticeKinds.All, k => Assert.True(settings.Allows(k)));

        settings.Set(NoticeKind.Friends, false);
        Assert.False(settings.Allows(NoticeKind.Friends));
        Assert.True(settings.Allows(NoticeKind.FriendRequest));
        settings.Set(NoticeKind.Friends, false);
        Assert.Single(settings.Muted);

        settings.On = false;
        Assert.All(NoticeKinds.All, k => Assert.False(settings.Allows(k)));
        Assert.True(settings.IsOn(NoticeKind.Clip), "turning everything off keeps each kind's own choice");

        settings.On = true;
        settings.Set(NoticeKind.Friends, true);
        Assert.Empty(settings.Muted);
    }

    [Fact]
    public void KindsKeepTheirIds()
    {
        foreach (var kind in NoticeKinds.All)
        {
            Assert.Equal(kind, NoticeKinds.FromId(NoticeKinds.Id(kind)));
            Assert.NotEmpty(NoticeKinds.Label(kind));
            Assert.NotEmpty(NoticeKinds.Caption(kind));
        }
        Assert.Null(NoticeKinds.FromId("something_else"));
        Assert.Equal(NoticeKinds.All.Count, NoticeKinds.All.Select(NoticeKinds.Id).Distinct().Count());
    }

    [Fact]
    public void OldMomentAndFriendSwitchesBecomeFilters()
    {
        var json = """{"GameNotifications":true,"MomentNotices":false,"FriendNotices":true}""";
        var settings = JsonSerializer.Deserialize(json, DesktopSettingsJson.Default.DesktopSettings)!;
        settings.MoveOldNoticeSwitches();
        Assert.False(settings.Notices.IsOn(NoticeKind.Moment));
        Assert.True(settings.Notices.IsOn(NoticeKind.Friends));
        Assert.True(settings.Notices.On);

        var saved = JsonSerializer.Serialize(settings, DesktopSettingsJson.Default.DesktopSettings);
        Assert.DoesNotContain("MomentNotices", saved);
        Assert.DoesNotContain("FriendNotices", saved);
        Assert.Contains("\"moment\"", saved);
    }

    [Fact]
    public void TheHistoryIsKeptOnThisPc()
    {
        var file = Path.Combine(_dir, "notifications.json");
        var now = DateTimeOffset.Now;
        var records = Enumerable.Range(0, NoticeHistory.MaxKept + 10).Select(i => new NoticeRecord
        {
            Time = now.AddMinutes(-i),
            Kind = i == 3 ? "not_a_kind" : "clip",
            Title = $"Clip {i}",
            Text = "30 s",
            Target = "clip:C:\\clips\\a.mp4",
            Read = i > 1,
        }).ToList();
        NoticeHistory.Save(file, records);

        var loaded = NoticeHistory.Load(file);
        Assert.Equal(NoticeHistory.MaxKept - 1, loaded.Count);
        Assert.Equal("Clip 0", loaded[0].Title);
        Assert.False(loaded[0].Read);
        Assert.True(loaded[2].Read);
        Assert.DoesNotContain(loaded, r => r.Kind == "not_a_kind");
        Assert.Empty(NoticeHistory.Load(Path.Combine(_dir, "missing.json")));
    }

    [Fact]
    public void ARenewalIsAnnouncedOnceAFewDaysBefore()
    {
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var renews = now.AddDays(2);

        var notice = RenewalNotice.For(renews, false, "Pro", now, null);
        Assert.NotNull(notice);
        Assert.Equal("Subscription renews soon", notice.Value.Title);
        Assert.Contains("Pro subscription renews", notice.Value.Text);

        Assert.Null(RenewalNotice.For(renews, false, "Pro", now, renews.ToUnixTimeMilliseconds()));
        Assert.Null(RenewalNotice.For(now.AddDays(10), false, "Pro", now, null));
        Assert.Null(RenewalNotice.For(now.AddDays(-1), false, "Pro", now, null));
        Assert.Null(RenewalNotice.For(null, false, "Pro", now, null));

        var ending = RenewalNotice.For(renews, true, "Ultra", now, null);
        Assert.Equal("Subscription ending", ending!.Value.Title);
        Assert.Contains("Ultra subscription ends", ending.Value.Text);
    }

    [Fact]
    public void RenewalDaysAreSaidPlainly()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("today", RenewalNotice.Day(now.AddMinutes(1), now.AddMinutes(-1)));
        Assert.Equal("tomorrow", RenewalNotice.Day(now.AddDays(1), now));
        Assert.StartsWith("on ", RenewalNotice.Day(now.AddDays(3), now));
    }

    [Fact]
    public void FriendRequestsPopUpOnce()
    {
        var received = new[] { ("A1", "@nova"), ("B2", "Rook") };
        Assert.Equal(["@nova", "Rook"], FriendAlerts.NewRequests([], received));
        Assert.Equal(["Rook"], FriendAlerts.NewRequests(["A1"], received));
        Assert.Empty(FriendAlerts.NewRequests(["A1", "B2"], received));

        var friends = new[] { ("A1", "@nova"), ("C3", "Kai") };
        Assert.Equal(["@nova"], FriendAlerts.Accepted(new HashSet<string> { "A1", "D4" }, friends));
    }

    private sealed class Answer(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task TheServiceSaysWhenASubscriptionRenews()
    {
        var at = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
        var client = new LicenseClient("https://service.test/", new HttpClient(new Answer(
            $$"""{"plan":"pro","display":"****-GOOD","expires":null,"renews":{{at.ToUnixTimeMilliseconds()}},"ending":true}""")));
        var check = await client.CheckAsync("GOOD", null);
        Assert.Equal(LicenseOutcome.Valid, check.Outcome);
        Assert.Equal(at, check.Renews);
        Assert.True(check.Ending);

        var plain = await new LicenseClient("https://service.test/", new HttpClient(new Answer(
            """{"plan":"ultra","display":"****-GOOD","expires":null}"""))).CheckAsync("GOOD", null);
        Assert.Null(plain.Renews);
        Assert.False(plain.Ending);
    }

    [Fact]
    public void FiltersFollowTheSettings()
    {
        var desktop = new DesktopSettings();
        var saves = 0;
        var notices = new NotificationsViewModel(desktop, null, () => saves++);
        var friends = notices.Filters.Single(f => f.Kind == NoticeKind.Friends);

        friends.IsOn = false;
        Assert.False(desktop.Notices.IsOn(NoticeKind.Friends));
        Assert.Equal(1, saves);
        Assert.True(notices.Filters[0].IsFirst);

        notices.On = false;
        Assert.False(friends.IsEnabled);
        Assert.False(notices.Allows(NoticeKind.Clip));
    }
}
