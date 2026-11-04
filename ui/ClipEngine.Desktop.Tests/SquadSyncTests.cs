using System.Text.Json.Nodes;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Squad;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class SquadSyncTests
{
    private sealed class FakeChannel : ISquadChannel
    {
        public List<JsonObject> Sent { get; } = [];
        public bool Connected { get; private set; }
        public event Action<JsonObject>? Received;
        public event Action<bool>? ConnectedChanged;
        public event Action? Rejected;
        public void Start() { }
        public void Send(JsonObject message) => Sent.Add((JsonObject)message.DeepClone());
        public void Connect()
        {
            Connected = true;
            ConnectedChanged?.Invoke(true);
        }
        public void Receive(string json) => Received?.Invoke((JsonObject)JsonNode.Parse(json)!);
        public void Reject() => Rejected?.Invoke();
        public List<JsonObject> Take(string type)
        {
            var found = Sent.Where(m => (string?)m["t"] == type).ToList();
            Sent.RemoveAll(m => (string?)m["t"] == type);
            return found;
        }
        public void Dispose() { }
    }

    private sealed class Rig
    {
        public long Now = 1_762_142_400_000;
        public FakeChannel Channel { get; } = new();
        public List<string> Commands { get; } = [];
        public List<string> Discord { get; } = [];
        public Dictionary<string, LocalClip> Clips { get; } = [];
        public List<LocalView> Uploads { get; } = [];
        public SquadSettings Settings { get; } = new() { SquadId = "ABCD1234", MemberId = "pME00000000", Token = "t" };
        public SquadSync Sync { get; }

        public Rig(long serverAhead = 250)
        {
            Sync = new SquadSync(Settings, Channel, new SquadViewIndex(null),
                                 c => { (c.StartsWith("squad-presence") ? Discord : Commands).Add(c); return true; },
                                 p => Clips.GetValueOrDefault(p), () => Now);
            Sync.UploadWanted += Uploads.Add;
            Channel.Connect();
            // One ping answered with the server 250 ms ahead and a 40 ms round trip.
            var ping = Channel.Take("ping").Single();
            var sent = (long)ping["c"]!;
            Now += 40;
            Channel.Receive($$"""{"t":"pong","c":{{sent}},"s":{{sent + 20 + serverAhead}}}""");
            Channel.Receive("""
                {"t":"welcome","squad":{"id":"ABCD1234","name":"Night shift","invite":"ABCD-1234-0000-0000-0000","you":"pME00000000",
                 "members":[{"id":"pME00000000","name":"Enterro","owner":true,"online":true},{"id":"pKAI0000000","name":"Kai","owner":false,"online":true}]},
                 "presences":[{"member":"pKAI0000000","game":"Valorant","match":null}],"groups":[]}
                """);
        }
    }

    [Fact]
    public void ConnectingSaysHelloAndWhatsBeingPlayed()
    {
        var rig = new Rig();
        Assert.Equal(250, rig.Sync.Clock.OffsetMs);
        Assert.Equal("Night shift", rig.Settings.SquadName);
        rig.Sync.OnRecordingStarted("Valorant");
        var presence = rig.Channel.Take("presence").Last();
        Assert.Equal("Valorant", (string?)presence["game"]);
        Assert.Null(presence["match"]);

        rig.Sync.OnMomentDetected("match_start", "Valorant", "ROUND 1");
        rig.Sync.OnMomentTime("match_start", rig.Now - 100);
        presence = rig.Channel.Take("presence").Last();
        Assert.Equal(rig.Now - 100 + 250, (long)presence["match"]!);
        Assert.Empty(rig.Channel.Take("moment"));  // match starts aren't shared by default

        rig.Sync.OnRecordingStarted("Desktop");
        Assert.Equal("", (string?)rig.Channel.Take("presence").Last()["game"]);
    }

    [Fact]
    public void ASquadmatesKillSavesThisPlayersViewOfIt()
    {
        var rig = new Rig();
        rig.Sync.OnRecordingStarted("Valorant");
        var at = rig.Now + 250 - 800;  // the kill, in server time, a little while ago
        rig.Channel.Receive($$"""{"t":"moment","id":"m1","type":"kill","at":{{at}},"game":"valorant","text":"KAI > X","by":"pKAI0000000","byName":"Kai"}""");
        var local = at - 250;
        Assert.Equal($"squad-save m1 {local - 10_000} {local + 5_000} Valorant", Assert.Single(rig.Commands));

        rig.Clips[@"C:\clips\v.mp4"] = new LocalClip(@"C:\clips\v.mp4", local - 10_100, 15.1, "squad", false);
        rig.Sync.OnSquadViewSaved("m1", @"C:\clips\v.mp4");
        var view = Assert.Single(rig.Channel.Take("view"));
        Assert.Equal("m1", (string?)view["moment"]);
        Assert.Equal(local - 10_100 + 250, (long)view["utc_start"]!);
        Assert.Equal(@"C:\clips\v.mp4", rig.Sync.Index.Find("m1")!.Path);
        Assert.Empty(rig.Uploads);  // a plain kill waits until someone asks for it

        rig.Channel.Receive("""{"t":"upload-request","moment":"m1","from":"pKAI0000000"}""");
        Assert.Equal("m1", Assert.Single(rig.Uploads).Moment);
    }

    [Fact]
    public void MomentsInAnotherGameOrWhileNotRecordingAreLeftAlone()
    {
        var rig = new Rig();
        rig.Channel.Receive($$"""{"t":"moment","id":"m1","type":"kill","at":{{rig.Now}},"game":"Valorant","text":"","byName":"Kai"}""");
        rig.Sync.OnRecordingStarted("Overwatch");
        rig.Channel.Receive($$"""{"t":"moment","id":"m2","type":"kill","at":{{rig.Now}},"game":"Valorant","text":"","byName":"Kai"}""");
        Assert.Empty(rig.Commands);
    }

    [Fact]
    public void OwnKillIsSharedAndItsAutomaticClipBecomesTheView()
    {
        var rig = new Rig();
        rig.Sync.OnRecordingStarted("Valorant");
        var killAt = rig.Now;
        rig.Sync.OnMomentDetected("kill", "Valorant", "ENTERRO > X");
        rig.Sync.OnMomentTime("kill", killAt);
        var moment = Assert.Single(rig.Channel.Take("moment"));
        Assert.Equal(killAt + 250, (long)moment["at"]!);
        Assert.Equal("ENTERRO > X", (string?)moment["text"]);

        rig.Channel.Receive($$"""{"t":"moment-ok","ref":"{{(string)moment["ref"]!}}","id":"m9","linked":["pKAI0000000"]}""");
        // The automatic clip lands a few seconds later, covering the kill.
        rig.Clips["a.mp4"] = new LocalClip("a.mp4", killAt - 10_050, 15.2, "auto", false);
        rig.Sync.OnClipSaved("a.mp4");
        var view = Assert.Single(rig.Channel.Take("view"));
        Assert.Equal("m9", (string?)view["moment"]);
        Assert.Equal(killAt - 10_050 + 250, (long)view["utc_start"]!);

        // Already covered, so no squad clip of its own later.
        rig.Now += 60_000;
        rig.Sync.Tick();
        Assert.Empty(rig.Commands);
    }

    [Fact]
    public void OwnMomentWithNoAutomaticClipGetsOneOfItsOwn()
    {
        var rig = new Rig();
        rig.Settings.Uploads = "all";
        rig.Sync.OnRecordingStarted("The Finals");
        var at = rig.Now;
        rig.Sync.OnMomentDetected("revive", "The Finals", "REVIVED");
        rig.Sync.OnMomentTime("revive", at);
        var reference = (string)rig.Channel.Take("moment").Single()["ref"]!;
        rig.Channel.Receive($$"""{"t":"moment-ok","ref":"{{reference}}","id":"m5","linked":[]}""");
        rig.Now = at + 5_000;
        rig.Sync.Tick();
        Assert.Empty(rig.Commands);
        rig.Now = at + 13_001;
        rig.Sync.Tick();
        Assert.Equal($"squad-save m5 {at - 10_000} {at + 5_000} The Finals", Assert.Single(rig.Commands));
        rig.Clips["s.mp4"] = new LocalClip("s.mp4", at - 10_000, 15, "squad", false);
        rig.Sync.OnSquadViewSaved("m5", "s.mp4");
        Assert.Equal("m5", Assert.Single(rig.Uploads).Moment);  // uploads = all
    }

    [Fact]
    public void NothingIsSharedBeforeTheClockIsKnownOrWhenTurnedOff()
    {
        var rig = new Rig();
        rig.Settings.ShareMoments = [];
        rig.Sync.OnMomentDetected("kill", "Valorant", "X");
        rig.Sync.OnMomentTime("kill", rig.Now);
        Assert.Empty(rig.Channel.Take("moment"));
        // A time line for a different moment than the one detected is ignored.
        rig.Settings.ShareMoments = ["kill"];
        rig.Sync.OnMomentDetected("death", "Valorant", "X");
        rig.Sync.OnMomentTime("kill", rig.Now);
        Assert.Empty(rig.Channel.Take("moment"));
    }

    [Fact]
    public void PresenceGroupsAndMembers()
    {
        var rig = new Rig();
        rig.Channel.Receive("""
            {"t":"presence","member":"pKAI0000000","online":true,"game":"Valorant","match":null,
             "groups":[{"game":"Valorant","members":["pME00000000","pKAI0000000"]}]}
            """);
        Assert.Equal("Kai", Assert.Single(rig.Sync.PlayingWithMe()).Name);
        rig.Channel.Receive("""{"t":"joined","member":"pREMY000000","name":"Remy"}""");
        rig.Channel.Receive("""{"t":"left","member":"pKAI0000000","owner":null}""");
        Assert.Equal(["Enterro", "Remy"], rig.Sync.Members.Select(m => m.Name).Order());
        Assert.Empty(rig.Sync.PlayingWithMe());
        rig.Sync.Unlink("pREMY000000");
        Assert.Equal("pREMY000000", (string?)rig.Channel.Take("unlink").Single()["member"]);
    }

    [Fact]
    public void TheDiscordStatusShowsTheSquad()
    {
        var rig = new Rig();
        rig.Sync.Server = "https://clip-engine.example.workers.dev";
        rig.Sync.OnRecordingStarted("Valorant");
        rig.Channel.Receive("""
            {"t":"presence","member":"pKAI0000000","online":true,"game":"Valorant","match":null,
             "groups":[{"game":"Valorant","members":["pME00000000","pKAI0000000"]}]}
            """);
        Assert.Equal("squad-presence 2 2 sABCD1234", rig.Discord.Last());
        rig.Settings.JoinOnDiscord = true;
        rig.Sync.UpdateDiscord();
        Assert.Equal("squad-presence 2 2 sABCD1234 https://clip-engine.example.workers.dev/s/ABCD-1234-0000-0000-0000", rig.Discord.Last());
        var count = rig.Discord.Count;
        rig.Sync.UpdateDiscord();
        Assert.Equal(count, rig.Discord.Count);  // only changes go to the engine
        rig.Settings.JoinOnDiscord = false;
        rig.Sync.OnRecordingStopped();
        Assert.Equal("squad-presence 0", rig.Discord.Last());
    }

    [Fact]
    public void UnlinkingIsSentAgainAfterReconnecting()
    {
        var rig = new Rig();
        rig.Sync.Unlink("pKAI0000000");
        Assert.True(rig.Sync.IsUnlinked("pKAI0000000"));
        Assert.Equal("pKAI0000000", (string?)rig.Channel.Take("unlink").Single()["member"]);
        rig.Channel.Connect();
        var again = rig.Channel.Take("unlink").Single();
        Assert.Equal("pKAI0000000", (string?)again["member"]);
        Assert.False((bool)again["undo"]!);

        rig.Sync.Unlink("pKAI0000000", undo: true);
        Assert.False(rig.Sync.IsUnlinked("pKAI0000000"));
        Assert.True((bool)rig.Channel.Take("unlink").Single()["undo"]!);
        rig.Channel.Connect();
        Assert.Empty(rig.Channel.Take("unlink"));
    }

    [Fact]
    public void ANewInviteCodeShowsUp()
    {
        var rig = new Rig();
        var changed = 0;
        rig.Sync.Changed += () => changed++;
        rig.Sync.UpdateInfo(rig.Sync.Info! with { Invite = "ABCD-1234-1111-1111-1111" });
        Assert.Equal("ABCD-1234-1111-1111-1111", rig.Sync.Info!.Invite);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void PingsFollowTheirSchedule()
    {
        var rig = new Rig();
        rig.Now += 1000;
        rig.Sync.Tick();
        Assert.Single(rig.Channel.Take("ping"));
        rig.Sync.Tick();
        Assert.Empty(rig.Channel.Take("ping"));
        rig.Now += 60_000;
        rig.Sync.Tick();
        rig.Sync.Tick();
        rig.Sync.Tick();
        Assert.True(rig.Channel.Take("ping").Count <= 2);
    }
}

public class SquadPlanTests
{
    [Fact]
    public void ClockPicksTheQuickestRoundTrip()
    {
        var clock = new SquadClock();
        Assert.False(clock.Synced);
        clock.AddSample(1000, 1400, 5000);  // slow: offset 3800
        clock.AddSample(2000, 2020, 5810);  // quicker: offset 3800
        clock.AddSample(3000, 3010, 6905);  // quickest: 6905 - 3005 = 3900
        Assert.Equal(3900, clock.OffsetMs);
        Assert.Equal(5, clock.UncertaintyMs);
        Assert.Equal(13_900, clock.ToServer(10_000));
        Assert.Equal(10_000, clock.ToLocal(13_900));
        clock.AddSample(5000, 1000, 0);  // nonsense is ignored
        Assert.Equal(3900, clock.OffsetMs);
    }

    [Fact]
    public void OwnClipNeedsRoomAroundTheMoment()
    {
        LocalClip[] clips =
        [
            new("long.mp4", 0, 600, "long", false),
            new("a.mp4", 10_000, 15, "auto", false),
            new("b.mp4", 8_000, 30, "hotkey", false),
            new("old.mp4", 0, 10, "auto", false),
        ];
        Assert.Equal("a.mp4", SquadPlan.OwnClipFor(clips, 20_000)!.Path);  // the shorter one
        Assert.Equal("b.mp4", SquadPlan.OwnClipFor(clips, 24_000)!.Path);  // a.mp4 ends too soon after
        Assert.Null(SquadPlan.OwnClipFor(clips, 300_000));                  // long recordings don't count
    }

    [Fact]
    public void UploadPolicies()
    {
        Assert.True(SquadPlan.UploadsByItself("all", "kill", false));
        Assert.False(SquadPlan.UploadsByItself("highlights", "kill", false));
        Assert.True(SquadPlan.UploadsByItself("highlights", "kill", true));
        Assert.True(SquadPlan.UploadsByItself("highlights", "highlight", false));
        Assert.False(SquadPlan.UploadsByItself("off", "highlight", true));
        Assert.False(SquadPlan.UploadsOnRequest("off"));
        Assert.True(SquadPlan.UploadsOnRequest("highlights"));
    }

    [Fact]
    public void ViewsLineUpAndTheHighlightCuts()
    {
        var mine = new SquadView("pA", "Enterro", 1_000_000, 15, true, true);
        var kai = new SquadView("pB", "Kai", 1_001_500, 15, true, false);
        var remy = new SquadView("pC", "Remy", 999_000, 12, true, false);
        var offsets = SquadPlan.SyncOffsets([mine, kai, remy]);
        Assert.Equal(1.5, offsets["pA"]);
        Assert.Equal(0, offsets["pB"]);
        Assert.Equal(2.5, offsets["pC"]);

        // Kai made the play at 1,010,000: 8.5 s into Kai's view.
        var parts = SquadPlan.Highlight([mine, kai, remy], "pB", 1_010_000);
        Assert.Equal(3, parts.Count);
        Assert.Equal(("pB", 2.5, 11.0), (parts[0].View.Member, parts[0].From, parts[0].To));
        Assert.Equal(("pA", 7.5, 11.5), (parts[1].View.Member, parts[1].From, parts[1].To));
        Assert.Equal(("pC", 8.5, 12.0), (parts[2].View.Member, parts[2].From, parts[2].To));  // cut at the clip's end
        Assert.Empty(SquadPlan.Highlight([], "pB", 0));
    }

    [Fact]
    public void SaveWindowsStayInTheBuffer()
    {
        Assert.Equal((90_000L, 105_000L), SquadPlan.SaveWindow(100_000, 10, 5));
        Assert.Equal((50_000L, 130_000L), SquadPlan.SaveWindow(100_000, 500, 500));
    }

    [Fact]
    public void ReadsTheEnginesSquadLines()
    {
        static EngineEvent? Parse(string message) =>
            EngineLogParser.ParseLine($"2026-11-03 21:04:05.123 INFO  {message}") is { } line ? EngineLogParser.ParseEvent(line) : null;
        Assert.Equal(new MomentTime("kill", 1762142400123), Parse("Moment kill at 1762142400123"));
        Assert.Equal(new SquadViewSaved("m1ABC", @"C:\Videos\Clip_Engine\Valorant\Valorant_2026-11-03_21-04-05.mp4"),
                     Parse(@"Squad view m1ABC saved to C:\Videos\Clip_Engine\Valorant\Valorant_2026-11-03_21-04-05.mp4"));
        Assert.Equal(new SquadViewSkipped("m1ABC", "not recording Valorant"), Parse("Squad moment m1ABC skipped: not recording Valorant"));
        // The moment line before it still reads as before.
        Assert.IsType<MomentDetected>(Parse("Detected kill in Valorant: ENTERRO > X"));
        Assert.Equal(new MontageProgress(40), Parse("Montage progress 40%"));
        Assert.Equal(new HighlightSaved(@"C:\Videos\Clip_Engine\Valorant\Valorant_2026-11-03_21-04-05_squad.mp4", 12.5),
                     Parse(@"Highlight saved (12.5 MB) to C:\Videos\Clip_Engine\Valorant\Valorant_2026-11-03_21-04-05_squad.mp4"));
    }

    [Fact]
    public void MetadataCarriesTheSquadFields()
    {
        var m = ClipMetadataFile.Parse("""{"game":"Valorant","trigger":"squad","seconds":15,"utc_start":1762142400123,"squad_moment":"m1","events":[]}""")!;
        Assert.Equal(1762142400123, m.UtcStart);
        Assert.Equal("m1", m.SquadMoment);
        Assert.True(m.IsSquadView);
        var plain = System.Text.Json.JsonSerializer.Serialize(new ClipMetadata(), ClipMetadataJson.Default.ClipMetadata);
        Assert.DoesNotContain("utc_start", plain);
        Assert.DoesNotContain("squad_moment", plain);
    }
}
