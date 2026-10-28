using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClipEngine.Desktop;
using ClipEngine.Desktop.Services;
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
window.ForceAnimated(false);

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

// The player, opened on the newest clip partway through.
vm.SelectedNav = vm.Nav.First(n => n.Page == Page.Clips);
Settle();
vm.OpenPlayer(vm.Clips[0]).ShowAt(17.6, playing: true);
Save(window, "5-player.png");

// Trimming that clip down to its best moments.
vm.OpenPlayer(vm.Clips[0]).ShowTrim(4.5, 27.5);
Save(window, "7-trim.png");

// The Clips page grouped by match.
vm.ShowPage(vm.ClipsPage);
vm.ShowMatches = true;
Save(window, "8-matches.png");
vm.ShowMatches = false;

// Detection Studio, testing the kill rule on a frame from that clip.
var studio = vm.OpenStudio(clip: vm.Clips[0], seconds: 12.4);
studio.ShowSample(SampleFrame(), "ENTERRO ELIMINATED VEX", matched: true);
Save(window, "9-studio.png");

// The side panel as it sits on the edge of the screen.
var panel = new SidePanelWindow { DataContext = vm, Width = 344, Height = 900 };
panel.Show();
Save(panel, "6-side-panel.png");

// A supporter colour theme.
var ember = AccentThemes.Find("ember");
AccentThemes.ApplyAccent(ember);
AccentThemes.ApplyBackground(ember, window.BlobViolet, window.BlobCyan, window.BlobPink, window.BlobBlue);
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
    public static void Fill(MainViewModel vm)
    {
        var now = DateTime.Now;
        vm.SetSupportedGames(["The Finals", "Overwatch", "Battlefield 6"]);

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
            new RecordingStarted("The Finals", 2560, 1440, 165, "HEVC", 60, "NVIDIA HEVC Encoder MFT"));

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
            Events = events.Select(e => new ClipEvent { Type = e.Type, Time = e.Time, Text = e.Text }).ToList(),
        };
        metadata.Title = string.Join(", ", metadata.Events.GroupBy(e => e.Type)
            .Select(g => EventKinds.Count(g.Key, g.Count()).ToLowerInvariant()));
        return metadata;
    }
}
