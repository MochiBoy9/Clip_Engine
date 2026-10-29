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
    public void AutomaticClipSettingsRoundTrip()
    {
        var settings = EngineSettings.Load(_file);
        Assert.True(settings.Detection);
        Assert.Equal(EngineSettings.AllEvents, settings.AutoClipEvents);

        settings.AutoClip = false;
        settings.AutoClipEvents = ["kill", "revive"];
        settings.ClipBeforeSeconds = 15;
        settings.ClipAfterSeconds = 3;
        settings.Save(_file);

        var reloaded = EngineSettings.Load(_file);
        Assert.False(reloaded.AutoClip);
        Assert.True(reloaded.Detection);
        Assert.Equal(["kill", "revive"], reloaded.AutoClipEvents);
        Assert.Equal((15, 3), (reloaded.ClipBeforeSeconds, reloaded.ClipAfterSeconds));
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
}
