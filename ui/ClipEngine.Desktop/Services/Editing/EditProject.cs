using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services.Editing;

// An edit kept on disk, so closing the editor and opening the clip again picks up where it was
// left: %LOCALAPPDATA%\Clip_Engine\edits\<id>.json, one per clip. Only the cuts are kept; the
// clip itself is never changed.
internal sealed class EditProject
{
    // Bumped when the file gains something an older app would get wrong.
    public const int CurrentVersion = 1;

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("sources")] public List<EditProjectSource> Sources { get; set; } = [];
    [JsonPropertyName("segments")] public List<EditProjectSegment> Segments { get; set; } = [];
    [JsonPropertyName("saved")] public DateTimeOffset Saved { get; set; }

    public static EditProject From(EditTimeline timeline, DateTimeOffset now) => new()
    {
        Sources = timeline.Sources.Select(s => new EditProjectSource { Path = s.Path, Duration = s.Duration }).ToList(),
        Segments = timeline.Segments.Select(s => new EditProjectSegment { Source = s.Source, From = s.From, To = s.To }).ToList(),
        Saved = now,
    };

    // The timeline this edit describes, if it still fits the clip: same file, same length. A clip
    // replaced by another of the same name starts a fresh edit.
    public EditTimeline? ToTimeline(string clip, double duration)
    {
        if (Version > CurrentVersion || Segments.Count == 0) return null;
        var source = Sources.FirstOrDefault(s => string.Equals(s.Path, clip, StringComparison.OrdinalIgnoreCase));
        if (source is null || Math.Abs(source.Duration - duration) > 0.5) return null;
        var timeline = new EditTimeline(
            Sources.Select(s => new EditSource(s.Path, s.Duration)),
            Segments.Select(s => new EditSegment(s.Source, s.From, s.To)));
        return timeline.Segments.Count == 0 ? null : timeline;
    }

    public static string FolderIn(string dataDir) => Path.Combine(dataDir, "edits");

    // The file for a clip's edit, named from its path so any clip name is fine.
    public static string PathFor(string dataDir, string clip)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(clip).ToUpperInvariant()));
        return Path.Combine(FolderIn(dataDir), Convert.ToHexString(hash, 0, 12).ToLowerInvariant() + ".json");
    }

    public static EditProject? Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize(File.ReadAllText(file), EditProjectJson.Default.EditProject) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // Writes beside the old file and swaps it in, so a crash mid-save keeps the last good edit.
    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, EditProjectJson.Default.EditProject));
        File.Move(temp, file, overwrite: true);
    }

    public static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class EditProjectSource
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("duration")] public double Duration { get; set; }
}

internal sealed class EditProjectSegment
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("from")] public double From { get; set; }
    [JsonPropertyName("to")] public double To { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EditProject))]
internal sealed partial class EditProjectJson : JsonSerializerContext;
