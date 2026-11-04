namespace ClipEngine.Desktop.Services.Squad;

// A clip of this player's, as squad sync sees it: when its first frame was recorded (local Unix
// ms) and how long it is.
internal sealed record LocalClip(string Path, long UtcStartMs, double Seconds, string Trigger, bool Favorite);

// One teammate's view of a squad moment, lined up by when its first frame was recorded (server ms).
internal sealed record SquadView(string Member, string Name, long UtcStartMs, double Seconds, bool Uploaded, bool Mine);

// A part of the squad highlight: seconds [From, To] of one view.
internal sealed record HighlightPart(SquadView View, double From, double To);

internal static class SquadPlan
{
    // The part of this player's recording to save for a squadmate's moment, in local Unix ms.
    public static (long Start, long End) SaveWindow(long momentLocalMs, int beforeSeconds, int afterSeconds)
    {
        beforeSeconds = Math.Clamp(beforeSeconds, 2, 50);
        afterSeconds = Math.Clamp(afterSeconds, 1, 30);
        return (momentLocalMs - beforeSeconds * 1000L, momentLocalMs + afterSeconds * 1000L);
    }

    public static bool ShouldShare(string type, IEnumerable<string> shared) => shared.Contains(type, StringComparer.OrdinalIgnoreCase);

    // The clip of this player's own that shows a moment of theirs: one that has at least two
    // seconds either side of it, the shortest of those so the view is about that moment.
    public static LocalClip? OwnClipFor(IEnumerable<LocalClip> clips, long momentLocalMs)
    {
        const long margin = 2000;
        return clips
            .Where(c => c.UtcStartMs > 0 && c.Trigger != "long")
            .Where(c => c.UtcStartMs + margin <= momentLocalMs && momentLocalMs + margin <= c.UtcStartMs + (long)(c.Seconds * 1000))
            .OrderBy(c => c.Seconds)
            .FirstOrDefault();
    }

    // Whether a view goes up to the shared library without anyone asking for it.
    public static bool UploadsByItself(string policy, string momentType, bool starred) => policy switch
    {
        "all" => true,
        "highlights" => starred || momentType is "highlight" or "match_end",
        _ => false,
    };

    // Whether a squadmate asking for a view uploads it.
    public static bool UploadsOnRequest(string policy) => policy != "off";

    // Where in each view the moment is: seconds from the view's start.
    public static double MomentIn(SquadView view, long momentServerMs) => (momentServerMs - view.UtcStartMs) / 1000.0;

    // How far each view has to start into its file so all of them show the same instant: the
    // latest-starting view starts at 0 and the others skip ahead to match it.
    public static IReadOnlyDictionary<string, double> SyncOffsets(IReadOnlyList<SquadView> views)
    {
        if (views.Count == 0) return new Dictionary<string, double>();
        var latest = views.Max(v => v.UtcStartMs);
        return views.ToDictionary(v => v.Member, v => (latest - v.UtcStartMs) / 1000.0);
    }

    // The squad highlight of one moment: the view of whoever made the play, up to just after
    // it, then a replay of the moment from each teammate's view. Views are cut so each part
    // stays inside its clip.
    public static IReadOnlyList<HighlightPart> Highlight(IReadOnlyList<SquadView> views, string byMember, long momentServerMs,
                                                        double lead = 6, double tail = 2.5, double replayLead = 2.5,
                                                        double replayTail = 1.5)
    {
        var parts = new List<HighlightPart>();
        HighlightPart? Cut(SquadView v, double before, double after)
        {
            var at = MomentIn(v, momentServerMs);
            var from = Math.Max(0, at - before);
            var to = Math.Min(v.Seconds, at + after);
            return to - from >= 1 ? new HighlightPart(v, Math.Round(from, 2), Math.Round(to, 2)) : null;
        }
        var main = views.FirstOrDefault(v => v.Member == byMember) ?? views.FirstOrDefault();
        if (main is null) return parts;
        if (Cut(main, lead, tail) is { } first) parts.Add(first);
        foreach (var v in views.Where(v => !ReferenceEquals(v, main)))
            if (Cut(v, replayLead, replayTail) is { } replay) parts.Add(replay);
        return parts;
    }
}
