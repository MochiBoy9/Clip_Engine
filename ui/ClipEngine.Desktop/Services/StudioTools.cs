using System.Globalization;

namespace ClipEngine.Desktop.Services;

internal sealed class StudioFailedException(string message) : Exception(message);

// Runs Detection Studio's engine commands (src/detect/RuleTester.cpp).
internal static class StudioTools
{
    public static string FrameFile => Path.Combine(AppPaths.DataDir, "Studio", "frame.png");

    public static bool Available => OperatingSystem.IsWindows() && File.Exists(AppPaths.EngineExe);

    // Saves the frame of a clip at `seconds` (or a screenshot as it is) and returns the PNG's path.
    public static async Task<string> GrabFrameAsync(string source, double seconds)
    {
        Require();
        Directory.CreateDirectory(Path.GetDirectoryName(FrameFile)!);
        FrameSaved? saved = null;
        var (exitCode, failure) = await EngineCommand.RunAsync(
            ["--frame", source, FrameFile, "--at", Number(seconds)], e => saved ??= e as FrameSaved);
        if (exitCode != 0 || saved is null) throw new StudioFailedException(Reason(failure, "Frame failed: ", exitCode));
        return FrameFile;
    }

    // Reads the rule's box of the frame like detection does: the text, and how many times its words match.
    public static async Task<(string Text, int Matches)> ReadAsync(string source, double seconds, StudioRule rule)
    {
        Require();
        string? text = null;
        var matches = 0;
        string[] args =
        [
            "--read", source, "--at", Number(seconds), "--region", rule.RegionText.Replace(" ", ""),
            "--threshold", rule.Threshold.ToString(CultureInfo.InvariantCulture), "--words", rule.Words, "--exclude", rule.Exclude,
        ];
        var (exitCode, failure) = await EngineCommand.RunAsync(args, e =>
        {
            if (e is TextRead read) text = read.Text;
            if (e is RuleMatches m) matches = m.Count;
        });
        if (exitCode != 0 || text is null) throw new StudioFailedException(Reason(failure, "Read failed: ", exitCode));
        return (text, matches);
    }

    private static void Require()
    {
        if (!Available) throw new StudioFailedException("Detection Studio needs clip_engine.exe next to the app.");
    }

    private static string Reason(string? failure, string prefix, int exitCode) =>
        failure?.Replace(prefix, "") ?? $"the engine exited with code {exitCode}";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
