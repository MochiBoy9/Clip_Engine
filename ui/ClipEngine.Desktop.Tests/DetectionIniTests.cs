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

    [Fact]
    public void MarksAGameSavedInStudioAsTheUsersOwn()
    {
        var user = DetectionIni.NewChanges();
        Assert.Empty(user.EditedGames);
        user.SetGame("valorant", null, []);
        // Even with every rule removed, the built-in ones stay off for it.
        Assert.Equal(["valorant"], user.EditedGames);
        Assert.Contains("[valorant.edited]", user.ToString());
        Assert.Empty(user.Rules);

        user.SetGame("valorant", null, [new StudioRule { Game = "valorant", Words = "KILLED" }]);
        Assert.Single(user.Rules);
        Assert.Single(user.ToString().Split('\n'), l => l == "[valorant.edited]");

        user.RemoveGame("valorant");
        Assert.Empty(user.EditedGames);
        Assert.StartsWith(DetectionIni.ChangesHeader, user.ToString());
    }

    [Fact]
    public void ComparesRulesByWhatTheySay()
    {
        var a = DetectionIni.Parse(Ini);
        var b = DetectionIni.Parse(Ini.Replace("exclude = BY   # not when you're the one", "exclude = BY"));
        Assert.Equal(a.RulesText("the_finals"), b.RulesText("the_finals"));
        Assert.Equal(a.RulesText("the_finals"), DetectionIni.RulesText(b.RulesFor("the_finals").Reverse()));
        var changed = b.RulesFor("the_finals");
        changed[0].Words = "ELIMINATED";
        Assert.NotEqual(a.RulesText("the_finals"), DetectionIni.RulesText(changed));
    }

    [Fact]
    public void TrimsAnOldWholeCopyToTheUsersChanges()
    {
        var shipped = DetectionIni.Parse(Ini.Replace("ELIMINATED | KNOCKED OUT", "ELIMINATION {number}"));
        // A pre-0.6 copy: The Finals as it shipped back then, Overwatch made in Studio, a game added
        // by hand, and the user's own [detection] setting.
        var old = DetectionIni.Parse(Ini[..Ini.IndexOf("[overwatch.kill]")].Replace("sample_ms = 250", "sample_ms = 200") + """
            # Made in Detection Studio.

            [overwatch.kill]
            region = 0.7, 0.1, 0.2, 0.1
            words = ELIMINATED

            [game.my_game]
            name = My Game
            exe = MyGame.exe

            [my_game.kill]
            words = KO
            """);
        var trimmed = old.ChangesOnly(shipped);
        Assert.NotNull(trimmed);
        var text = trimmed!.ToString();
        Assert.StartsWith(DetectionIni.ChangesHeader, text);
        // The Finals goes back to the built-in rules, and their fix.
        Assert.DoesNotContain("the_finals", text);
        Assert.Equal(["my_game", "overwatch"], trimmed.EditedGames.Order());
        Assert.Equal("0.7, 0.1, 0.2, 0.1", trimmed.RulesFor("overwatch").Single().RegionText);
        Assert.Equal(["my_game"], trimmed.Games.Select(g => g.Id));
        Assert.Contains("[detection]\nsample_ms = 200", text);
        // Already trimmed: nothing more to do.
        Assert.Null(trimmed.ChangesOnly(shipped));
    }

    [Fact]
    public void DropsStudioGamesThatMatchTheBuiltInRules()
    {
        var shipped = DetectionIni.Parse(Ini);
        var old = DetectionIni.Parse(Ini.Replace("# ---------------------------------------------------------------------- Overwatch",
                                                 "# ---------------------------------------------------------------------- Overwatch\n# Made in Detection Studio."));
        var trimmed = old.ChangesOnly(shipped)!;
        Assert.Empty(trimmed.EditedGames);
        Assert.DoesNotContain("[detection]", trimmed.ToString());
    }

    [Theory]
    [InlineData("Marvel Rivals", "marvel_rivals")]
    [InlineData("  VALORANT ", "valorant")]
    [InlineData("Counter-Strike 2", "counter_strike_2")]
    [InlineData("!!!", "")]
    public void GameIdsSuitSectionNames(string name, string id) =>
        Assert.Equal(id, ClipEngine.Desktop.ViewModels.StudioViewModel.GameId(name));
}
