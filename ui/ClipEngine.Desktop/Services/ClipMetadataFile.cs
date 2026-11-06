using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipEngine.Desktop.Services;

internal sealed class ClipEvent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("time")] public double Time { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    // How the engine found it: one entry for each rule that saw it (see Witness in
    // src/detect/GameEvent.h). Null for moments from a game's own API and older clips.
    [JsonPropertyName("found_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ClipWitness>? FoundBy { get; set; }
}

internal sealed class ClipWitness
{
    [JsonPropertyName("rule")] public string Rule { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    // Seconds from the moment's time to when this rule saw it.
    [JsonPropertyName("offset")] public double Offset { get; set; }
    // False when another rule's count won, or it saw the same moment again.
    [JsonPropertyName("counted")] public bool Counted { get; set; } = true;
    // The box it read: left, top, width, height as fractions of the frame.
    [JsonPropertyName("box")] public double[] Box { get; set; } = [];
}

// What the engine knows about a clip: see src/media/ClipMetadata.cpp.
internal sealed class ClipMetadata
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("game")] public string Game { get; set; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";
    [JsonPropertyName("seconds")] public double Seconds { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("events")] public List<ClipEvent> Events { get; set; } = [];

    // The recording the clip came from (when it started), where the clip starts in it, and
    // which match of that recording it belongs to (0 when no match start was detected).
    [JsonPropertyName("session")] public string Session { get; set; } = "";
    [JsonPropertyName("session_seconds")] public double SessionSeconds { get; set; } = -1;
    [JsonPropertyName("match")] public int Match { get; set; }

    // When the first frame was recorded (Unix ms, 0 when unknown), and the squad moment the clip
    // shows, for squad sync. See ClipMetadata in src/media/ClipMetadata.h.
    [JsonPropertyName("utc_start")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long UtcStart { get; set; }

    [JsonPropertyName("squad_moment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SquadMoment { get; set; }

    // The clip's audio tracks in order: "all" (every source mixed, what players play) when there's more than one,
    // then "game" (or "system", everything heard), "mic", "discord" and "apps". Null for clips from before 0.15,
    // which have game audio and then the mic.
    [JsonPropertyName("audio_tracks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AudioTracks { get; set; }

    // Starred in the app. Favorites are never removed by the storage cleanup.
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }

    // The player's own marks of what really happened in the clip, made in the player's "Check
    // detection" panel, to score detection against. Null until the clip has been marked.
    [JsonPropertyName("labels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ClipEvent>? Labels { get; set; }

    // Two reads of one killfeed line used to be saved as two moments: a repeat of the same
    // moment with the same text a few seconds later is the same kill, so it's dropped (and the
    // title recounted). Clips saved by newer engines don't have these.
    private const double RepeatSeconds = 8;

    internal void MergeRepeats()
    {
        var kept = new List<ClipEvent>();
        foreach (var e in Events.OrderBy(e => e.Time))
        {
            var text = e.Text.Trim();
            var repeat = text.Length > 0 && kept.Any(k => k.Type == e.Type && e.Time - k.Time <= RepeatSeconds
                                                         && string.Equals(k.Text.Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (!repeat) kept.Add(e);
        }
        if (kept.Count == Events.Count) return;
        Events = kept;
        Title = string.Join(", ", EngineSettings.AllEvents
            .Select(type => (type, count: kept.Count(e => e.Type == type)))
            .Where(t => t.count > 0)
            .Select(t => ViewModels.EventKinds.Count(t.type, t.count).ToLowerInvariant()));
    }

    public bool IsAutomatic => Trigger == "auto";
    public bool IsSquadView => Trigger == "squad";
    public bool IsVoice => Trigger == "voice";
    public bool HasSession => Session.Length > 0 && SessionSeconds >= 0;
}

[JsonSerializable(typeof(ClipMetadata))]
internal sealed partial class ClipMetadataJson : JsonSerializerContext;

// Reads and writes a clip's markers: the <clip>.json next to it, or the copy the engine stores
// inside the .mp4 in a 'uuid' box so it travels with shared files.
internal static class ClipMetadataFile
{
    // Must match kMetadataUuid in src/media/ClipMetadata.cpp.
    private static readonly byte[] Uuid =
        [0x2f, 0x6c, 0x1c, 0x3e, 0x9b, 0x4a, 0x4d, 0x27, 0x8e, 0x51, 0xc1, 0x1c, 0xe4, 0x61, 0x3a, 0xe0];

    public static string SidecarPath(string clip) => Path.ChangeExtension(clip, ".json");

    public static ClipMetadata? Read(string clip) => ReadSidecar(clip) ?? ReadEmbedded(clip);

    public static ClipMetadata? ReadSidecar(string clip)
    {
        try
        {
            var sidecar = SidecarPath(clip);
            return File.Exists(sidecar) ? Parse(File.ReadAllText(sidecar)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static ClipMetadata? Parse(string json)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize(json, ClipMetadataJson.Default.ClipMetadata);
            metadata?.MergeRepeats();
            return metadata;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void WriteSidecar(string clip, ClipMetadata metadata) =>
        File.WriteAllText(SidecarPath(clip), JsonSerializer.Serialize(metadata, ClipMetadataJson.Default.ClipMetadata));

    public static ClipMetadata? ReadEmbedded(string clip)
    {
        try
        {
            using var stream = new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FindBox(stream) is { } json ? Parse(json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindBox(Stream stream)
    {
        Span<byte> header = stackalloc byte[16];
        var end = stream.Length;
        long position = 0;
        while (position + 8 <= end)
        {
            stream.Position = position;
            if (stream.Read(header[..8]) != 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerSize = 8L;
            if (size == 1)
            {
                if (stream.Read(header[8..16]) != 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize || position + size > end) return null;

            if (header[4..8].SequenceEqual("uuid"u8) && size >= headerSize + Uuid.Length && size - headerSize < 1 << 20)
            {
                var body = new byte[size - headerSize];
                stream.Position = position + headerSize;
                stream.ReadExactly(body);
                if (body.AsSpan(0, Uuid.Length).SequenceEqual(Uuid)) return Encoding.UTF8.GetString(body, Uuid.Length, body.Length - Uuid.Length);
            }
            position += size;
        }
        return null;
    }
}
