using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

public enum Page { Home, Clips, Squad, Friends, Records, Journal, Games, Cloud, Settings }

public sealed record NavItem(Page Page, string Title, StreamGeometry Icon)
{
    // Its place in the rail, printed beside it: 01 for the first.
    public string Index => ((int)Page + 1).ToString("00", CultureInfo.InvariantCulture);
}

public enum ActivityKind { Info, Recording, Clip, Moment, Warning, Error }

public sealed class ActivityItem(DateTime time, ActivityKind kind, string text)
{
    public string Time { get; } = time.ToString("t", CultureInfo.CurrentCulture);
    public ActivityKind Kind { get; } = kind;
    public string Text { get; } = text;
    public bool IsClip => Kind == ActivityKind.Clip;
    public bool IsMoment => Kind == ActivityKind.Moment;
    public bool IsRecording => Kind == ActivityKind.Recording;
    public bool IsProblem => Kind is ActivityKind.Warning or ActivityKind.Error;
    public bool IsInfo => Kind == ActivityKind.Info;
}

public sealed class ClipItemViewModel : ObservableObject
{
    private Bitmap? _thumbnail;
    private ClipFile _file;

    internal ClipItemViewModel(ClipFile file, int paletteIndex, MainViewModel? owner = null)
    {
        _file = file;
        Placeholder = Palettes.Gradient(paletteIndex);
        OpenCommand = new RelayCommand(() => owner?.OpenClipCommand.Execute(this));
        ShowCommand = new RelayCommand(() => owner?.ShowClipCommand.Execute(this));
        DeleteCommand = new RelayCommand(() => owner?.DeleteClipCommand.Execute(this));
        ShareCommand = new RelayCommand(() => owner?.ShareClipCommand.Execute(this));
        LinkCommand = new RelayCommand(() => owner?.CopyLinkCommand.Execute(this));
        FavoriteCommand = new RelayCommand(() => owner?.FavoriteClipCommand.Execute(this));
    }

    public RelayCommand OpenCommand { get; }
    public RelayCommand ShowCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand LinkCommand { get; }
    public RelayCommand FavoriteCommand { get; }

    internal ClipFile File
    {
        get => _file;
        set
        {
            if (!Set(ref _file, value)) return;
            Raise(nameof(IsFavorite));
            Raise(nameof(FavoriteTip));
        }
    }

    // Starred clips sort into Favorites and are never removed by the storage cleanup.
    public bool IsFavorite => File.Metadata?.Favorite == true;
    public string FavoriteTip => IsFavorite ? "Remove from favorites" : "Add to favorites (kept forever)";

    // The clip's moments along its length, for the strip under the thumbnail.
    public IReadOnlyList<RibbonTick> Ribbon => RibbonTick.For(Events, File.Duration?.TotalSeconds ?? File.Metadata?.Seconds ?? 0);
    public bool HasRibbon => Events.Count > 0;
    public string Path => File.Path;
    public string Game => File.Game;
    public string Title => System.IO.Path.GetFileNameWithoutExtension(File.Path).Replace('_', ' ');

    // "3 kills, 1 revive" for clips with detected moments.
    public string Summary => File.Metadata?.Title ?? "";
    public bool HasSummary => Summary.Length > 0;
    public bool IsAutomatic => File.Metadata?.IsAutomatic == true;
    public bool IsVoice => File.Metadata?.IsVoice == true;
    public string Caption => HasSummary ? $"{Summary} · {Size}" : Size;
    internal IReadOnlyList<ClipEvent> Events => File.Metadata?.Events ?? [];
    public string When => Format.When(File.Created);
    public string Size => Format.Bytes(File.Bytes);
    public string Duration => File.Duration is { } d ? Format.Duration(d) : "";
    public bool HasDuration => File.Duration is not null;
    public IBrush Placeholder { get; }
    public string Initials => Format.Initials(File.Game);

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (Set(ref _thumbnail, value)) Raise(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;
}

// One moment on an event ribbon, as a fraction of the way along.
public sealed record RibbonTick(double Fraction, IBrush Brush)
{
    internal static IReadOnlyList<RibbonTick> For(IEnumerable<ClipEvent> events, double seconds) =>
        seconds > 0 ? events.Select(e => new RibbonTick(e.Time / seconds, EventKinds.Brush(e.Type))).ToList() : [];
}

// A detected moment on the player's timeline, and how it was found.
public sealed class MarkerViewModel(string type, double seconds, string text, RelayCommand seek) : ObservableObject
{
    // Jumping to a marker starts a little before it, so you see what led up to it.
    private const double LeadIn = 2;

    private bool _isSelected;

    public string Type { get; } = type;
    public string Label { get; } = EventKinds.Label(type);
    public string Text { get; } = text;
    public double Seconds { get; } = seconds;
    public double SeekSeconds => Math.Max(0, Seconds - LeadIn);
    public string TimeText { get; } = Format.Duration(TimeSpan.FromSeconds(seconds));
    public IBrush Brush { get; } = EventKinds.Brush(type);
    public RelayCommand SeekCommand { get; } = seek;

