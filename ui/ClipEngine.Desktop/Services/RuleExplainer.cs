using System.Globalization;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Services;

// What a rule does among its game's rules, as the engine runs them (src/detect/EventFuser.h).
internal enum RuleRole
{
    Counts,   // counts every moment it sees, on its own
    Decides,  // one of rules that count a moment once together, and the one that says how many
    Agrees,   // one of those rules, backing up the one that decides, and counting what it alone saw
    BacksUp,  // confirm_only: never counts a moment alone
    Off,      // can't run: it needs the player's name, or has nothing to look for
}

// Detection Studio's plain account of a rule: what it watches, when it saves a moment, how
// repeats are told apart, and how it works with the game's other rules for the same moment.
// Everything here mirrors the engine's defaults, so what Studio says is what the engine does.
internal static class RuleExplainer
{
    // The engine's defaults (src/detect/DetectionRules.h, RuleTracker and EventFuser).
    public const int ForgetNamedMs = 3000;
    public const int ForgetUnnamedMs = 1200;
    public const int HoldMs = 1000;

    // How often the engine looks for a rule's event unless the rule says (DefaultSampleMs).
    public static int DefaultSampleMs(string eventName, int sampleMs) => eventName switch
    {
        "death" => Math.Max(sampleMs, 500),
        "match_start" or "match_end" => Math.Max(sampleMs, 1000),
        _ => sampleMs,
    };

    public static int SampleMs(StudioRule rule, int sampleMs) => rule.IntSetting("sample_ms") is { } ms and >= 100 and <= 5000
        ? ms : DefaultSampleMs(rule.Event, sampleMs);

