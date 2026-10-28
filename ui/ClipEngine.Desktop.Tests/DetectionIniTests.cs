using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class DetectionIniTests
{
    private const string Ini = """
        # Clip Engine detection rules.

        [detection]
        sample_ms = 250


        # ---------------------------------------------------------------------- The Finals

        [the_finals.kill]
        region = 0.30, 0.52, 0.40, 0.24
        words = ELIMINATED | KNOCKED OUT
        exclude = BY   # not when you're the one

        [the_finals.death]
        region = 0.2, 0.3, 0.6, 0.5
        words = ELIMINATED BY
        cooldown_ms = 8000


        # ---------------------------------------------------------------------- Overwatch

        [overwatch.kill]
        region = 0.30, 0.55, 0.40, 0.20
        words = ELIMINATED
        threshold = 180
        """;

    [Fact]
    public void ReadsRules()
    {
        var rules = DetectionIni.Parse(Ini).Rules;
        Assert.Equal(["the_finals.kill", "the_finals.death", "overwatch.kill"], rules.Select(r => r.Section));
        var kill = rules[0];
        Assert.Equal((0.30, 0.52, 0.40, 0.24), (kill.Left, kill.Top, kill.Width, kill.Height));
        Assert.Equal(("ELIMINATED | KNOCKED OUT", "BY"), (kill.Words, kill.Exclude));
        Assert.Equal(8000, rules[1].CooldownMs);
        Assert.Equal(180, rules[2].Threshold);
    }

    [Fact]
    public void ReplacesOneGameAndKeepsTheRest()
    {
        var ini = DetectionIni.Parse(Ini);
        ini.SetGame("the_finals", null,
        [
            new StudioRule { Game = "the_finals", Event = "kill", Left = 0.7, Top = 0.05, Width = 0.25, Height = 0.2, Words = "ELIMINATED" },
            new StudioRule { Game = "the_finals", Event = "revive", Suffix = "teammate", Words = "REVIVED", Threshold = 200, CooldownMs = 3000 },
        ]);
        var text = ini.ToString();

        var again = DetectionIni.Parse(text);
        Assert.Equal(["overwatch.kill", "the_finals.kill", "the_finals.revive.teammate"], again.Rules.Select(r => r.Section));
        Assert.Contains("region = 0.7, 0.05, 0.25, 0.2", text);
        Assert.Contains("[the_finals.revive.teammate]\nregion = 0.35, 0.4, 0.3, 0.15\nwords = REVIVED\ncooldown_ms = 3000\nthreshold = 200", text);
        Assert.DoesNotContain("KNOCKED OUT", text);
        Assert.DoesNotContain("The Finals\n", text.Split("Made in")[0]);  // the old banner went with its rules
        Assert.Contains("# ---------------------------------------------------------------------- Overwatch", text);
        Assert.Contains("sample_ms = 250", text);
    }

    [Fact]
    public void AddsAndReadsCustomGames()
    {
        var ini = DetectionIni.Parse(Ini);
        var valorant = new StudioGame("valorant", "VALORANT", ["VALORANT-Win64-Shipping.exe"]);
        ini.SetGame("valorant", valorant, [new StudioRule { Game = "valorant", Words = "ELIMINATED" }]);
        ini.SetGame("valorant", valorant, [new StudioRule { Game = "valorant", Words = "ELIMINATED | KILLED" }]);

        var again = DetectionIni.Parse(ini.ToString());
        var game = Assert.Single(again.Games);
        Assert.Equal(("valorant", "VALORANT"), (game.Id, game.Name));
        Assert.Equal(["VALORANT-Win64-Shipping.exe"], game.Executables);
        var rule = Assert.Single(again.Rules, r => r.Game == "valorant");
        Assert.Equal("ELIMINATED | KILLED", rule.Words);
        Assert.Equal(1, ini.ToString().Split("[game.valorant]").Length - 1);
    }

    [Fact]
    public void KeepsSettingsStudioDoesNotEdit()
    {
        var ini = DetectionIni.Parse("""
            [valorant.kill]
            region = 0.66, 0.07, 0.33, 0.26
            words = {player}
            position = start
            cooldown_ms = 0

            [valorant.kill.banner]
            region = 0.42, 0.72, 0.16, 0.16
            image = valorant_kill.png
            image_screen = 1440
            sample_ms = 200
            """);
        var rules = ini.Rules;
        Assert.Equal("start", rules[0].Position);
        Assert.Equal("anywhere", rules[1].Position);
        Assert.Equal("valorant_kill.png", rules[1].Image);

        ini.SetGame("valorant", null, rules);
        var text = ini.ToString();
        Assert.Contains("words = {player}\ncooldown_ms = 0\nposition = start\n", text);
        Assert.Contains("[valorant.kill.banner]\nregion = 0.42, 0.72, 0.16, 0.16\nimage = valorant_kill.png\nimage_screen = 1440\nsample_ms = 200\n", text);
        Assert.Equal("start", DetectionIni.Parse(text).Rules[0].Position);
    }

    [Fact]
    public void KeepsTheGamesScoreboardWhenItsRulesAreReplaced()
    {
        var ini = DetectionIni.Parse(Ini + "\n[the_finals.scoreboard]\nkills = E | ELIMS\ndeaths = D\n");
        ini.SetGame("the_finals", null, [new StudioRule { Game = "the_finals", Words = "ELIMINATED" }]);
        var text = ini.ToString();
        Assert.Contains("[the_finals.scoreboard]\nkills = E | ELIMS", text);
        // The scoreboard isn't a rule the Studio shows.
        Assert.DoesNotContain(DetectionIni.Parse(text).Rules, r => r.Section.StartsWith("the_finals.scoreboard"));
    }

    [Theory]
    [InlineData("Marvel Rivals", "marvel_rivals")]
    [InlineData("  VALORANT ", "valorant")]
    [InlineData("Counter-Strike 2", "counter_strike_2")]
    [InlineData("!!!", "")]
    public void GameIdsSuitSectionNames(string name, string id) =>
        Assert.Equal(id, ClipEngine.Desktop.ViewModels.StudioViewModel.GameId(name));
}
