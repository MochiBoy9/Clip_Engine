namespace ClipEngine.Desktop.Services;

// How a game's rules did on a clip: what they found (from clip_engine.exe --scan) against the
// player's own marks of what really happened. A found moment counts for a mark of the same kind
// within Tolerance seconds of it; the closest pairs are matched first, and each is used once.
internal sealed record AccuracyResult(
    IReadOnlyList<ClipEvent> Caught,   // marks the rules found
    IReadOnlyList<ClipEvent> Missed,   // marks they didn't
    IReadOnlyList<ClipEvent> Extra)    // moments they found that aren't marked
{
    public int Marked => Caught.Count + Missed.Count;

    // Per kind of moment, in the engine's order: how many were marked, caught, and found extra.
    public IReadOnlyList<(string Type, int Marked, int Caught, int Extra)> ByType =>
        EngineSettings.AllEvents
            .Select(t => (t, Caught.Count(e => e.Type == t) + Missed.Count(e => e.Type == t), Caught.Count(e => e.Type == t),
                          Extra.Count(e => e.Type == t)))
            .Where(r => r.Item2 > 0 || r.Item4 > 0)
            .ToList();

    public string Summary
    {
        get
        {
            if (Marked == 0 && Extra.Count == 0) return "Nothing marked and nothing found.";
            var caught = Marked == 0 ? "Nothing marked" : $"Caught {Caught.Count} of {Marked} ({Caught.Count * 100 / Marked}%)";
            var extra = Extra.Count switch { 0 => "no false alarms", 1 => "1 false alarm", var n => $"{n} false alarms" };
            return $"{caught}, {extra}.";
        }
    }
}

internal static class DetectionAccuracy
{
    public const double Tolerance = 2.0;

    public static AccuracyResult Compare(IEnumerable<ClipEvent> marked, IEnumerable<ClipEvent> found, double tolerance = Tolerance)
    {
        var marks = marked.OrderBy(e => e.Time).ToList();
        var finds = found.OrderBy(e => e.Time).ToList();
        var pairs = new List<(int Mark, int Find, double Gap)>();
        for (var m = 0; m < marks.Count; m++)
        {
            for (var f = 0; f < finds.Count; f++)
            {
                var gap = Math.Abs(marks[m].Time - finds[f].Time);
                if (marks[m].Type == finds[f].Type && gap <= tolerance) pairs.Add((m, f, gap));
            }
        }
        var usedMarks = new HashSet<int>();
        var usedFinds = new HashSet<int>();
        foreach (var (m, f, _) in pairs.OrderBy(p => p.Gap))
        {
            if (usedMarks.Contains(m) || usedFinds.Contains(f)) continue;
            usedMarks.Add(m);
            usedFinds.Add(f);
        }
        return new AccuracyResult(
            marks.Where((_, i) => usedMarks.Contains(i)).ToList(),
            marks.Where((_, i) => !usedMarks.Contains(i)).ToList(),
            finds.Where((_, i) => !usedFinds.Contains(i)).ToList());
    }
}
