using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

// Lines below are in the exact format src/core/Log.cpp writes.
public class EngineLogParserTests
{
    private static EngineEvent? Parse(string message, string level = "INFO ") =>
        EngineLogParser.ParseLine($"2026-10-26 21:04:05.123 {level} {message}") is { } line
            ? EngineLogParser.ParseEvent(line)
            : null;

    [Fact]
    public void ReadsTimeAndLevel()
    {
        var line = EngineLogParser.ParseLine("2026-10-26 21:04:05.123 WARN  Encoder is falling behind: 3 frames dropped so far");
        Assert.NotNull(line);
        Assert.Equal(LogLevel.Warn, line.Level);
        Assert.Equal(new DateTime(2026, 10, 26, 21, 4, 5, 123), line.Time);
        Assert.Equal("Encoder is falling behind: 3 frames dropped so far", line.Message);
    }

    [Fact]
    public void StudioTools()
    {
        var w = Assert.IsType<ScanWitness>(Parse("Scan witness the_finals.kill.score_line +0.49s agreed: ELIMINATION + 250"));
        Assert.Equal(("the_finals.kill.score_line", 0.49, false, "ELIMINATION + 250"), (w.Rule, w.Offset, w.Counted, w.Text));
        Assert.True(Assert.IsType<ScanWitness>(Parse("Scan witness the_finals.kill.killfeed -0.20s counted: SMEDC CAPN")).Counted);
        var t = Assert.IsType<TrackRead>(Parse("Track at 15.25: 1 | ELIMINATED MRBEAN ELIMINATION + 250"));
        Assert.Equal((15.25, 1, "ELIMINATED MRBEAN ELIMINATION + 250"), (t.Seconds, t.Matches, t.Text));
        Assert.Equal("", Assert.IsType<TrackRead>(Parse("Track at 3.00: 0 | ")).Text);
        Assert.Equal(40, Assert.IsType<TrackProgress>(Parse("Track progress 40%")).Percent);
        Assert.Equal(120, Assert.IsType<TrackDone>(Parse("Track done: 120 reads")).Reads);
        Assert.Equal(@"C:\x\sounds\the_finals_kill_1.wav",
                     Assert.IsType<SoundSaved>(Parse(@"Sound saved to C:\x\sounds\the_finals_kill_1.wav")).Path);
    }

    [Fact]
    public void IgnoresOtherText() => Assert.Null(EngineLogParser.ParseLine("Clip Engine is already running."));

    [Fact]
    public void Recording()
    {
        var e = Assert.IsType<RecordingStarted>(Parse("Recording The Finals: 2560x1440 at 165 fps, HEVC 60 Mbps on NVIDIA HEVC Encoder MFT"));
        Assert.Equal(("The Finals", 2560, 1440, 165, "HEVC", 60, "NVIDIA HEVC Encoder MFT"),
                     (e.Label, e.Width, e.Height, e.Fps, e.Codec, e.Mbps, e.Encoder));
    }

    [Fact]
    public void StoppedRecording() =>
        Assert.Equal("Desktop", Assert.IsType<RecordingStopped>(Parse("Stopped recording Desktop")).Label);

    [Fact]
    public void GameFocus()
    {
        Assert.Equal("Halo Infinite", Assert.IsType<GameAway>(
            Parse("Halo Infinite isn't in front, so the recording shows Game Not Found until you're back")).Game);
        Assert.Equal("Halo Infinite", Assert.IsType<GameBack>(Parse("Back in Halo Infinite, recording it again")).Game);
    }

    [Fact]
    public void ClipSaved()
    {
        var e = Assert.IsType<ClipSaved>(Parse(@"Saved 30s clip (212.4 MB) to C:\Users\me\Videos\Clip_Engine\The Finals\The Finals_2026-10-26_21-04-05.mp4"));
        Assert.Equal(30, e.Seconds);
        Assert.Equal(212.4, e.Megabytes);
        Assert.EndsWith("21-04-05.mp4", e.Path);
    }

