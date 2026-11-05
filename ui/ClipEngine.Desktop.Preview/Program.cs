using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClipEngine.Desktop;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Cloud;
using ClipEngine.Desktop.Services.Squad;
using ClipEngine.Desktop.Services.Stats;
using SquadView = ClipEngine.Desktop.Services.Squad.SquadView;
using ClipEngine.Desktop.ViewModels;
using ClipEngine.Desktop.Views;

// Usage: ClipEngine.Desktop.Preview <output folder>
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "screenshots");
Directory.CreateDirectory(output);

AppBuilder.Configure<App>()
          .UseSkia()
          .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
          .WithInterFont()
          .SetupWithoutStarting();

var vm = new MainViewModel(live: false);
Sample.Fill(vm);

var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
window.Show();

void Settle()
{
    // Let layout, bindings and the page cross-fade finish.
    for (var i = 0; i < 40; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

void Save(Avalonia.Controls.Window target, string name)
{
    Settle();
    var frame = target.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered");
    var path = Path.Combine(output, name);
    frame.Save(path);
    Console.WriteLine(path);
}

void Capture(Page page, string name)
{
    vm.SelectedNav = vm.Nav.First(n => n.Page == page);
    Save(window, name);
}

Capture(Page.Home, "1-home.png");
Capture(Page.Clips, "2-clips.png");
Capture(Page.Games, "3-games.png");
Capture(Page.Settings, "4-settings.png");
Capture(Page.Records, "11-records.png");

// A squad of four, two playing with you, and the moments in its shared library.
SampleSquad.Fill(vm);
window.Height = 1500;
Capture(Page.Squad, "17-squad.png");
window.Height = 900;
var together = SampleSquad.WatchTogether(vm);
vm.ShowPage(together);
Save(window, "18-watch-together.png");
SampleSquad.FillCloud(vm);
Capture(Page.Cloud, "19-cloud.png");

// Friends, two of them playing, one with a squad to join, and a request.
var friends = vm.Friends;
friends.ShowSample("A1B2-C3D4",
[
    friends.SampleFriend("K1A1K1A1", "Kai", "kai", true, "The Finals", "N1GHT5HF7Q4K2M9XW3BD", TimeSpan.FromMinutes(95), TimeSpan.FromMinutes(42)),
    friends.SampleFriend("M1RA0000", "Mira", "mira", true, "Overwatch", null, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(12)),
    friends.SampleFriend("R00K0000", "Rook", null, false, "", null),
    friends.SampleFriend("VEX00000", "Vex", "vexed", true, "", null, TimeSpan.FromMinutes(7)),
],
[friends.SampleRequest("N0VA0000", "Nova", "nova")]);
vm.Invite.ShowSample();
Capture(Page.Friends, "20-friends.png");

// Ultra: its badge, themes, 4K and the credits.
vm.Plan.ShowSample(Plan.Ultra, new Credits(["Mira", "Enterro"], ["Kai the Clipper", "Rook", "Vex"]));
window.Height = 1300;
Capture(Page.Settings, "21-plans.png");
// A Content Creator key from the key maker: its badge, the reason, the credits and the direct line.
vm.Plan.ShowSample(Plan.Ultra, new Credits(["Mira"], ["Kai the Clipper", "Rook"]) { Creator = ["Enterro"], Exclusive = ["Nova"] });
vm.Plan.Accept("preview", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Ultra, "CEK-7Q4K…M9XW", DateTimeOffset.UtcNow.AddDays(90), true,
    Role: "creator", RoleLabel: "Content Creator", Perks: ["contact", "bugs"], CreditsAllowed: true, Reason: "Streams Clip Engine every week"));
vm.Plan.ShowSampleMessages(
[
    new ContactMessage(2, "bug", "The side panel flickers when a game goes fullscreen.", DateTimeOffset.UtcNow.AddHours(-3), null, null),
    new ContactMessage(1, "message", "Could clips upload straight to YouTube?", DateTimeOffset.UtcNow.AddDays(-1),
                       "Yes! It's planned for the next update.", DateTimeOffset.UtcNow.AddHours(-20)),
]);
window.Height = 1700;
Capture(Page.Settings, "22-creator-key.png");
window.Height = 900;
vm.Plan.ShowSample(Plan.Free, new Credits([], []));

// The stats overlay's settings, on a PC that hasn't allowed frame timing yet, and the overlay over a game.
vm.Settings.OnFrameTimingChecked(FrameTiming.NeedsPermission);
vm.Settings.StatsOverlayOn = true;
vm.SelectedNav = vm.Nav.First(n => n.Page == Page.Settings);
Settle();
var statsHeading = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().First(t => t.Text == "STATS OVERLAY");
var settingsScroll = statsHeading.GetVisualAncestors().OfType<Avalonia.Controls.ScrollViewer>().First();
if (statsHeading.TranslatePoint(new Point(0, 0), (Visual)settingsScroll.Content!) is { } headingAt)
    settingsScroll.Offset = new Vector(0, headingAt.Y - 60);
Save(window, "22-stats-settings.png");
vm.StatsOverlay.Show(new GameStats
{
    Frames = FrameTiming.Reading, Fps = 162.4, OnePercentLow = 118.7, CpuPercent = 34, GpuPercent = 91, GpuCelsius = 71,
    VramUsed = (long)(7.4 * (1L << 30)), VramTotal = 12L << 30, RamUsed = (long)(14.2 * (1L << 30)), RamTotal = 32L << 30,
}, vm.DesktopSettings.StatsOverlay.Choice);
var statsOverlay = new StatsOverlayWindow
{
    DataContext = vm.StatsOverlay,
    SizeToContent = Avalonia.Controls.SizeToContent.Manual,
    Width = 1280,
    Height = 720,
    Background = new ImageBrush(SampleFrame()) { Stretch = Stretch.UniformToFill },
};
statsOverlay.Show();
Save(statsOverlay, "23-stats-overlay.png");

// The audio tracks with Discord muted and the mic turned down, voice clips on, and a half-hour replay.
vm.Settings.MicTrack.Load(true, 80, false);
vm.Settings.DiscordTrack.Load(true, 100, true);
vm.Settings.AppsTrack.Load(true, 45, false);
vm.Settings.BufferSeconds = 1800;
vm.Settings.VoiceClips = true;
vm.Settings.VoicePhrasesText = "clip that, save the last minute 60";
window.Height = 2400;
Capture(Page.Settings, "22-audio-voice.png");
window.Height = 900;

// The player, opened on the newest clip partway through.
vm.SelectedNav = vm.Nav.First(n => n.Page == Page.Clips);
Settle();
vm.OpenPlayer(vm.Clips[0]).ShowAt(17.6, playing: true);
Save(window, "5-player.png");

// Trimming that clip down to its best moments.
vm.OpenPlayer(vm.Clips[0]).ShowTrim(4.5, 27.5);
Save(window, "7-trim.png");

// The editor on that clip: cut into parts, one cut out, the second part picked.
vm.OpenEditor(vm.Clips[0], vm.OpenPlayer(vm.Clips[0]))!.ShowSample();
Save(window, "22-editor.png");

vm.OpenPlayer(vm.Clips[0]).ShowCheck();
Save(window, "13-check-detection.png");

// How a kill was found: the killfeed counted it and the score line agreed.
var why = vm.OpenPlayer(vm.Clips[0]);
why.ShowAt(17.6, playing: false);
why.ShowWhy(2);
Save(window, "14-why-this-kill.png");

// The Clips page grouped by match.
vm.ShowPage(vm.ClipsPage);
vm.ShowMatches = true;
Save(window, "8-matches.png");
vm.ShowMatches = false;

// Detection Studio, testing the kill rule on a frame from that clip.
var studio = vm.OpenStudio(clip: vm.Clips[0], seconds: 12.4);
studio.ShowSample(SampleFrame(), "ENTERRO ELIMINATED VEX", matched: true);
studio.ShowSampleTools();
Save(window, "9-studio.png");

// Testing every rule on the clips marked in Check detection, with what they missed.
studio.ShowSampleTab(2);
Save(window, "15-studio-marked-clips.png");

// The score line rule's fine-tuning, and how it works with the killfeed.
studio.ShowSampleTab(0);
studio.SelectRule("the_finals.kill.score_line");
studio.ShowFineTune = true;
Settle();
window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().First(v => v.Name == "RuleEditor").ScrollToEnd();
Save(window, "16-studio-fine-tune.png");
studio.ShowFineTune = false;

// The side panel as it sits on the edge of the screen.
var panel = new SidePanelWindow { DataContext = vm, Width = 344, Height = 900 };
panel.Show();
Save(panel, "6-side-panel.png");

// A long recording running, with moments still being marked.
vm.SimulateEngine(EngineState.Running, new LongRecordingStarted("The Finals"));
vm.ShowPage(vm.Home);
Save(window, "12-long-recording.png");
vm.SimulateEngine(EngineState.Running, new LongRecordingStopped(754, null));

// A supporter colour theme.
var ember = AccentThemes.Find("ember");
AccentThemes.ApplyAccent(ember);
vm.ShowPage(vm.Home);
Save(window, "10-theme-ember.png");

// A frame of the sample game with a kill message in its feed, as a clip would show it.
static Bitmap SampleFrame()
{
    var thumb = new Bitmap(Path.Combine(AppContext.BaseDirectory, "SampleThumbnails", "finals_1.png"));
    var frame = new RenderTargetBitmap(new PixelSize(1280, 720));
    using (var context = frame.CreateDrawingContext())
    {
        context.DrawImage(thumb, new Rect(0, 0, 1280, 720));
        var face = new Typeface("Inter", FontStyle.Normal, FontWeight.Bold);
        var lines = new[] { ("ENTERRO", "ELIMINATED", "VEX", 98.0), ("MIRA", "REVIVED", "ENTERRO", 132.0) };
        foreach (var (who, what, whom, y) in lines)
        {
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#99000000")), null, new Rect(846, y - 4, 400, 30), 4, 4);
            var x = 858.0;
            foreach (var (text, color) in new[] { (who, "#FFFFFF"), (what, "#FF4D7A"), (whom, "#FFFFFF") })
            {
                var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                                                  FlowDirection.LeftToRight, face, 17, new SolidColorBrush(Color.Parse(color)));
                context.DrawText(formatted, new Point(x, y));
                x += formatted.Width + 12;
            }
        }
    }
    return frame;
}

