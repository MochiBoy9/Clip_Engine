using System.Globalization;

namespace ClipEngine.Desktop.Services;

internal sealed class EditFailedException(string message) : Exception(message);

internal static class ClipEditing
{
    // Saves the part of a clip between start and end as a new clip next to it, without
    // re-encoding (clip_engine.exe --trim, src/media/Trimmer.cpp). Returns the new clip's path.
    public static async Task<string> TrimAsync(string clip, double start, double end, CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(AppPaths.EngineExe))
            throw new EditFailedException("Trimming needs clip_engine.exe next to the app.");
        if (end - start < 0.5) throw new EditFailedException("Pick a part at least half a second long.");

        var folder = Path.GetDirectoryName(clip)!;
        var output = ClipSharing.UniquePath(folder, $"{Path.GetFileNameWithoutExtension(clip)}_trim.mp4");
        Trimmed? trimmed = null;
        var (exitCode, failure) = await EngineCommand.RunAsync(
            ["--trim", clip, output, "--start", Seconds(start), "--end", Seconds(end)],
            e => trimmed ??= e as Trimmed,
            cancel);

        if (exitCode != 0 || trimmed is null || !File.Exists(output))
        {
            var reason = failure?.Replace("Trim failed: ", "") ?? $"the engine exited with code {exitCode}";
            throw new EditFailedException($"Couldn't trim the clip: {reason}");
        }
        return output;
    }

    private static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
