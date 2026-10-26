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
}