internal static class Sample
{
    // Some of the games installed from different stores, with art: the sample frames stand in for store banners, and
    // a few made-up shapes for program icons.
    private static void SampleGameArt(MainViewModel vm)
    {
        var installed = new (string Game, string Store)[]
        {
            ("The Finals", "Steam"), ("Overwatch", "Battle.net"), ("Battlefield 6", "EA app"), ("Counter-Strike 2", "Steam"),
            ("Valorant", "Riot"), ("Fortnite", "Epic Games"), ("Marvel Rivals", "Steam"), ("Roblox", "Roblox"),
        };
        vm.ApplyGameScan(new GameScan(installed.ToDictionary(g => g.Game, g => new GameInstall(g.Game, g.Store, null, null, null)),
                                      null, true));
        var dir = Path.Combine(AppContext.BaseDirectory, "SampleThumbnails");
        Bitmap Banner(string file)
        {
            using var stream = File.OpenRead(Path.Combine(dir, file + ".png"));
            return Bitmap.DecodeToWidth(stream, 460);
        }
        GameArt.Set("The Finals", new GameArtwork(Banner("finals_2"), null));
        GameArt.Set("Overwatch", new GameArtwork(Banner("overwatch_1"), null));
        GameArt.Set("Battlefield 6", new GameArtwork(Banner("bf6_1"), null));
        GameArt.Set("Counter-Strike 2", new GameArtwork(Banner("bf6_2"), null));
        GameArt.Set("Marvel Rivals", new GameArtwork(Banner("overwatch_2"), null));
        GameArt.Set("Valorant", new GameArtwork(null, Icon("#FF4655", 0)));
        GameArt.Set("Fortnite", new GameArtwork(null, Icon("#9D4DFF", 1)));
        GameArt.Set("Roblox", new GameArtwork(null, Icon("#E8E8E8", 2)));
    }

