using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class PlayerNamesTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"clip_engine_{Guid.NewGuid():N}.ini");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void SplitsTrimsAndDropsTags() =>
        Assert.Equal(["Mochi", "xX Sniper Xx"], PlayerNames.Split(" Mochi#NA1 ,, xX Sniper Xx, mochi "));

    [Fact]
    public void ChangesOnlyItsOwnLine()
    {
        File.WriteAllText(_file, """
            # Clip Engine settings.
            codec = auto
            player_names =   # your names
            detection = true
            """);

        Assert.Empty(PlayerNames.Load(_file));
        PlayerNames.Save(_file, ["Mochi", "Alt"]);

        var lines = File.ReadAllLines(_file);
        Assert.Equal(["# Clip Engine settings.", "codec = auto", "player_names = Mochi, Alt", "detection = true"], lines);
        Assert.Equal(["Mochi", "Alt"], PlayerNames.Load(_file));

        // The engine settings page saves around it without losing it.
        var settings = EngineSettings.Load(_file);
        settings.Codec = "hevc";
        settings.Save(_file);
        Assert.Equal(["Mochi", "Alt"], PlayerNames.Load(_file));
    }

    [Fact]
    public void AddsTheLineWhenMissing()
    {
        File.WriteAllText(_file, "codec = auto\n");
        PlayerNames.Save(_file, ["Mochi"]);
        Assert.Equal(["Mochi"], PlayerNames.Load(_file));
    }

    [Theory]
    [InlineData("Counter-Strike 2", "Official game events")]
    [InlineData("League of Legends", "Official game events")]
    [InlineData("Team Fortress 2", "Reads the game's console log")]
    [InlineData("Valorant", "Reads the killfeed")]
    [InlineData("Battlefield 2042", "Reads the screen")]
    [InlineData("The Finals", "Reads the screen")]
    [InlineData("Halo Infinite", "Reads the killfeed")]
    [InlineData("Roblox", "Records, clips on the hotkey")]
    [InlineData("Some new game", "Reads the screen")]
    public void KnowsHowEachGameIsRead(string game, string method) => Assert.Equal(method, GameCatalog.Describe(game));
}
