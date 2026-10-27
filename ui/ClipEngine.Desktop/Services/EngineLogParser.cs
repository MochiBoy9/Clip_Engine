using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipEngine.Desktop.Services;

internal enum LogLevel { Info, Warn, Error }

internal sealed record LogLine(DateTime Time, LogLevel Level, string Message);

// Something the engine reported that the UI reacts to.
internal abstract record EngineEvent;
internal sealed record EngineStarted(string Version) : EngineEvent;
internal sealed record SupportedGames(IReadOnlyList<string> Names) : EngineEvent;
internal sealed record ClipsFolder(string Path) : EngineEvent;
internal sealed record SaveHotkey(string Hotkey, int ClipSeconds) : EngineEvent;
internal sealed record GameDetected(string Game) : EngineEvent;
internal sealed record GameClosed(string Game) : EngineEvent;
internal sealed record RecordingStarted(string Label, int Width, int Height, int Fps, string Codec, int Mbps, string Encoder)
    : EngineEvent;
internal sealed record RecordingStopped(string Label) : EngineEvent;
internal sealed record ClipSaved(string Path, double Seconds, double Megabytes) : EngineEvent;
internal sealed record MomentDetected(string Type, string Game, string Text) : EngineEvent;
internal sealed record ExportProgress(int Percent) : EngineEvent;
internal sealed record Exported(string Path, double Megabytes) : EngineEvent;

// Reads the lines clip_engine.exe writes, in the format of src/core/Log.cpp.
internal static partial class EngineLogParser
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (INFO|WARN|ERROR)\s+(.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^Clip Engine (\S+) starting$")]
    private static partial Regex StartingRegex();

    [GeneratedRegex(@"^Waiting for a supported game: (.+)$")]
    private static partial Regex WaitingRegex();

    [GeneratedRegex(@"^Clips go to: (.+)$")]
    private static partial Regex ClipsGoToRegex();

    [GeneratedRegex(@"^Press (.+) to save the last (\d+) seconds$")]
    private static partial Regex PressRegex();

    [GeneratedRegex(@"^Recording (.+): (\d+)x(\d+) at (\d+) fps, (\S+) (\d+) Mbps on (.+)$")]
    private static partial Regex RecordingRegex();

    [GeneratedRegex(@"^Stopped recording (.+)$")]
    private static partial Regex StoppedRegex();

    [GeneratedRegex(@"^Saved ([\d.]+)s clip \(([\d.]+) MB\) to (.+)$")]
    private static partial Regex SavedRegex();

    [GeneratedRegex(@"^Detected (\S+) in (.+?): (.*)$")]
    private static partial Regex MomentRegex();

    [GeneratedRegex(@"^Export progress (\d+)%$")]
    private static partial Regex ExportProgressRegex();

    [GeneratedRegex(@"^Exported ([\d.]+) MB to (.+)$")]
    private static partial Regex ExportedRegex();

    [GeneratedRegex(@"^(.+) (detected|closed)$")]
    private static partial Regex GameRegex();

    public static LogLine? ParseLine(string line)
    {
        var m = LineRegex().Match(line.TrimEnd('\r', '\n'));
        if (!m.Success) return null;
        var time = DateTime.ParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var level = m.Groups[2].Value switch
        {
            "WARN" => LogLevel.Warn,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Info,
        };
        return new LogLine(time, level, m.Groups[3].Value);
    }

    public static EngineEvent? ParseEvent(LogLine line)
    {
        if (line.Level != LogLevel.Info) return null;
        var text = line.Message;
        Match m;

        if ((m = StartingRegex().Match(text)).Success) return new EngineStarted(m.Groups[1].Value);
        if ((m = WaitingRegex().Match(text)).Success)
            return new SupportedGames(m.Groups[1].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries));
        if ((m = ClipsGoToRegex().Match(text)).Success) return new ClipsFolder(m.Groups[1].Value);
        if ((m = PressRegex().Match(text)).Success) return new SaveHotkey(m.Groups[1].Value, Int(m.Groups[2]));
        if ((m = RecordingRegex().Match(text)).Success)
            return new RecordingStarted(m.Groups[1].Value, Int(m.Groups[2]), Int(m.Groups[3]), Int(m.Groups[4]),
                                        m.Groups[5].Value, Int(m.Groups[6]), m.Groups[7].Value);
        if ((m = StoppedRegex().Match(text)).Success) return new RecordingStopped(m.Groups[1].Value);
        if ((m = SavedRegex().Match(text)).Success)
            return new ClipSaved(m.Groups[3].Value, Double(m.Groups[1]), Double(m.Groups[2]));
        if ((m = MomentRegex().Match(text)).Success) return new MomentDetected(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        if ((m = ExportProgressRegex().Match(text)).Success) return new ExportProgress(Int(m.Groups[1]));
        if ((m = ExportedRegex().Match(text)).Success) return new Exported(m.Groups[2].Value, Double(m.Groups[1]));
        if ((m = GameRegex().Match(text)).Success)
            return m.Groups[2].Value == "detected" ? new GameDetected(m.Groups[1].Value) : new GameClosed(m.Groups[1].Value);
        return null;
    }

    private static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);
    private static double Double(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
}
