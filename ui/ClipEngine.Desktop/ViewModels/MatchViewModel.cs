using Avalonia.Media;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// One match on the Clips page: its result, the moments of the whole match on one ribbon, and
// its best clips.
public sealed class MatchViewModel
{
    private const int ShownClips = 3;

    internal MatchViewModel(MatchGroup match, IReadOnlyList<ClipItemViewModel> clips)
    {
        Match = match;
        Clips = clips;
        Gradient = Palettes.Gradient(Palettes.IndexFor(match.Game));
        // The clips with the most moments first; ties go to the earlier one.
        BestClips = clips.OrderByDescending(c => c.Events.Count).ThenBy(c => c.File.Created).Take(ShownClips).ToList();
        OpenCommand = new RelayCommand(() => BestClips.FirstOrDefault()?.OpenCommand.Execute(null));

        var seconds = Math.Max(1, match.Length.TotalSeconds);
        Ribbon = match.Events.Select(e => new RibbonTick(e.Seconds / seconds, EventKinds.Brush(e.Type))).ToList();
        Stats = new[] { "kill", "assist", "death", "revive" }
            .Select(type => (Type: type, Count: match.Count(type)))
            .Where(s => s.Count > 0 || s.Type is "kill" or "death")
            .Select(s => new MatchStat(s.Count.ToString(), EventKinds.Plural(s.Type), EventKinds.Brush(s.Type)))
            .ToList();
    }

    internal MatchGroup Match { get; }
    public IReadOnlyList<ClipItemViewModel> Clips { get; }
    public IReadOnlyList<ClipItemViewModel> BestClips { get; }
    public IReadOnlyList<RibbonTick> Ribbon { get; }
    public IReadOnlyList<MatchStat> Stats { get; }
    public RelayCommand OpenCommand { get; }

    public string Game => Match.Game;
    public string Title => Match.Number > 0 ? $"Match {Match.Number}" : "Play session";
    public string When => Format.When(Match.Start);
    public string Length => Format.Duration(Match.Length);
    public string ClipCountText => Clips.Count == 1 ? "1 clip" : $"{Clips.Count} clips";
    public string Caption => $"{When} · {Length} · {ClipCountText}";
    public string? Result => Match.Result;
    public bool HasResult => Match.Result is not null;
    public bool HasMoments => Match.Events.Count > 0;
    public IBrush Gradient { get; }
    public string Initials => Format.Initials(Match.Game);
}

public sealed record MatchStat(string Value, string Label, IBrush Brush);