    // Each rule that saw it, the one that counted it first.
    public IReadOnlyList<WitnessViewModel> Witnesses { get; private init; } = [];
    internal IReadOnlyList<ClipWitness>? FoundBy
    {
        init => Witnesses = (value ?? []).OrderByDescending(w => w.Counted).ThenBy(w => w.Offset)
            .Select(w => new WitnessViewModel(w, Brush)).ToList();
    }
    public bool HasWitnesses => Witnesses.Count > 0;
    // Seen by more than one rule, so a misread of one alone couldn't have made it up.
    public bool IsConfirmed => Witnesses.Select(w => w.Rule).Distinct().Count() > 1;
    public string ConfirmedTip => IsConfirmed ? $"Seen by {Witnesses.Select(w => w.Rule).Distinct().Count()} rules" : "";

    public string WhySummary
    {
        get
        {
            if (!HasWitnesses) return "From the game's own events, or saved before Clip Engine kept how each moment was found.";
            var counted = Witnesses.FirstOrDefault(w => w.Counted) ?? Witnesses[0];
            var others = Witnesses.Where(w => w.Rule != counted.Rule).Select(w => w.Name.ToLowerInvariant()).Distinct().ToList();
            return others.Count == 0
                ? $"Only the {counted.Name.ToLowerInvariant()} saw it."
                : $"Counted from the {counted.Name.ToLowerInvariant()}, and the {string.Join(" and the ", others)} saw it too.";
        }
    }

    // Selected in the list: shows how it was found.
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    // Opens Detection Studio on this moment, with the rule that counted it picked.
    public RelayCommand? StudioCommand { get; init; }
    public bool HasStudio => StudioCommand is not null;
}

// One rule's sighting of a moment.
public sealed class WitnessViewModel
{
    private static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x93, 0x9E));

    internal WitnessViewModel(ClipWitness witness, IBrush moment)
    {
        Rule = witness.Rule;
        Name = RuleNames.Describe(witness.Rule);
        Text = witness.Text;
        Counted = witness.Counted;
        var when = Math.Abs(witness.Offset) < 0.05 ? "" : $" · {witness.Offset.ToString("+0.0;-0.0", CultureInfo.InvariantCulture)}s";
        Detail = (witness.Counted ? "Counted" : "Agreed") + when;
        Box = witness.Box.Length == 4 ? witness.Box : null;
        Brush = witness.Counted ? moment : Muted;
    }

    public string Rule { get; }
    public string Name { get; }
    public string Text { get; }
    public bool Counted { get; }
    public string Detail { get; }
    public IBrush Brush { get; }
    // Where it read, as fractions of the frame: left, top, width, height.
    internal double[]? Box { get; }
}

// Names for detection.ini sections a player can read: "the_finals.kill.killfeed" is the killfeed.
internal static class RuleNames
{
    public static string Describe(string section)
    {
        var parts = section.Split('.');
        if (parts.Length >= 3)
        {
            var name = string.Join(" ", parts[2..]).Replace('_', ' ').Trim();
            if (name.Length > 0) return char.ToUpperInvariant(name[0]) + name[1..];
        }
        return parts.Length >= 2 ? $"{EventKinds.Label(parts[1])} rule" : section;
    }
}

// Names and colours for the event types in src/detect/GameEvent.h.
internal static class EventKinds
{
    public static string Label(string type) => type switch
    {
        "kill" => "Kill",
        "assist" => "Assist",
        "death" => "Death",
        "revive" => "Revive",
        "match_start" => "Match start",
        "match_end" => "Match end",
        "highlight" => "Highlight",
        _ => type.Length > 0 ? char.ToUpperInvariant(type[0]) + type[1..].Replace('_', ' ') : "Moment",
    };

    // "1 Kill", "3 Kills".
    public static string Count(string type, int count) => $"{count} {(count == 1 ? Label(type) : Plural(type))}";

    public static string Plural(string type) => type switch
    {
        "kill" => "Kills",
        "assist" => "Assists",
        "death" => "Deaths",
        "revive" => "Revives",
        "match_start" => "Match starts",
        "match_end" => "Match ends",
        "highlight" => "Highlights",
        _ => Label(type),
    };

    // One shared brush per kind: the same few are drawn thousands of times across the library.
    private static readonly IBrush Kill = new ImmutableSolidColorBrush(Color.Parse("#FF3D52"));
    private static readonly IBrush Assist = new ImmutableSolidColorBrush(Color.Parse("#3AB8FF"));
    private static readonly IBrush Death = new ImmutableSolidColorBrush(Color.Parse("#7F8088"));
    private static readonly IBrush Revive = new ImmutableSolidColorBrush(Color.Parse("#34D17D"));
    private static readonly IBrush Match = new ImmutableSolidColorBrush(Color.Parse("#FF8A2B"));
    private static readonly IBrush Other = new ImmutableSolidColorBrush(Color.Parse("#B48CFF"));

