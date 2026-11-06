using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class ClipKeybindsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"clip_engine_{Guid.NewGuid():N}.ini");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void ParsesKeysWithTheirLengths()
    {
        var keys = ClipKeybinds.Parse("Alt+F8 15, Shift+F8 120s,  Ctrl + F8 45 , Nope 10, F9, F10 0, F11 x");
        Assert.Equal([new ClipKeybind("Alt+F8", 15), new ClipKeybind("Shift+F8", 120), new ClipKeybind("Ctrl + F8", 45)], keys);
        Assert.Empty(ClipKeybinds.Parse(""));
        Assert.Equal("Alt+F8 15, Shift+F8 120", ClipKeybinds.Format([new("Alt+F8", 15), new(" Shift+F8 ", 120)]));
        Assert.Equal(keys, ClipKeybinds.Parse(ClipKeybinds.Format(keys)));
    }

    [Theory]
    [InlineData("F8", true)]
    [InlineData("Alt+F10", true)]
    [InlineData("ctrl + shift + s", true)]
    [InlineData("PageDown", true)]
    [InlineData("S", true)]  // the engine takes it; Problem warns about it
    [InlineData("F25", false)]
    [InlineData("F0", false)]
    [InlineData("Alt", false)]
    [InlineData("F8+F9", false)]
    [InlineData("Alt+", false)]
    [InlineData("", false)]
    public void KnowsWhichKeysTheEngineReads(string text, bool expected) => Assert.Equal(expected, ClipKeybinds.IsKey(text));

    [Fact]
    public void WarnsAboutKeysThatGoOffWhileTyping()
    {
        Assert.Null(ClipKeybinds.Problem("F7"));
        Assert.Null(ClipKeybinds.Problem("Alt+S"));
        Assert.NotNull(ClipKeybinds.Problem("S"));
        Assert.NotNull(ClipKeybinds.Problem("Banana"));
        Assert.Equal(ClipKeybinds.Normalize("Alt + F8"), ClipKeybinds.Normalize("f8+alt"));
        Assert.Equal(ClipKeybinds.Normalize("Control+X"), ClipKeybinds.Normalize("ctrl+x"));
        Assert.NotEqual(ClipKeybinds.Normalize("Alt+F8"), ClipKeybinds.Normalize("F8"));
    }

    [Fact]
    public void SavesAGamesOwnKeysOnItsOwnLine()
    {
        File.WriteAllText(_file, """
            # settings
            save_hotkey = F8
            hud_scale = valorant 90
            """);
        Assert.Null(ClipKeybinds.LoadForGame(_file, "valorant"));

        ClipKeybinds.SaveForGame(_file, "valorant", [new("F8", 20), new("Alt+F8", 60)]);
        ClipKeybinds.SaveForGame(_file, "fortnite", [new("F7", 45)]);
        Assert.Contains("clip_hotkeys.valorant = F8 20, Alt+F8 60", File.ReadAllLines(_file));
        Assert.Equal([new ClipKeybind("F8", 20), new ClipKeybind("Alt+F8", 60)], ClipKeybinds.LoadForGame(_file, "valorant"));

        // Changing them keeps one line, where it was.
        ClipKeybinds.SaveForGame(_file, "valorant", [new("F6", 10)]);
        var lines = File.ReadAllLines(_file);
        Assert.Single(lines, l => l.StartsWith("clip_hotkeys.valorant"));
        Assert.Equal(3, Array.IndexOf(lines, "clip_hotkeys.valorant = F6 10"));

        // None takes the game back to the keys from Settings, and leaves the others be.
        ClipKeybinds.SaveForGame(_file, "valorant", null);
        Assert.Null(ClipKeybinds.LoadForGame(_file, "valorant"));
        Assert.Equal([new ClipKeybind("F7", 45)], ClipKeybinds.LoadForGame(_file, "fortnite"));
        Assert.Contains("hud_scale = valorant 90", File.ReadAllLines(_file));
        Assert.Equal("F8", EngineSettings.Load(_file).SaveHotkey);
    }

    [Fact]
    public void EngineSettingsKeepMoreClipKeys()
    {
        File.WriteAllText(_file, "more_clip_hotkeys = Alt+F8 15, Shift+F8 120\nclip_hotkeys.valorant = F8 20\n");
        var settings = EngineSettings.Load(_file);
        Assert.Equal([new ClipKeybind("Alt+F8", 15), new ClipKeybind("Shift+F8", 120)], settings.MoreClipHotkeys);

        settings.MoreClipHotkeys = [new("Ctrl+F8", 90)];
        settings.Save(_file);
        var lines = File.ReadAllLines(_file);
        Assert.Contains("more_clip_hotkeys = Ctrl+F8 90", lines);
        // A game's own keys aren't the engine settings' to change.
        Assert.Contains("clip_hotkeys.valorant = F8 20", lines);
    }

    [Fact]
    public void ListFlagsClashesAndSuggestsFreeKeys()
    {
        var changes = 0;
        var list = new ClipKeybindsViewModel(() => ["F8", "F9"], () => changes++, 60);
        list.Load([new("Alt+F8", 15)]);
        Assert.Equal(0, changes);

        list.AddCommand.Execute(null);
        Assert.Equal(1, changes);
        Assert.Equal(2, list.Rows.Count);
        Assert.Equal("Shift+F8", list.Rows[1].Hotkey);  // Alt+F8 is taken
        Assert.NotEqual(15, list.Rows[1].Seconds);
        Assert.Null(list.FirstProblem);

        list.Rows[1].Hotkey = "alt + f8";
        Assert.NotNull(list.Rows[1].Problem);
        Assert.Null(list.Rows[0].Problem);
        list.Rows[1].Hotkey = "F9";
        Assert.Contains("another hotkey", list.Rows[1].Problem);
        list.Rows[1].Hotkey = "F7";
        Assert.Null(list.FirstProblem);

        // Lengths stay within the replay buffer.
        list.Rows[1].Seconds = 500;
        Assert.Equal(60, list.Rows[1].Seconds);
        list.MaxSeconds = 30;
        Assert.Equal(30, list.Rows[1].Seconds);
        Assert.Equal(15, list.Rows[0].Seconds);

        list.Rows[0].RemoveCommand.Execute(null);
        Assert.Equal([new ClipKeybind("F7", 30)], list.ToKeybinds());
    }

    [Fact]
    public void GameKeysStartFromTheOnesInSettings()
    {
        var game = new GameClipKeysViewModel(live: false, sendEngineCommand: null);
        game.Load("valorant", "Valorant");
        Assert.False(game.UseOwn);
        Assert.False(game.HasChanges);
        Assert.Contains("F8 saves 30 s", game.Summary);

        game.UseOwn = true;
        Assert.True(game.HasChanges);
        Assert.Equal([new ClipKeybind("F8", 30)], game.Keys.ToKeybinds());
        game.Keys.Rows[0].Seconds = 20;
        game.SaveCommand.Execute(null);
        Assert.False(game.HasChanges);
        Assert.Contains("Valorant", game.Message);

        game.UseOwn = false;
        Assert.True(game.HasChanges);
        game.UseOwn = true;
        Assert.False(game.HasChanges);
    }
}
