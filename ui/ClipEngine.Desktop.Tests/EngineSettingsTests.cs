using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class EngineSettingsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"clip_engine_{Guid.NewGuid():N}.ini");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void KeepsCommentsAndChangesValues()
    {
        File.WriteAllText(_file, """
            # Clip Engine settings.
            output_dir =
            buffer_seconds = 60  # recent gameplay
            codec = auto
            Save_Hotkey = F8
            """);

        var settings = EngineSettings.Load(_file);
        Assert.Equal(60, settings.BufferSeconds);
        Assert.Equal("F8", settings.SaveHotkey);

        settings.BufferSeconds = 120;
        settings.Codec = "hevc";
        settings.SaveHotkey = "Alt+F10";
        settings.Save(_file);

        var lines = File.ReadAllLines(_file);
        Assert.Equal("# Clip Engine settings.", lines[0]);
        Assert.Contains("buffer_seconds = 120", lines);
        Assert.Contains("codec = hevc", lines);
        Assert.Contains("Save_Hotkey = Alt+F10", lines);
        // Settings missing from the file are added at the end.
        Assert.Contains("mic_audio = true", lines);

        var reloaded = EngineSettings.Load(_file);
        Assert.Equal((120, "hevc", "Alt+F10"), (reloaded.BufferSeconds, reloaded.Codec, reloaded.SaveHotkey));
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var settings = EngineSettings.Load(_file);
        Assert.Equal((60, 30, "auto", "F8"), (settings.BufferSeconds, settings.ClipSeconds, settings.Codec, settings.SaveHotkey));
    }

    [Fact]
    public void GameFocusAndDiscordRoundTrip()
    {
        var settings = EngineSettings.Load(_file);
        Assert.True(settings.GameFocus);
        Assert.True(settings.DiscordPresence);

        settings.GameFocus = false;
        settings.DiscordPresence = false;
        settings.Save(_file);
        Assert.Contains("game_focus = false", File.ReadAllLines(_file));

        var reloaded = EngineSettings.Load(_file);
        Assert.False(reloaded.GameFocus);
        Assert.False(reloaded.DiscordPresence);
    }

    [Fact]
    public void AutomaticClipSettingsRoundTrip()
    {
        var settings = EngineSettings.Load(_file);
        Assert.True(settings.Detection);
        Assert.Equal(EngineSettings.AllEvents, settings.AutoClipEvents);

        settings.AutoClip = false;
        settings.AutoClipEvents = ["kill", "revive"];
        settings.ClipBeforeSeconds = 15;
        settings.ClipAfterSeconds = 3;
        Assert.True(settings.ClipLoneDeaths && settings.AutoHighlights);
        settings.ClipLoneDeaths = false;
        settings.AutoHighlights = false;
        settings.Save(_file);
        // Engine settings the app doesn't show are left as they were.
        File.AppendAllText(_file, "moment_clip_seconds = match_end 20/8\n");
        EngineSettings.Load(_file).Save(_file);
        Assert.Contains("moment_clip_seconds = match_end 20/8", File.ReadAllLines(_file));

        var reloaded = EngineSettings.Load(_file);
        Assert.False(reloaded.AutoClip);
        Assert.True(reloaded.Detection);
        Assert.Equal(["kill", "revive"], reloaded.AutoClipEvents);
        Assert.Equal((15, 3), (reloaded.ClipBeforeSeconds, reloaded.ClipAfterSeconds));
        Assert.False(reloaded.ClipLoneDeaths);
        Assert.False(reloaded.AutoHighlights);
    }

    [Fact]
    public void ReadsAndWritesTheReadingQuality()
    {
        Assert.Equal("medium", new EngineSettings().ReadingQuality);
        File.WriteAllText(_file, "reading_quality = Ultra\n");
        var settings = EngineSettings.Load(_file);
        Assert.Equal("ultra", settings.ReadingQuality);
        settings.ReadingQuality = "low";
        settings.Save(_file);
        Assert.Contains("reading_quality = low", File.ReadAllLines(_file));

        File.WriteAllText(_file, "reading_quality = extreme\n");
        Assert.Equal("medium", EngineSettings.Load(_file).ReadingQuality);
    }

    [Fact]
    public void ReadsAndWritesAudioTracks()
    {
        var defaults = EngineSettings.Load(_file);
        Assert.True(defaults.DiscordAudio && defaults.AppsAudio);
        Assert.All(EngineSettings.AudioTracks, t => Assert.Equal(100, defaults.VolumeOf(t)));
        Assert.Empty(defaults.MutedAudio);

        File.WriteAllText(_file, """
            discord_audio = false
            mic_volume = 140%
            game_volume = 900
            radio_volume = 20
            muted_audio = Discord, radio, apps
            """);
        var settings = EngineSettings.Load(_file);
        Assert.False(settings.DiscordAudio);
        Assert.Equal((140, 200, 100), (settings.VolumeOf("mic"), settings.VolumeOf("game"), settings.VolumeOf("apps")));
        Assert.Equal(["discord", "apps"], settings.MutedAudio);
        Assert.True(settings.IsMuted("apps"));

        settings.Volumes["apps"] = 35;
        settings.MutedAudio = ["mic"];
        settings.Save(_file);
        var reloaded = EngineSettings.Load(_file);
        Assert.Equal(35, reloaded.VolumeOf("apps"));
        Assert.Equal(["mic"], reloaded.MutedAudio);
        Assert.Contains("muted_audio = mic", File.ReadAllLines(_file));
    }

    [Fact]
    public void SavesAudioLevelsWithoutTouchingOtherSettings()
    {
        File.WriteAllText(_file, """
            buffer_seconds = 60
            mic_volume = 100
            muted_audio =
            """);
        // Another setting changed in the file meanwhile, such as one saved from the Settings page.
        var levels = new EngineSettings { BufferSeconds = 900, Volumes = new() { ["mic"] = 60 }, MutedAudio = ["game"] };
        levels.SaveAudioLevels(_file);

        var lines = File.ReadAllLines(_file);
        Assert.Contains("buffer_seconds = 60", lines);
        Assert.Contains("mic_volume = 60", lines);
        Assert.Contains("game_volume = 100", lines);
        Assert.Contains("muted_audio = game", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("clip_seconds", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsAndWritesVoiceClips()
    {
        var defaults = EngineSettings.Load(_file);
        Assert.False(defaults.VoiceClips);
        Assert.Equal(("clip that", 0, "medium"), (defaults.VoicePhrases, defaults.VoiceClipSeconds, defaults.VoiceSensitivity));

        File.WriteAllText(_file, "voice_clips = true\nvoice_phrases = clip it, save that 60\nvoice_clip_seconds = 45\nvoice_sensitivity = HIGH\n");
        var settings = EngineSettings.Load(_file);
        Assert.True(settings.VoiceClips);
        Assert.Equal(("clip it, save that 60", 45, "high"), (settings.VoicePhrases, settings.VoiceClipSeconds, settings.VoiceSensitivity));

        // A # would start a comment in the file.
        settings.VoicePhrases = "clip #1, clip that";
        settings.VoiceSensitivity = "low";
        settings.Save(_file);
        var reloaded = EngineSettings.Load(_file);
        Assert.Equal(("clip  1, clip that", "low"), (reloaded.VoicePhrases, reloaded.VoiceSensitivity));
    }

    [Fact]
    public void BufferCanBeHalfAnHour()
    {
        File.WriteAllText(_file, "buffer_seconds = 1800\n");
        Assert.Equal(1800, EngineSettings.Load(_file).BufferSeconds);
    }
}