    private static Bitmap Icon(string color, int shape)
    {
        var icon = new RenderTargetBitmap(new PixelSize(128, 128));
        using (var context = icon.CreateDrawingContext())
        {
            var fill = new SolidColorBrush(Color.Parse(color));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                Point[] points = shape switch
                {
                    0 => [new(16, 24), new(64, 104), new(112, 24), new(84, 24), new(64, 62), new(44, 24)],
                    1 => [new(36, 12), new(100, 12), new(92, 40), new(64, 40), new(60, 56), new(84, 56), new(76, 84), new(54, 84), new(46, 116), new(18, 116)],
                    _ => [new(30, 8), new(120, 30), new(98, 120), new(8, 98)],
                };
                c.BeginFigure(points[0], true);
                foreach (var pt in points.Skip(1)) c.LineTo(pt);
                c.EndFigure(true);
            }
            context.DrawGeometry(fill, null, g);
            if (shape == 2) context.DrawRectangle(new SolidColorBrush(Color.Parse("#16181C")), null, new Rect(52, 52, 24, 24));
        }
        return icon;
    }

    public static void Fill(MainViewModel vm)
    {
        var now = DateTime.Now;
        vm.SampleTimelines = Timelines(now);
        vm.SetSupportedGames(["The Finals", "Overwatch", "Battlefield 6", "Counter-Strike 2", "Dota 2", "League of Legends",
                              "Rocket League", "Valorant", "Fortnite", "Apex Legends", "PUBG", "Marvel Rivals",
                              "Team Fortress 2", "Rainbow Six Siege", "Call of Duty", "Delta Force", "Deadlock",
                              "Halo Infinite", "Halo MCC", "Battlefield 2042", "Battlefield V", "Battlefield 1",
                              "Battlefield 4", "Paladins", "Splitgate", "Roblox", "Minecraft"]);
        SampleGameArt(vm);

        var clips = new (string Game, string Thumb, double MinutesAgo, int Seconds, long Mb)[]
        {
            ("The Finals", "finals_1", 4, 30, 212),
            ("The Finals", "finals_2", 26, 30, 198),
            ("Overwatch", "overwatch_1", 95, 30, 176),
            ("Battlefield 6", "bf6_1", 170, 45, 301),
            ("Overwatch", "overwatch_2", 60 * 26, 30, 181),
            ("Battlefield 6", "bf6_2", 60 * 30, 30, 224),
        };
        var files = clips.Select((c, i) =>
        {
            var time = now.AddMinutes(-c.MinutesAgo);
            var name = $"{c.Game}_{time:yyyy-MM-dd_HH-mm-ss}.mp4";
            return new ClipFile(Path.Combine(@"C:\Users\You\Videos\Clip_Engine", c.Game, name), c.Game, time,
                                c.Mb * 1024 * 1024, TimeSpan.FromSeconds(c.Seconds), Moments(i, c.Game, c.Seconds, time));
        }).ToList();
        vm.SetClips(files);

        var dir = Path.Combine(AppContext.BaseDirectory, "SampleThumbnails");
        for (var i = 0; i < clips.Length; i++)
            vm.Clips[i].Thumbnail = new Bitmap(Path.Combine(dir, clips[i].Thumb + ".png"));

        vm.SimulateEngine(EngineState.Running,
            new EngineStarted("0.2.0"),
            new SaveHotkey("F8", 30),
            new GameDetected("The Finals"),
            new RecordingStarted("The Finals", 2560, 1440, 165, "HEVC", 60, "NVIDIA HEVC Encoder MFT"),
            new MomentDetected("kill", "The Finals", "ELIMINATED Vortex"),
            new MomentDetected("assist", "The Finals", "ASSIST Kintsugi"),
            new MomentDetected("kill", "The Finals", "ELIMINATED Rook"),
            new MomentDetected("revive", "The Finals", "REVIVED Mochi"),
            new ScoreboardRead("The Finals", new Dictionary<string, int> { ["kills"] = 9, ["assists"] = 4, ["deaths"] = 3, ["revives"] = 2 }));

        vm.Activity.Clear();
        vm.AddActivity(ActivityKind.Moment, "Kill in The Finals", now.AddMinutes(-6));
        vm.AddActivity(ActivityKind.Moment, "Assist in The Finals", now.AddMinutes(-5));
        vm.AddActivity(ActivityKind.Moment, "Kill in The Finals", now.AddMinutes(-4));
        vm.AddActivity(ActivityKind.Info, "Engine started", now.AddMinutes(-41));
        vm.AddActivity(ActivityKind.Info, "The Finals detected", now.AddMinutes(-39));
        vm.AddActivity(ActivityKind.Recording, "Recording The Finals at 2560×1440, 165 fps", now.AddMinutes(-39));
        vm.AddActivity(ActivityKind.Clip, "Saved a 30 s clip (198.0 MB)", now.AddMinutes(-26));
        vm.AddActivity(ActivityKind.Clip, "Saved a 30 s clip (212.0 MB)", now.AddMinutes(-4));
        vm.ShowToast("Clip saved · 30 s · 212.0 MB");
    }

    // A few weeks of recordings, as the engine's timelines would have them, for the Records page.
    private static IReadOnlyList<Timeline> Timelines(DateTime now)
    {
        var random = new Random(7);
        var games = new (string Game, int MaxKills, bool Board)[]
        {
            ("Valorant", 26, true), ("The Finals", 14, true), ("Overwatch", 31, true), ("Counter-Strike 2", 29, true),
            ("Apex Legends", 9, false), ("Battlefield 6", 38, true),
        };
        var timelines = new List<Timeline>();
        for (var day = 1; day <= 24; day += 1 + random.Next(2))
        {
            var (game, maxKills, board) = games[random.Next(games.Length)];
            var start = now.Date.AddDays(-day).AddHours(18 + random.Next(4));
            var lines = new List<string> { $$"""{"session": "{{start:yyyy-MM-dd'T'HH:mm:ss}}", "game": "{{game}}", "version": 1}""" };
            var t = 30;
            for (var match = 1; match <= 1 + random.Next(3); match++)
            {
                lines.Add($$"""{"t": {{t}}, "match": {{match}}, "type": "match_start", "text": "MATCH START"}""");
                int kills = 0, deaths = 0, assists = 0;
                var target = 3 + random.Next(maxKills);
                while (kills < target)
                {
                    t += 5 + random.Next(60);
                    var roll = random.Next(10);
                    var type = roll < 6 ? "kill" : roll < 8 ? "death" : "assist";
                    if (type == "kill") kills++;
                    else if (type == "death") deaths++;
                    else assists++;
                    lines.Add($$"""{"t": {{t}}, "match": {{match}}, "type": "{{type}}", "text": ""}""");
                    if (board && random.Next(4) == 0)
                        lines.Add($$"""{"t": {{t}}, "match": {{match}}, "source": "api", "stats": {"kills": {{kills}}, "deaths": {{deaths}}, "assists": {{assists}} """ + "}}");
                }
                t += 60;
                lines.Add($$"""{"t": {{t}}, "match": {{match}}, "type": "match_end", "text": "{{(random.Next(5) < 3 ? "VICTORY" : "DEFEAT")}}"}""");
                t += 120;
            }
            if (Timeline.Parse(lines) is { } timeline) timelines.Add(timeline);
        }
        return timelines;
    }

    // How the newest clip's kills were found, as the engine saves it: the killfeed counted each
    // and the score line under the crosshair agreed, except one only the score line saw.
    private static List<ClipWitness>? FoundBy(int index, string type, string text)
    {
        if (index != 0 || type != "kill") return null;
        var name = text.Split(' ').Last().ToUpperInvariant();
        var feed = new ClipWitness
        {
            Rule = "the_finals.kill.killfeed", Text = $"SMEDC MOCHI {name}", Counted = true, Box = [0.7, 0.03, 0.3, 0.2],
        };
        var score = new ClipWitness
        {
            Rule = "the_finals.kill.score_line", Text = $"ELIMINATED {name} ELIMINATION + 250", Offset = 0.3, Counted = false,
            Box = [0.3, 0.52, 0.4, 0.24],
        };
        if (name == "HAZE") return [new ClipWitness { Rule = score.Rule, Text = score.Text, Box = score.Box }];
        return [feed, score];
    }

    // Detected moments for some of the sample clips, like the engine writes into each clip.
    // The first four were recorded with match tracking; the two oldest are from before it.
    private static ClipMetadata? Moments(int index, string game, int seconds, DateTime saved)
    {
        (string Type, double Time, string Text)[] events = index switch
        {
            0 => [("kill", 6.2, "ELIMINATED Vortex"), ("assist", 12.8, "ASSIST Kintsugi"), ("kill", 19.5, "ELIMINATED Rook"),
                  ("revive", 23.1, "REVIVED Mochi"), ("kill", 26.4, "ELIMINATED Haze")],
            1 => [("match_start", 2.0, "ROUND 1"), ("kill", 14.0, "ELIMINATED Static"), ("death", 24.5, "ELIMINATED BY Nova")],
            2 => [("kill", 9.5, "ELIMINATION Tracer"), ("kill", 11.2, "ELIMINATION Mercy"), ("highlight", 12.0, "DOUBLE KILL"),
                  ("match_end", 28.0, "VICTORY")],
            3 => [("revive", 21.0, "REVIVED squadmate"), ("death", 33.0, "KILLED BY sniper")],
            _ => [],
        };
        if (events.Length == 0) return null;
        // One recording of The Finals that started 40 minutes ago, and one each for the others.
        var sessionStart = index <= 1 ? DateTime.Now.AddMinutes(-40) : saved.AddMinutes(-12);
        var metadata = new ClipMetadata
        {
            Game = game,
            Trigger = index == 0 || index == 2 ? "auto" : "hotkey",
            Seconds = seconds,
            Session = sessionStart.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            SessionSeconds = Math.Max(0, (saved.AddSeconds(-seconds) - sessionStart).TotalSeconds),
            Match = index switch { 0 => 2, 1 => 1, _ => 1 },
            Favorite = index == 2,
            Events = events.Select(e => new ClipEvent { Type = e.Type, Time = e.Time, Text = e.Text, FoundBy = FoundBy(index, e.Type, e.Text) }).ToList(),
        };
        metadata.Title = string.Join(", ", metadata.Events.GroupBy(e => e.Type)
            .Select(g => EventKinds.Count(g.Key, g.Count()).ToLowerInvariant()));
        return metadata;
    }
}

