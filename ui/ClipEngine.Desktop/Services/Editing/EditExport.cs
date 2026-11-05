using System.Globalization;

namespace ClipEngine.Desktop.Services.Editing;

// How big the exported video is. It's never made bigger than the clip it's cut from.
public enum ExportQuality { Original, P1080, P720 }

public sealed record ExportQualityOption(ExportQuality Quality, string Label, string Detail)
{
    public override string ToString() => Label;
}

// Saves an edit as a new clip: every part in order, re-encoded into one H.264 MP4 at the
// quality picked, with the moments from each part moved to where they land
// (clip_engine.exe --montage, src/media/Exporter.cpp). The clips it's cut from stay as they are.
internal static class EditExport
{
    public static IReadOnlyList<ExportQualityOption> Qualities { get; } =
    [
        new(ExportQuality.Original, "Original", "Same size as the clip, at a high bitrate"),
        new(ExportQuality.P1080, "1080p", "Up to 1920×1080, 16 Mbps"),
        new(ExportQuality.P720, "720p", "Up to 1280×720, 8 Mbps, quick to upload"),
    ];

    // The engine's arguments for the export.
    public static IReadOnlyList<string> Arguments(EditTimeline timeline, string output, ExportQuality quality)
    {
        var arguments = new List<string> { "--montage", output };
        foreach (var s in timeline.Segments)
        {
            arguments.Add("--part");
            arguments.Add(string.Create(CultureInfo.InvariantCulture, $"{s.Source}|{s.From:0.000}|{s.To:0.000}"));
        }
        var (height, mbps) = quality switch
        {
            ExportQuality.P1080 => ("1080", "16"),
            ExportQuality.P720 => ("720", "8"),
            // The engine picks the bitrate from the clip's own size.
            _ => ("2160", "auto"),
        };
        arguments.AddRange(["--height", height, "--mbps", mbps, "--kind", "edit"]);
        return arguments;
    }

    // Where the export goes: next to the first clip, as <name>_edit.mp4 (or _edit_2 and so on).
    public static string OutputFor(EditTimeline timeline)
    {
        var first = timeline.Segments[0].Source;
        return ClipSharing.UniquePath(Path.GetDirectoryName(first)!, $"{Path.GetFileNameWithoutExtension(first)}_edit.mp4");
    }

    // Exports the edit, reporting progress from 0 to 100. Returns the new clip's path.
    public static async Task<string> RunAsync(EditTimeline timeline, ExportQuality quality, Action<int> progress,
                                              CancellationToken cancel = default)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(AppPaths.EngineExe))
            throw new EditFailedException("Exporting needs clip_engine.exe next to the app.");
        if (timeline.Segments.Count == 0 || timeline.Duration < EditTimeline.MinSegment)
            throw new EditFailedException("There's nothing left in the edit to export.");
        foreach (var source in timeline.Sources)
        {
            if (!File.Exists(source.Path)) throw new EditFailedException($"{Path.GetFileName(source.Path)} isn't there any more.");
        }

        var output = OutputFor(timeline);
        string? saved = null;
        try
        {
            var (exitCode, failure) = await EngineCommand.RunAsync(Arguments(timeline, output, quality), e =>
            {
                if (e is MontageProgress p) progress(p.Percent);
                if (e is HighlightSaved h) saved = h.Path;
            }, cancel);
            if (exitCode != 0 || saved is null || !File.Exists(output))
            {
                var reason = failure?.Replace("Highlight failed: ", "") ?? $"the engine exited with code {exitCode}";
                throw new EditFailedException($"Couldn't export the edit: {reason}");
            }
        }
        catch (OperationCanceledException)
        {
            // The engine writes to <output>.part and only renames it when done.
            try { File.Delete(output + ".part"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
        return output;
    }
}