    public static IBrush Brush(string type) => type switch
    {
        "kill" => Kill,
        "assist" => Assist,
        "death" => Death,
        "revive" => Revive,
        "match_start" or "match_end" => Match,
        _ => Other,
    };
}

public sealed class GameItemViewModel(string name, int paletteIndex) : ObservableObject
{
    private bool _isRunning;
    private bool _isRecording;
    private bool _isInstalled = true;
    private string? _store;
    private int _clipCount;

    public string Name { get; } = name;
    public string Initials { get; } = Format.Initials(name);
    public IBrush Gradient { get; } = Palettes.Gradient(paletteIndex);
    // How moments are found in this game: its own API, its killfeed or the rest of the screen.
    public string DetectionText { get; } = GameCatalog.Describe(name);

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (Set(ref _isRunning, value)) Raise(nameof(Status));
        }
    }

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (Set(ref _isRecording, value)) Raise(nameof(Status));
        }
    }

    public int ClipCount
    {
        get => _clipCount;
        set
        {
            if (Set(ref _clipCount, value)) Raise(nameof(ClipCountText));
        }
    }

    // Off Windows nothing can be looked up, so every game counts as installed.
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (!Set(ref _isInstalled, value)) return;
            Raise(nameof(Status));
            Raise(nameof(StoreText));
            Raise(nameof(StoreTag));
        }
    }

    // The store it was found through, like Steam or Battle.net.
    public string? Store
    {
        get => _store;
        set
        {
            if (!Set(ref _store, value)) return;
            Raise(nameof(StoreText));
            Raise(nameof(StoreTag));
        }
    }

    public string StoreText => Store ?? (IsInstalled ? "Supported" : "Not installed");
    public string StoreTag => StoreText.ToUpperInvariant();

    public string ClipCountText => ClipCount == 1 ? "1 clip" : $"{ClipCount} clips";
    public string Status => IsRecording ? "Recording now" : IsRunning ? "Running" : IsInstalled ? "Ready to detect" : "Not installed";
}

public sealed record FpsOption(int Value, string Label)
{
    public override string ToString() => Label;
}

// One choice in Settings' resolution list. Ones above the plan's (or a special key's) are shown with the plan that
// has them.
public sealed class ResolutionOption(int value) : ObservableObject
{
    private int _cap = Plans.Free.Lines;

    public int Value { get; } = value;
    public bool IsAvailable => Value <= 0 || Value <= _cap;

    public string Label => Value switch
    {
        <= 0 => $"Best for plan ({Resolutions.Name(_cap)})",
        2160 => IsAvailable ? "4K (2160p)" : "4K (2160p) · Ultra",
        _ when !IsAvailable => $"{Resolutions.Name(Value)} · {Resolutions.Needs(Value)}",
        _ => Resolutions.Name(Value),
    };

    // cap: the highest resolution the plan or a special key allows.
    internal void Refresh(int cap)
    {
        _cap = cap;
        Raise(string.Empty);
    }

    public override string ToString() => Label;
}

internal static class Palettes
{
    private static readonly (Color A, Color B)[] Pairs =
    [
        (Color.Parse("#B45CF6"), Color.Parse("#22C8EE")),
        (Color.Parse("#FF5E9C"), Color.Parse("#FFB457")),
        (Color.Parse("#3EE0A6"), Color.Parse("#2F7BFF")),
        (Color.Parse("#7C6CFF"), Color.Parse("#FF5EC4")),
        (Color.Parse("#22C8EE"), Color.Parse("#3EE0A6")),
        (Color.Parse("#FFB457"), Color.Parse("#FF4D7A")),
    ];

    public static IBrush Gradient(int index)
    {
        var (a, b) = Pairs[Math.Abs(index) % Pairs.Length];
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };
    }

    // A dark plate in the game's first colour, behind its icon.
    public static IBrush Plate(int index)
    {
        var (a, _) = Pairs[Math.Abs(index) % Pairs.Length];
        return new ImmutableSolidColorBrush(Color.FromRgb((byte)(a.R * 0.22 + 14), (byte)(a.G * 0.22 + 14), (byte)(a.B * 0.22 + 16)));
    }

    public static int IndexFor(string name)
    {
        var hash = 0;
        foreach (var c in name) hash = unchecked(hash * 31 + char.ToLowerInvariant(c));
        return hash;
    }
}

internal static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
                          : $"{(int)d.TotalMinutes}:{d.Seconds:00}";

    public static string When(DateTime time)
    {
        var today = DateTime.Today;
        var t = time.ToString("t", CultureInfo.CurrentCulture);
        if (time.Date == today) return $"Today, {t}";
        if (time.Date == today.AddDays(-1)) return $"Yesterday, {t}";
        return time.ToString("MMM d, ", CultureInfo.CurrentCulture) + t;
    }

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }
}