internal sealed class PreviewChannel : ISquadChannel
{
    public bool Connected { get; private set; }
    public event Action<System.Text.Json.Nodes.JsonObject>? Received;
    public event Action<bool>? ConnectedChanged;
    public event Action? Rejected { add { } remove { } }
    public void Start()
    {
        Connected = true;
        ConnectedChanged?.Invoke(true);
    }
    public void Send(System.Text.Json.Nodes.JsonObject message) { }
    public void Receive(string json) => Received?.Invoke((System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(json)!);
    public void Dispose() { }
}

internal static class SampleSquad
{
    private const string Server = "https://clip-engine.example.workers.dev";
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Fill(MainViewModel vm)
    {
        vm.DesktopSettings.OnlineServer = Server;
        var settings = vm.DesktopSettings.Squad;
        settings.SquadId = "N1GHT5HF";
        settings.SquadName = "Night shift";
        settings.MemberId = "pENTERRO000";
        settings.PlayerName = "Enterro";
        settings.Token = "preview";
        settings.JoinOnDiscord = true;
        vm.Squad.PlayerName = "Enterro";

        SquadView View(string member, string name, double seconds, bool uploaded, bool mine = false) =>
            new(member, name, 0, seconds, uploaded, mine);
        var moments = new[]
        {
            new SquadMoment("m1", "The Finals", "kill", Now - 4 * 60_000, "pENTERRO000", "Enterro", "ENTERRO ELIMINATED VORTEX", true,
                            [View("pENTERRO000", "Enterro", 30, true, true), View("pKAI0000000", "Kai", 15, true), View("pMIRA000000", "Mira", 15, false)]),
            new SquadMoment("m2", "The Finals", "revive", Now - 9 * 60_000, "pMIRA000000", "Mira", "MIRA REVIVED ENTERRO", false,
                            [View("pMIRA000000", "Mira", 15, true), View("pENTERRO000", "Enterro", 15, true, true)]),
            new SquadMoment("m3", "The Finals", "highlight", Now - 14 * 60_000, "pKAI0000000", "Kai", "Triple kill", false,
                            [View("pKAI0000000", "Kai", 30, true), View("pENTERRO000", "Enterro", 15, true, true), View("pMIRA000000", "Mira", 15, true)]),
            new SquadMoment("m4", "Overwatch", "match_end", Now - 26 * 60 * 60_000, "pROOK000000", "Rook", "VICTORY", false,
                            [View("pROOK000000", "Rook", 20, false), View("pENTERRO000", "Enterro", 20, false, true)]),
        };
        var channel = new PreviewChannel();
        vm.Squad.ConnectForPreview(channel, moments);
        channel.Receive("""
            {"t":"welcome","squad":{"id":"N1GHT5HF","name":"Night shift","invite":"N1GHT5HF-7Q4K2M9XW3BD","you":"pENTERRO000",
             "storage":{"used":1288490188,"limit":5368709120},
             "members":[{"id":"pENTERRO000","name":"Enterro","owner":true,"online":true},{"id":"pKAI0000000","name":"Kai","owner":false,"online":true},
                        {"id":"pMIRA000000","name":"Mira","owner":false,"online":true},{"id":"pROOK000000","name":"Rook","owner":false,"online":false}]},
             "presences":[{"member":"pENTERRO000","game":"The Finals"},{"member":"pKAI0000000","game":"The Finals"},{"member":"pMIRA000000","game":"The Finals"}],
             "groups":[{"game":"The Finals","members":["pENTERRO000","pKAI0000000","pMIRA000000"]}]}
            """);
        vm.Squad.Sync!.OnRecordingStarted("The Finals");
    }

