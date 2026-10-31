using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class RuleExplainerTests
{
    // The Finals' kill rules as they ship: the killfeed decides, the score line and the chime back it up.
    private const string Finals = """
        [detection]
        sample_ms = 250

        [the_finals.kill.killfeed]
        region = 0.70, 0.03, 0.30, 0.20
        words = {player}
        position = start
        skip_tag = true
        cooldown_ms = 0
        merge_ms = 4000
        priority = 2

        [the_finals.kill.score_line]
        region = 0.30, 0.52, 0.40, 0.24
        words = ELIMINATION {number}
        exclude = DOUBLE | TRIPLE
        forget_ms = 3000
        merge_ms = 4000

        [the_finals.kill.sound]
        sound = a.wav | b.wav
        confirm_only = true
        merge_ms = 4000

        [the_finals.revive]
        region = 0.30, 0.52, 0.40, 0.24
        words = REVIVE {number}

        [the_finals.match_end]
        region = 0.25, 0.32, 0.5, 0.2
        words = VICTORY | DEFEAT
        """;

    private static List<StudioRule> Rules() => DetectionIni.Parse(Finals).RulesFor("the_finals").ToList();
    private static StudioRule Named(List<StudioRule> rules, string suffix) => rules.First(r => r.Suffix == suffix);

    [Fact]
    public void NamesRulesFromTheirSections()
    {
        var rules = DetectionIni.Parse(Finals).Rules;
        Assert.Equal(["Killfeed", "Score line", "Sound", "Revive", "Match end"], rules.Select(RuleExplainer.Name));
        Assert.Equal("Kill 2", RuleExplainer.Name(new StudioRule { Event = "kill", Suffix = "2" }));
    }

    [Fact]
    public void SaysWhichRuleDecidesTheCount()
    {
        var rules = Rules();
        Assert.Equal(RuleRole.Decides, RuleExplainer.Role(Named(rules, "killfeed"), rules, hasPlayerNames: true));
        Assert.Equal(RuleRole.Agrees, RuleExplainer.Role(Named(rules, "score_line"), rules, hasPlayerNames: true));
        Assert.Equal(RuleRole.BacksUp, RuleExplainer.Role(Named(rules, "sound"), rules, hasPlayerNames: true));
        Assert.Equal(RuleRole.Counts, RuleExplainer.Role(rules.First(r => r.Event == "revive"), rules, hasPlayerNames: true));
    }

    // Without a name the engine leaves the killfeed out, so the score line counts alone.
    [Fact]
    public void KillfeedIsOffWithoutAName()
    {
        var rules = Rules();
        Assert.Equal(RuleRole.Off, RuleExplainer.Role(Named(rules, "killfeed"), rules, hasPlayerNames: false));
        Assert.Equal(RuleRole.Counts, RuleExplainer.Role(Named(rules, "score_line"), rules, hasPlayerNames: false));
        var lines = RuleExplainer.Explain(Named(rules, "killfeed"), rules, hasPlayerNames: false);
        Assert.StartsWith("Off until you set your in-game name", lines[0]);
    }

    [Fact]
    public void ExplainsWhatARuleReadsInPlainWords()
    {
        var rules = Rules();
        var killfeed = RuleExplainer.Explain(Named(rules, "killfeed"), rules, hasPlayerNames: true);
        Assert.Contains(killfeed, l => l.Contains("every 250 ms") && l.Contains("your name") && l.Contains("start of a row, after any squad tag"));
        Assert.Contains(killfeed, l => l.Contains("told apart by the names in it"));
        Assert.DoesNotContain(killfeed, l => l.StartsWith("Waits"));  // cooldown_ms = 0
        Assert.Contains(killfeed, l => l.Contains("highest priority (2)") && l.Contains("Score line") && l.Contains("Sound backs them up"));

        var score = RuleExplainer.Explain(Named(rules, "score_line"), rules, hasPlayerNames: true);
        Assert.Contains(score, l => l.Contains("\"ELIMINATION\" with a number") && l.Contains("unless \"DOUBLE\" or \"TRIPLE\""));
        Assert.Contains(score, l => l.Contains("gone for 3 s"));
        Assert.Contains(score, l => l.Contains("Killfeed (priority 2) says how many"));

        var sound = RuleExplainer.Explain(Named(rules, "sound"), rules, hasPlayerNames: true);
        Assert.Contains(sound, l => l.Contains("any of 2 cuts"));
        Assert.Contains(sound, l => l.StartsWith("Never saves a Kill by itself: it backs up Killfeed and Score line"));
    }

    [Fact]
    public void ExplainsHowOftenEachMomentIsLookedFor()
    {
        var rules = Rules();
        var end = RuleExplainer.Explain(rules.First(r => r.Event == "match_end"), rules, hasPlayerNames: true);
        Assert.Contains(end, l => l.Contains("every 1 s"));
        var death = new StudioRule { Game = "the_finals", Event = "death", Words = "ELIMINATED BY" };
        Assert.Equal(500, RuleExplainer.SampleMs(death, 250));
        death.SetSetting("sample_ms", "150");
        Assert.Equal(150, RuleExplainer.SampleMs(death, 250));
    }

    // Two rules for one moment that each count on their own save it twice; Studio says so.
    [Fact]
    public void WarnsWhenTwoRulesCountTheSameMoment()
    {
        var a = new StudioRule { Event = "kill", Words = "ELIMINATED" };
        var b = new StudioRule { Event = "kill", Suffix = "banner", Words = "KILL" };
        var lines = RuleExplainer.Explain(a, [a, b], hasPlayerNames: true);
        Assert.Contains(lines, l => l.Contains("saved twice") && l.Contains("Banner"));
        var alone = RuleExplainer.Explain(a, [a], hasPlayerNames: true);
        Assert.Contains(alone, l => l.Contains("only rule counting kills"));
    }

    [Fact]
    public void ABackUpWithNothingToBackUpSaysSo()
    {
        var sound = new StudioRule { Event = "kill", Suffix = "sound" };
        sound.SetSetting("sound", "a.wav");
        sound.SetSetting("confirm_only", "true");
        sound.SetSetting("merge_ms", "4000");
        var words = new StudioRule { Event = "kill", Words = "ELIMINATED" };
        Assert.Contains(RuleExplainer.Explain(sound, [sound, words], hasPlayerNames: true), l => l.Contains("does nothing yet"));
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var rule = Rules()[0];
        var copy = rule.Clone();
        copy.SetSetting("priority", "5");
        copy.Words = "X";
        Assert.Equal("2", rule.Setting("priority"));
        Assert.Equal("{player}", rule.Words);
        Assert.True(rule.NeedsPlayer && rule.SkipTag);
        Assert.Equal(DetectionIni.RulesText([rule]), DetectionIni.RulesText([rule.Clone()]));
    }

    [Fact]
    public void NamesSectionsFromWhatsTyped()
    {
        Assert.Equal("score_line", StudioViewModel.SectionName("Score line"));
        Assert.Equal("big_banner", StudioViewModel.SectionName("  Big   Banner! "));
        var rules = new List<StudioRule>
        {
            new() { Event = "kill", Suffix = "feed" }, new() { Event = "kill", Suffix = "feed" }, new() { Event = "death", Suffix = "feed" },
        };
        StudioViewModel.NameSections(rules);
        Assert.Equal(["feed", "2", "feed"], rules.Select(r => r.Suffix));
    }

    [Fact]
    public void FrameCacheKeepsTheNewest()
    {
        var cache = new FrameCache(2);
        cache.Add("a.mp4", 1.0, [1]);
        cache.Add("a.mp4", 2.04, [2]);
        Assert.Equal([2], cache.TryGet("a.mp4", 2.0)!);
        Assert.NotNull(cache.TryGet("a.mp4", 1.0));  // now the newest
        cache.Add("b.mp4", 1.0, [3]);
        Assert.Null(cache.TryGet("a.mp4", 2.0));
        Assert.NotNull(cache.TryGet("a.mp4", 1.0));
        Assert.Equal(2, cache.Count);
    }
}
