using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

public enum Page { Home, Clips, Games, Settings }

public sealed record NavItem(Page Page, string Title, StreamGeometry Icon);

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
        FavoriteCommand = new RelayCommand(() => owner?.FavoriteClipCommand.Execute(this));
    }

    public RelayCommand OpenCommand { get; }
    public RelayCommand ShowCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ShareCommand { get; }
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

// A detected moment on the player's timeline.
public sealed class MarkerViewModel(string type, double seconds, string text, RelayCommand seek)
{
    // Jumping to a marker starts a little before it, so you see what led up to it.
    private const double LeadIn = 2;

    public string Type { get; } = type;
    public string Label { get; } = EventKinds.Label(type);
    public string Text { get; } = text;
    public double Seconds { get; } = seconds;
    public double SeekSeconds => Math.Max(0, Seconds - LeadIn);
    public string TimeText { get; } = Format.Duration(TimeSpan.FromSeconds(seconds));
    public IBrush Brush { get; } = EventKinds.Brush(type);
    public RelayCommand SeekCommand { get; } = seek;
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

    public static IBrush Brush(string type) => new SolidColorBrush(Color.Parse(type switch
    {
        "kill" => "#FF4D7A",
        "assist" => "#22C8EE",
        "death" => "#8E97B8",
        "revive" => "#3EE0A6",
        "match_start" or "match_end" => "#FFC24B",
        _ => "#B45CF6",
    }));
}

public sealed class GameItemViewModel(string name, int paletteIndex) : ObservableObject
{
    private bool _isRunning;
    private bool _isRecording;
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

    public string ClipCountText => ClipCount == 1 ? "1 clip" : $"{ClipCount} clips";
    public string Status => IsRecording ? "Recording now" : IsRunning ? "Running" : "Ready to detect";
}

public sealed record FpsOption(int Value, string Label)
{
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
