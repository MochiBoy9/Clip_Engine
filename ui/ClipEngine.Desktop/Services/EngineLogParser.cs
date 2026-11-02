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
// With game focus on: another window came in front of the game, so the recording shows
// "Game Not Found" until the game is back.
internal sealed record GameAway(string Game) : EngineEvent;
internal sealed record GameBack(string Game) : EngineEvent;
internal sealed record ClipSaved(string Path, double Seconds, double Megabytes) : EngineEvent;
internal sealed record MomentDetected(string Type, string Game, string Text) : EngineEvent;
internal sealed record ExportProgress(int Percent) : EngineEvent;
internal sealed record Exported(string Path, double Megabytes) : EngineEvent;
internal sealed record Trimmed(string Path, double Seconds, double From) : EngineEvent;
internal sealed record FrameSaved(string Path, int Width, int Height) : EngineEvent;
internal sealed record VideoLength(double Seconds) : EngineEvent;
internal sealed record TextRead(string Text) : EngineEvent;
internal sealed record RuleMatches(int Count) : EngineEvent;
internal sealed record RecordHotkey(string Hotkey) : EngineEvent;
internal sealed record LongRecordingStarted(string Game) : EngineEvent;
internal sealed record LongRecordingStopped(double Seconds, string? Reason) : EngineEvent;
internal sealed record LongRecordingSaved(string Path, double Seconds, double Megabytes) : EngineEvent;
// --scan: a moment the game's rules found in a clip, how far it has got, and the end.
internal sealed record ScanFound(string Type, double Seconds, string Text) : EngineEvent;
internal sealed record ScanProgress(int Percent) : EngineEvent;
internal sealed record ScanDone(int Count) : EngineEvent;
// --learn: a line the universal HUD reader was taught from the player's marks, and how it went.
internal sealed record LearnExample(string Label, string Zone, string Text) : EngineEvent;
internal sealed record LearnProgress(int Percent) : EngineEvent;
internal sealed record LearnDone(int Added) : EngineEvent;
// --scan: a rule that saw the moment found just before, and whether it was the one that counted it.
internal sealed record ScanWitness(string Rule, double Offset, bool Counted, string Text) : EngineEvent;
// --track: what the box read at a time (when it changed), how far it has got, and the end.
internal sealed record TrackRead(double Seconds, int Matches, string Text) : EngineEvent;
internal sealed record TrackProgress(int Percent) : EngineEvent;
internal sealed record TrackDone(int Reads) : EngineEvent;
// --cut-sound: where the sound was saved.
internal sealed record SoundSaved(string Path) : EngineEvent;
// The player's totals in the match, from the game's API or its scoreboard.
internal sealed record ScoreboardRead(string Game, IReadOnlyDictionary<string, int> Stats) : EngineEvent;

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

    [GeneratedRegex(@"^Trimmed ([\d.]+)s from ([\d.]+)s to (.+)$")]
    private static partial Regex TrimmedRegex();

    [GeneratedRegex(@"^Frame (\d+)x(\d+) saved to (.+)$")]
    private static partial Regex FrameRegex();

    [GeneratedRegex(@"^Video length: ([\d.]+)$")]
    private static partial Regex VideoLengthRegex();

    [GeneratedRegex(@"^Read: ?(.*)$")]
    private static partial Regex ReadRegex();

    [GeneratedRegex(@"^Matches: (\d+)$")]
    private static partial Regex MatchesRegex();

    [GeneratedRegex(@"^Press (.+) to start or stop a long recording$")]
    private static partial Regex RecordHotkeyRegex();

    [GeneratedRegex(@"^Long recording started in (.+); press .+ again to stop it$")]
    private static partial Regex LongStartedRegex();

    [GeneratedRegex(@"^Long recording stopped after (\d+)s(?: because (.+))?$")]
    private static partial Regex LongStoppedRegex();

    [GeneratedRegex(@"^Saved ([\d.]+)s long recording \(([\d.]+) MB\) to (.+)$")]
    private static partial Regex LongSavedRegex();

    [GeneratedRegex(@"^Scoreboard in (.+?): (.+)$")]
    private static partial Regex ScoreboardRegex();

    [GeneratedRegex(@"^([a-z0-9_]+) (-?\d+)$")]
    private static partial Regex StatRegex();

    [GeneratedRegex(@"^(.+) isn't in front, so the recording shows Game Not Found until you're back$")]
    private static partial Regex AwayRegex();

    [GeneratedRegex(@"^Back in (.+), recording it again$")]
    private static partial Regex BackRegex();

    [GeneratedRegex(@"^Scan found (\S+) at ([\d.]+): ?(.*)$")]
    private static partial Regex ScanFoundRegex();

    [GeneratedRegex(@"^Scan progress (\d+)%$")]
    private static partial Regex ScanProgressRegex();

    [GeneratedRegex(@"^Scan done: (\d+) moments$")]
    private static partial Regex ScanDoneRegex();

    [GeneratedRegex(@"^Scan witness (\S+) ([+-][\d.]+)s (counted|agreed): ?(.*)$")]
    private static partial Regex ScanWitnessRegex();

    [GeneratedRegex(@"^Learn example (\S+) in (\S*): ?(.*)$")]
    private static partial Regex LearnExampleRegex();

    [GeneratedRegex(@"^Learn progress (\d+)%$")]
    private static partial Regex LearnProgressRegex();

    [GeneratedRegex(@"^Learn done: (\d+) new examples$")]
    private static partial Regex LearnDoneRegex();

    [GeneratedRegex(@"^Track at ([\d.]+): (\d+) \| ?(.*)$")]
    private static partial Regex TrackReadRegex();

    [GeneratedRegex(@"^Track progress (\d+)%$")]
    private static partial Regex TrackProgressRegex();

    [GeneratedRegex(@"^Track done: (\d+) reads$")]
    private static partial Regex TrackDoneRegex();

    [GeneratedRegex(@"^Sound saved to (.+)$")]
    private static partial Regex SoundSavedRegex();

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
        if ((m = TrimmedRegex().Match(text)).Success) return new Trimmed(m.Groups[3].Value, Double(m.Groups[1]), Double(m.Groups[2]));
        if ((m = FrameRegex().Match(text)).Success) return new FrameSaved(m.Groups[3].Value, Int(m.Groups[1]), Int(m.Groups[2]));
        if ((m = VideoLengthRegex().Match(text)).Success) return new VideoLength(Double(m.Groups[1]));
        if ((m = ReadRegex().Match(text)).Success) return new TextRead(m.Groups[1].Value);
        if ((m = MatchesRegex().Match(text)).Success) return new RuleMatches(Int(m.Groups[1]));
        if ((m = RecordHotkeyRegex().Match(text)).Success) return new RecordHotkey(m.Groups[1].Value);
        if ((m = LongStartedRegex().Match(text)).Success) return new LongRecordingStarted(m.Groups[1].Value);
        if ((m = LongStoppedRegex().Match(text)).Success)
            return new LongRecordingStopped(Double(m.Groups[1]), m.Groups[2].Success ? m.Groups[2].Value : null);
        if ((m = LongSavedRegex().Match(text)).Success)
            return new LongRecordingSaved(m.Groups[3].Value, Double(m.Groups[1]), Double(m.Groups[2]));
        if ((m = ScoreboardRegex().Match(text)).Success)
        {
            var stats = new Dictionary<string, int>();
            foreach (var part in m.Groups[2].Value.Split(", "))
            {
                var stat = StatRegex().Match(part);
                if (stat.Success) stats[stat.Groups[1].Value] = Int(stat.Groups[2]);
            }
            if (stats.Count > 0) return new ScoreboardRead(m.Groups[1].Value, stats);
        }
        if ((m = AwayRegex().Match(text)).Success) return new GameAway(m.Groups[1].Value);
        if ((m = BackRegex().Match(text)).Success) return new GameBack(m.Groups[1].Value);
        if ((m = ScanFoundRegex().Match(text)).Success) return new ScanFound(m.Groups[1].Value, Double(m.Groups[2]), m.Groups[3].Value);
        if ((m = ScanProgressRegex().Match(text)).Success) return new ScanProgress(Int(m.Groups[1]));
        if ((m = ScanDoneRegex().Match(text)).Success) return new ScanDone(Int(m.Groups[1]));
        if ((m = ScanWitnessRegex().Match(text)).Success)
            return new ScanWitness(m.Groups[1].Value, Double(m.Groups[2]), m.Groups[3].Value == "counted", m.Groups[4].Value);
        if ((m = LearnExampleRegex().Match(text)).Success) return new LearnExample(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
        if ((m = LearnProgressRegex().Match(text)).Success) return new LearnProgress(Int(m.Groups[1]));
        if ((m = LearnDoneRegex().Match(text)).Success) return new LearnDone(Int(m.Groups[1]));
        if ((m = TrackReadRegex().Match(text)).Success) return new TrackRead(Double(m.Groups[1]), Int(m.Groups[2]), m.Groups[3].Value);
        if ((m = TrackProgressRegex().Match(text)).Success) return new TrackProgress(Int(m.Groups[1]));
        if ((m = TrackDoneRegex().Match(text)).Success) return new TrackDone(Int(m.Groups[1]));
        if ((m = SoundSavedRegex().Match(text)).Success) return new SoundSaved(m.Groups[1].Value);
        if ((m = GameRegex().Match(text)).Success)
            return m.Groups[2].Value == "detected" ? new GameDetected(m.Groups[1].Value) : new GameClosed(m.Groups[1].Value);
        return null;
    }

    private static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);
    private static double Double(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
}