    [Fact]
    public void GamesAndHotkey()
    {
        Assert.Equal(["The Finals", "Overwatch", "Battlefield 6"],
                     Assert.IsType<SupportedGames>(Parse("Waiting for a supported game: The Finals, Overwatch, Battlefield 6")).Names);
        var hotkey = Assert.IsType<SaveHotkey>(Parse("Press Alt+F10 to save the last 45 seconds"));
        Assert.Equal(("Alt+F10", 45), (hotkey.Hotkey, hotkey.ClipSeconds));
        Assert.Equal("Overwatch", Assert.IsType<GameDetected>(Parse("Overwatch detected")).Game);
        Assert.Equal("Overwatch", Assert.IsType<GameClosed>(Parse("Overwatch closed")).Game);
        Assert.Equal("0.2.0", Assert.IsType<EngineStarted>(Parse("Clip Engine 0.2.0 starting")).Version);
    }

    [Fact]
    public void ErrorsAreNotEvents() => Assert.Null(Parse("Overwatch detected", "ERROR"));

    [Fact]
    public void Moment()
    {
        var e = Assert.IsType<MomentDetected>(Parse("Detected kill in The Finals: ELIMINATED Vortex"));
        Assert.Equal(("kill", "The Finals", "ELIMINATED Vortex"), (e.Type, e.Game, e.Text));
    }

    [Fact]
    public void Export()
    {
        Assert.Equal(42, Assert.IsType<ExportProgress>(Parse("Export progress 42%")).Percent);
        var done = Assert.IsType<Exported>(Parse(@"Exported 9.4 MB to C:\Users\You\AppData\Local\Clip_Engine\Shared\a_10MB.mp4"));
        Assert.Equal(9.4, done.Megabytes);
        Assert.EndsWith("a_10MB.mp4", done.Path);
    }

    [Fact]
    public void Trimmed()
    {
        var e = Assert.IsType<Trimmed>(Parse(@"Trimmed 12.48s from 3.97s to C:\Videos\Clip_Engine\The Finals\The Finals_2026_trim.mp4"));
        Assert.Equal((12.48, 3.97, @"C:\Videos\Clip_Engine\The Finals\The Finals_2026_trim.mp4"), (e.Seconds, e.From, e.Path));
    }

    [Fact]
    public void LongRecording()
    {
        Assert.Equal("F9", Assert.IsType<RecordHotkey>(Parse("Press F9 to start or stop a long recording")).Hotkey);
        Assert.Equal("Rainbow Six Siege",
                     Assert.IsType<LongRecordingStarted>(Parse("Long recording started in Rainbow Six Siege; press Alt+F9 again to stop it")).Game);
        var stopped = Assert.IsType<LongRecordingStopped>(Parse("Long recording stopped after 3725s"));
        Assert.Equal((3725.0, (string?)null), (stopped.Seconds, stopped.Reason));
        Assert.Equal("the drive is nearly full",
                     Assert.IsType<LongRecordingStopped>(Parse("Long recording stopped after 12s because the drive is nearly full")).Reason);
        var saved = Assert.IsType<LongRecordingSaved>(Parse(@"Saved 3725s long recording (8123.4 MB) to C:\Videos\Clip_Engine\Valorant\Valorant_2026-10-28_20-01-02_long.mp4"));
        Assert.Equal((3725.0, 8123.4, @"C:\Videos\Clip_Engine\Valorant\Valorant_2026-10-28_20-01-02_long.mp4"),
                     (saved.Seconds, saved.Megabytes, saved.Path));
    }

    [Fact]
    public void Scoreboard()
    {
        var read = Assert.IsType<ScoreboardRead>(Parse("Scoreboard in Counter-Strike 2: kills 12, deaths 3, assists 4, mvps 2, score 31"));
        Assert.Equal("Counter-Strike 2", read.Game);
        Assert.Equal(12, read.Stats["kills"]);
        Assert.Equal(31, read.Stats["score"]);
        Assert.Equal(5, read.Stats.Count);
    }
}
