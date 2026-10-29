using System.Globalization;

namespace ClipEngine.Desktop.Services;

internal sealed class StudioFailedException(string message) : Exception(message);

// Runs Detection Studio's engine commands (src/detect/RuleTester.cpp).
internal static class StudioTools
{
    public static string FrameFile => Path.Combine(AppPaths.DataDir, "Studio", "frame.png");

    public static bool Available => OperatingSystem.IsWindows() && File.Exists(AppPaths.EngineExe);

    // Saves the frame of a video at `seconds` (or a screenshot as it is) and returns the PNG's
    // path, and for a video its length in seconds when it's known.
    public static async Task<(string Png, double? Length)> GrabFrameAsync(string source, double seconds)
    {
        Require();
        Directory.CreateDirectory(Path.GetDirectoryName(FrameFile)!);
        FrameSaved? saved = null;
        double? length = null;
        var (exitCode, failure) = await EngineCommand.RunAsync(["--frame", source, FrameFile, "--at", Number(seconds)], e =>
        {
            saved ??= e as FrameSaved;
            if (e is VideoLength v) length = v.Seconds;
        });
        if (exitCode != 0 || saved is null) throw new StudioFailedException(Reason(failure, "Frame failed: ", exitCode));
        return (FrameFile, length);
    }

    // Reads the rule's box of the frame like detection does at that reading quality (low to ultra):
    // the text, and how many times its words match.
    public static async Task<(string Text, int Matches)> ReadAsync(string source, double seconds, StudioRule rule, string quality,
                                                                  int hudScale = 100)
    {
        Require();
        string? text = null;
        var matches = 0;
        List<string> args =
        [
            "--read", source, "--at", Number(seconds), "--region", rule.RegionText.Replace(" ", ""),
            "--threshold", rule.Threshold.ToString(CultureInfo.InvariantCulture), "--words", rule.Words, "--exclude", rule.Exclude,
            "--position", rule.Position, "--event", rule.Event, "--quality", quality,
            "--hud-scale", hudScale.ToString(CultureInfo.InvariantCulture),
        ];
        // Placed on the frame the way the engine places it on a screen that shape.
        if (rule.Setting("anchor") is { } anchor) args.AddRange(["--anchor", anchor]);
        if (rule.Setting("aspect") is { } aspect) args.AddRange(["--aspect", aspect]);
        // Icons are found next to detection.ini, like the engine looks for them.
        if (rule.Image is { Length: > 0 } image)
        {
            var path = Path.IsPathRooted(image)
                ? image
                : new[] { AppPaths.DataDir, AppContext.BaseDirectory }.Select(d => Path.Combine(d, image)).FirstOrDefault(File.Exists) ?? image;
            args.AddRange(["--image", path]);
            if (rule.Setting("image_match") is { } match) args.AddRange(["--image-match", match]);
            if (rule.Setting("image_screen") is { } screen) args.AddRange(["--image-screen", screen]);
        }
        var (exitCode, failure) = await EngineCommand.RunAsync(args, e =>
        {
            if (e is TextRead read) text = read.Text;
            if (e is RuleMatches m) matches = m.Count;
        });
        if (exitCode != 0 || text is null) throw new StudioFailedException(Reason(failure, "Read failed: ", exitCode));
        return (text, matches);
    }

    // Runs the game's rules over the whole clip (--scan), as detection would have in game, and
    // returns the moments they find. progress gets how far it has got, 0 to 100.
    public static async Task<IReadOnlyList<ClipEvent>> ScanAsync(string clip, string game, string quality, Action<int> progress,
                                                                CancellationToken cancel = default)
    {
        Require();
        var found = new List<ClipEvent>();
        var done = false;
        var (exitCode, failure) = await EngineCommand.RunAsync(["--scan", clip, "--game", game, "--quality", quality], e =>
        {
            if (e is ScanFound f) lock (found) found.Add(new ClipEvent { Type = f.Type, Time = f.Seconds, Text = f.Text });
            if (e is ScanProgress p) progress(p.Percent);
            if (e is ScanDone) done = true;
        }, cancel);
        if (exitCode != 0 || !done) throw new StudioFailedException(Reason(failure, "Scan failed: ", exitCode));
        lock (found) return found.ToList();
    }

    private static void Require()
    {
        if (!Available) throw new StudioFailedException("Detection Studio needs clip_engine.exe next to the app.");
    }

    private static string Reason(string? failure, string prefix, int exitCode) =>
        failure?.Replace(prefix, "") ?? $"the engine exited with code {exitCode}";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
