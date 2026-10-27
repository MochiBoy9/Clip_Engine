using System.Buffers.Binary;
using System.Text;
using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class ClipMetadataTests : IDisposable
{
    // As src/media/ClipMetadata.cpp writes it.
    private const string Json = """
        {
          "version": 1,
          "game": "The Finals",
          "trigger": "auto",
          "seconds": 27.50,
          "title": "2 kills, 1 revive",
          "events": [
            { "type": "kill", "time": 6.20, "text": "ELIMINATED Vortex" },
            { "type": "revive", "time": 12.00, "text": "REVIVED Mochi" },
            { "type": "kill", "time": 19.50, "text": "ELIMINATED \"Rook\"" }
          ]
        }
        """;

    private static readonly byte[] Uuid =
        [0x2f, 0x6c, 0x1c, 0x3e, 0x9b, 0x4a, 0x4d, 0x27, 0x8e, 0x51, 0xc1, 0x1c, 0xe4, 0x61, 0x3a, 0xe0];

    private readonly string _dir = Directory.CreateTempSubdirectory("clip_engine_").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void ParsesEngineJson()
    {
        var m = ClipMetadataFile.Parse(Json);
        Assert.NotNull(m);
        Assert.Equal(("The Finals", "auto", 27.5, "2 kills, 1 revive"), (m.Game, m.Trigger, m.Seconds, m.Title));
        Assert.True(m.IsAutomatic);
        Assert.Equal(3, m.Events.Count);
        Assert.Equal(("revive", 12.0), (m.Events[1].Type, m.Events[1].Time));
        Assert.Equal("ELIMINATED \"Rook\"", m.Events[2].Text);
    }

    [Fact]
    public void BadJsonIsIgnored() => Assert.Null(ClipMetadataFile.Parse("{ not json"));

    [Fact]
    public void ReadsTheCopyInsideTheMp4()
    {
        var clip = WriteMp4("clip.mp4", Json);
        var m = ClipMetadataFile.ReadEmbedded(clip);
        Assert.NotNull(m);
        Assert.Equal("The Finals", m.Game);
        Assert.Equal(3, m.Events.Count);
        // No sidecar, so Read falls back to the embedded copy.
        Assert.Equal("2 kills, 1 revive", ClipMetadataFile.Read(clip)?.Title);
    }

    [Fact]
    public void Mp4WithoutMarkersHasNone() => Assert.Null(ClipMetadataFile.ReadEmbedded(WriteMp4("plain.mp4", null)));

    [Fact]
    public void ImportCopiesTheClipAndRestoresItsMarkers()
    {
        var sent = WriteMp4("The Finals_2026-10-26_21-04-05_10MB.mp4", Json);
        var library = Path.Combine(_dir, "Shared with me");

        var imported = ClipSharing.Import(sent, library);
        Assert.Equal(Path.Combine(library, Path.GetFileName(sent)), imported);
        Assert.True(File.Exists(ClipMetadataFile.SidecarPath(imported)));
        Assert.Equal(3, ClipMetadataFile.ReadSidecar(imported)?.Events.Count);

        // Importing the same file again doesn't overwrite the first copy.
        var again = ClipSharing.Import(sent, library);
        Assert.NotEqual(imported, again);
        Assert.EndsWith("_2.mp4", again);
    }

    // A minimal MP4: ftyp, an empty mdat and, when given, the engine's metadata box at the end.
    private string WriteMp4(string name, string? json)
    {
        using var data = new MemoryStream();
        WriteBox(data, "ftyp", "isom\0\0\0\0isomavc1"u8.ToArray());
        WriteBox(data, "mdat", new byte[64]);
        if (json is not null) WriteBox(data, "uuid", [.. Uuid, .. Encoding.UTF8.GetBytes(json)]);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void WriteBox(Stream stream, string type, byte[] body)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + body.Length));
        Encoding.ASCII.GetBytes(type, header[4..]);
        stream.Write(header);
        stream.Write(body);
    }
}
