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

    public bool IsAutomatic => Trigger == "auto";
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
            return JsonSerializer.Deserialize(json, ClipMetadataJson.Default.ClipMetadata);
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