    public static SquadPlayerViewModel WatchTogether(MainViewModel vm)
    {
        var row = vm.Squad.Moments[0];
        var dir = Path.Combine(AppContext.BaseDirectory, "SampleThumbnails");
        var views = row.Moment.Views.Select((v, i) => (v with { UtcStartMs = row.Moment.At - 10_000 - i * 1500 }, $"view{i}.mp4")).ToList();
        var player = new SquadPlayerViewModel(vm, vm.Squad, row, views, vm.Squad);
        string[] stills = ["finals_1", "finals_2", "finals_1"];
        for (var i = 0; i < player.Tiles.Count; i++)
            player.Tiles[i].Still = new Avalonia.Media.Imaging.Bitmap(Path.Combine(dir, stills[i % stills.Length] + ".png"));
        return player;
    }

    public static void FillCloud(MainViewModel vm)
    {
        var hour = 3_600_000L;
        vm.Cloud.ShowSample(Server, new CloudAccount("A1B2C3D4", "Enterro", "enterro", "pro", "Pro", 671_088_640, 107_374_182_400, 4, BadgeLabel: "Pro"),
        [
            new CloudClip("q7k2mx", "A1B2C3D4q7k2mx", null, "3 kills, 1 revive", "The Finals", Now - hour / 10, 30, 41_943_040, true, true, "public", true, 128),
            new CloudClip("b4nt9w", "A1B2C3D4b4nt9w", null, "Squad highlight: Kai's triple kill", "The Finals", Now - hour / 2, 18, 22_020_096, true, true, "public", false, 46),
            new CloudClip("x2p8rd", "A1B2C3D4x2p8rd", null, "2 kills", "Overwatch", Now - 26 * hour, 30, 38_797_312, true, true, "unlisted", false, 9),
            new CloudClip("h6w3zc", "A1B2C3D4h6w3zc", null, "Victory", "Battlefield 6", Now - 50 * hour, 45, 60_817_408, true, true, "private", false, 0),
        ]);
    }
}
