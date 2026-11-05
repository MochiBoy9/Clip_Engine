using System.Globalization;

namespace ClipEngine.Desktop.Services.Stats;

// Whether the frame rate can be read. Windows only shares frame timing with administrators and
// the Performance Log Users group.
internal enum FrameTiming { Reading, NeedsPermission, Unavailable }

// One reading of the game and the PC, taken once a second while the overlay is up.
internal sealed record GameStats
{
    public FrameTiming Frames { get; init; }
    public double? Fps { get; init; }
    public double? OnePercentLow { get; init; }
    public double? CpuPercent { get; init; }
    public double? GpuPercent { get; init; }
    public double? GpuCelsius { get; init; }
    public long? VramUsed { get; init; }
    public long? VramTotal { get; init; }
    public long? RamUsed { get; init; }
    public long? RamTotal { get; init; }
}

// Which readings the overlay shows; see StatsOverlaySettings.
internal sealed record StatsChoice(bool Fps, bool Lows, bool Cpu, bool Gpu, bool GpuTemp, bool Vram, bool Ram);

// How hot a reading is running, for its colour.
internal enum StatLevel { Normal, Warn, Bad }

internal sealed record StatReadout(string Label, string Value, StatLevel Level);

internal static class StatsReadouts
{
    public const string None = "--";

    // The overlay's line, in order. Readings the PC doesn't give keep their place with "--", so the
    // overlay doesn't change size while you play.
    public static IReadOnlyList<StatReadout> For(GameStats stats, StatsChoice show)
    {
        var list = new List<StatReadout>(7);
        if (show.Fps)
        {
            var value = stats.Frames == FrameTiming.NeedsPermission ? "OFF" : Whole(stats.Fps);
            list.Add(new("FPS", value, stats.Fps is { } f ? (f < 30 ? StatLevel.Bad : f < 55 ? StatLevel.Warn : StatLevel.Normal) : StatLevel.Normal));
        }
        if (show.Lows) list.Add(new("1% LOW", stats.Frames == FrameTiming.NeedsPermission ? "OFF" : Whole(stats.OnePercentLow), Low(stats)));
        if (show.Cpu) list.Add(new("CPU", Percent(stats.CpuPercent), Load(stats.CpuPercent)));
        if (show.Gpu) list.Add(new("GPU", Percent(stats.GpuPercent), StatLevel.Normal));
        if (show.GpuTemp) list.Add(new("GPU °C", Whole(stats.GpuCelsius), Heat(stats.GpuCelsius)));
        if (show.Vram) list.Add(new("VRAM", Gigabytes(stats.VramUsed), Share(stats.VramUsed, stats.VramTotal)));
        if (show.Ram) list.Add(new("RAM", Gigabytes(stats.RamUsed), Share(stats.RamUsed, stats.RamTotal)));
        return list;
    }

    private static string Whole(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : None;

    private static string Percent(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(Math.Round(v), 0, 100).ToString("0", CultureInfo.InvariantCulture) + "%" : None;

    private static string Gigabytes(long? bytes) =>
        bytes is { } b and >= 0 ? (b / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + "G" : None;

    // A 1% low far under the average is the stutter players notice.
    private static StatLevel Low(GameStats stats) =>
        stats is { OnePercentLow: { } low, Fps: { } fps } && fps > 0
            ? low < 30 || low < fps * 0.5 ? StatLevel.Bad : low < fps * 0.7 ? StatLevel.Warn : StatLevel.Normal
            : StatLevel.Normal;

    // A CPU this busy is likely holding the game back.
    private static StatLevel Load(double? percent) =>
        percent is >= 95 ? StatLevel.Bad : percent is >= 85 ? StatLevel.Warn : StatLevel.Normal;

    private static StatLevel Heat(double? celsius) =>
        celsius is >= 87 ? StatLevel.Bad : celsius is >= 80 ? StatLevel.Warn : StatLevel.Normal;

    private static StatLevel Share(long? used, long? total) =>
        used is { } u && total is { } t && t > 0
            ? u >= t * 0.95 ? StatLevel.Bad : u >= t * 0.85 ? StatLevel.Warn : StatLevel.Normal
            : StatLevel.Normal;
}