    // "Killfeed" for [the_finals.kill.killfeed], or the event for a game's only rule for it.
    public static string Name(StudioRule rule)
    {
        if (rule.Suffix.Length == 0) return EventKinds.Label(rule.Event);
        var words = rule.Suffix.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ').Trim();
        return words.Length == 0 ? EventKinds.Label(rule.Event)
            : int.TryParse(words, out _) ? $"{EventKinds.Label(rule.Event)} {words}"
            : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public static RuleRole Role(StudioRule rule, IReadOnlyList<StudioRule> all, bool hasPlayerNames)
    {
        if (!Runs(rule, hasPlayerNames)) return RuleRole.Off;
        if (rule.ConfirmOnly) return RuleRole.BacksUp;
        if (Merge(rule) <= 0) return RuleRole.Counts;
        var partners = Partners(rule, all, hasPlayerNames).Where(r => !r.ConfirmOnly).ToList();
        if (partners.Count == 0) return RuleRole.Counts;
        return partners.All(p => Priority(p) <= Priority(rule)) ? RuleRole.Decides : RuleRole.Agrees;
    }

    public static string RoleText(RuleRole role) => role switch
    {
        RuleRole.Decides => "Decides the count",
        RuleRole.Agrees => "Backs up, counts misses",
        RuleRole.BacksUp => "Backs up only",
        RuleRole.Off => "Off",
        _ => "Counts on its own",
    };

    // The sentences Studio shows for the rule, in order: watches, matches, repeats, teamwork.
    public static IReadOnlyList<string> Explain(StudioRule rule, IReadOnlyList<StudioRule> all, bool hasPlayerNames,
                                                int sampleMs = 250)
    {
        var lines = new List<string>();
        var moment = EventKinds.Label(rule.Event);
        var every = Seconds(SampleMs(rule, sampleMs));

        if (rule.NeedsPlayer && !hasPlayerNames)
            lines.Add("Off until you set your in-game name: it looks for your name, and the engine skips it without one.");

        if (rule.IsSound)
        {
            var count = (rule.Setting("sound") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Length;
            var match = rule.Setting("sound_match") ?? "0.44";
            lines.Add($"Listens to the game's audio for {(count == 1 ? "a sound" : $"any of {count} cuts of a sound")} cut from your clips, "
                      + $"and hears it when the audio is at least {match} alike (0 to 1).");
        }
        else if (rule.Image is { Length: > 0 } image)
        {
            lines.Add($"Looks in the box every {every} for the picture {Path.GetFileName(image)}, "
                      + $"and sees a {moment} when part of the box is at least {rule.Setting("image_match") ?? "0.85"} alike (0 to 1).");
        }
        else if (Phrases(rule.Words).Count == 0)
        {
            lines.Add("Has no words to look for yet, so it never saves anything. Type the words the game shows.");
        }
        else
        {
            var where = rule.Position switch
            {
                "start" => rule.SkipTag ? " at the start of a row, after any squad tag" : " at the start of a row",
                "end" => " at the end of a row",
                _ => "",
            };
            var unless = Phrases(rule.Exclude) is { Count: > 0 } ex ? $", unless {Or(ex)} is on the same row" : "";
            var bright = rule.Threshold > 0 ? $", keeping only pixels brighter than {rule.Threshold}" : "";
            lines.Add($"Reads the box every {every}{bright}. It sees a {moment} when the box shows {Or(Phrases(rule.Words))}{where}{unless}.");
        }

        // Repeats: the same message read again isn't another moment.
        if (!rule.IsSound)
        {
            var forget = rule.IntSetting("forget_ms") is { } f and > 0 ? $"has been gone for {Seconds(f)}"
                : Named(rule) ? $"has been gone for {Seconds(ForgetNamedMs)}"
                : $"has been gone for about {Seconds(ForgetUnnamedMs)}";
            lines.Add(Named(rule)
                ? $"A message counts once however long it stays up; it's told apart by the names in it, and the same names count again once the message {forget}."
                : $"A message counts once however long it stays up, and counts again once it {forget}.");
        }
        if (rule.CooldownMs > 0)
        {
            var held = rule.Event is "kill" or "assist" or "revive" ? $" {EventKinds.Plural(rule.Event)} seen meanwhile are counted after it, not lost." : "";
            lines.Add($"Waits {Seconds(rule.CooldownMs)} after a match before matching again.{held}");
        }

        lines.Add(Teamwork(rule, all, hasPlayerNames));
        return lines;
    }

    // How the rule's sightings combine with the game's other rules for the same moment.
    private static string Teamwork(StudioRule rule, IReadOnlyList<StudioRule> all, bool hasPlayerNames)
    {
        var moment = EventKinds.Label(rule.Event);
        var plural = EventKinds.Plural(rule.Event).ToLowerInvariant();
        var others = all.Where(r => !ReferenceEquals(r, rule) && r.Event == rule.Event).ToList();
        var partners = Partners(rule, all, hasPlayerNames).ToList();
        var counting = partners.Where(r => !r.ConfirmOnly).ToList();
        var window = Seconds(Merge(rule));

        if (rule.ConfirmOnly)
        {
            if (Merge(rule) <= 0 || counting.Count == 0)
                return $"Never saves a {moment} by itself, and no other {moment} rule counts once with it, so it does nothing yet. "
                       + $"Turn on \"Count once with the other {moment} rules\" for one that reads the screen.";
            return $"Never saves a {moment} by itself: it backs up {Names(counting)} when it hears or sees the same {moment} within {window}, "
                   + "and shows as agreeing in \"why this moment\".";
        }
        if (Merge(rule) <= 0)
        {
            var alone = others.Where(r => Merge(r) <= 0 && !r.ConfirmOnly).ToList();
            return alone.Count == 0
                ? $"It's the only rule counting {plural}, so each {moment} it sees is saved."
                : $"Counts every {moment} it sees, and so does {Names(alone)}: a {moment} both see is saved twice. "
                  + $"Turn on \"Count once with the other {moment} rules\" on both to count it once.";
        }
        if (counting.Count == 0)
        {
            var backers = partners.Where(r => r.ConfirmOnly).ToList();
            return backers.Count > 0
                ? $"It's the only rule counting {plural} together with others, so it counts every {moment} it sees; {Names(backers)} backs it up."
                : $"Counts once with the other {moment} rules, but none of them do yet, so it counts every {moment} it sees.";
        }

        var top = counting.Concat([rule]).Max(Priority);
        var deciders = counting.Concat([rule]).Where(r => Priority(r) == top).ToList();
        var confirms = partners.Where(r => r.ConfirmOnly).ToList();
        var backed = confirms.Count > 0 ? $" {Names(confirms)} backs them up." : "";
        if (deciders.Count > 1 && deciders.Contains(rule))
            return $"Sightings within {window} of {Names(counting)} are the same {moment}. They share the highest priority ({top}), so the first to see it says how many.{backed}";
        if (deciders.Contains(rule))
            return $"Sightings within {window} of {Names(counting)} are the same {moment}. This rule has the highest priority ({top}), so it says how many there were; "
                   + $"the others back it up, and still count a {moment} only they saw.{backed}";
        return $"Sightings within {window} of {Names(deciders)} are the same {moment}, and {Names(deciders)} (priority {top}) says how many there were. "
               + $"This one backs it up, and still counts a {moment} that only it saw.{backed}";
    }

    // The game's other rules that count a moment of this event once with this one.
    private static IEnumerable<StudioRule> Partners(StudioRule rule, IReadOnlyList<StudioRule> all, bool hasPlayerNames) =>
        all.Where(r => !ReferenceEquals(r, rule) && r.Event == rule.Event && Merge(r) > 0 && Runs(r, hasPlayerNames));

    private static bool Runs(StudioRule rule, bool hasPlayerNames) =>
        !(rule.NeedsPlayer && !hasPlayerNames && Phrases(rule.Words).All(p => p.Contains("{player}", StringComparison.OrdinalIgnoreCase)))
        && (rule.IsSound || rule.Image is { Length: > 0 } || Phrases(rule.Words).Count > 0);

    public static int Merge(StudioRule rule) => rule.IntSetting("merge_ms") ?? 0;
    public static int Priority(StudioRule rule) => rule.IntSetting("priority") ?? 1;

    // A message with names in it (a killfeed row) is remembered by them; a lone popup isn't.
    private static bool Named(StudioRule rule) =>
        rule.Position != "anywhere" || Phrases(rule.Words).Any(p => p.Contains("{player}", StringComparison.OrdinalIgnoreCase));

    private static List<string> Phrases(string text) =>
        text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Or(IReadOnlyList<string> phrases)
    {
        var shown = phrases.Select(Quote).ToList();
        return shown.Count switch
        {
            1 => shown[0],
            2 => $"{shown[0]} or {shown[1]}",
            _ => string.Join(", ", shown.Take(shown.Count - 1)) + $" or {shown[^1]}",
        };
    }

    // "ELIMINATION {number}" reads as "ELIMINATION" and a number.
    private static string Quote(string phrase)
    {
        var text = phrase.Replace("{player}", "\u0001", StringComparison.OrdinalIgnoreCase)
                         .Replace("{number}", "\u0002", StringComparison.OrdinalIgnoreCase).Trim();
        if (text == "\u0001") return "your name";
        if (text == "\u0002") return "a number";
        var parts = text.Split(['\u0001', '\u0002']).Select(p => p.Trim()).Where(p => p.Length > 0).Select(p => $"\"{p}\"");
        var extra = new List<string>();
        if (text.Contains('\u0001')) extra.Add("your name");
        if (text.Contains('\u0002')) extra.Add("a number");
        return string.Join(" with ", new[] { string.Join(" ", parts) }.Concat([string.Join(" and ", extra)]).Where(s => s.Length > 0));
    }

    private static string Names(IReadOnlyList<StudioRule> rules)
    {
        var names = rules.Select(r => Name(r)).Distinct().ToList();
        return names.Count switch
        {
            0 => "no other rule",
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + $" and {names[^1]}",
        };
    }

    internal static string Seconds(int ms) => ms % 1000 == 0
        ? $"{ms / 1000} s"
        : ms < 1000 ? $"{ms} ms" : $"{(ms / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} s";
}
