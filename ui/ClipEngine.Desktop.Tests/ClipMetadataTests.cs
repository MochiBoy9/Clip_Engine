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
    public void KeepsHowEachMomentWasFound()
    {
        const string withWitnesses = """
            {
              "version": 2, "game": "The Finals", "trigger": "auto", "seconds": 20.00, "title": "1 kill",
              "events": [
                { "type": "kill", "time": 15.50, "text": "SMEDC CAPN OSPUZE MRBEAN", "found_by": [
                  { "rule": "the_finals.kill.killfeed", "text": "SMEDC CAPN OSPUZE MRBEAN", "offset": 0.00, "counted": true, "box": [0.7000, 0.0300, 0.3000, 0.2000] },
                  { "rule": "the_finals.kill.score_line", "text": "ELIMINATION + 250", "offset": 0.49, "counted": false, "box": [0.3000, 0.5200, 0.4000, 0.2400] }
                ] },
                { "type": "death", "time": 18.00, "text": "ELIMINATED BY" }
              ]
            }
            """;
        var m = ClipMetadataFile.Parse(withWitnesses);
        Assert.NotNull(m);
        var found = m.Events[0].FoundBy;
        Assert.NotNull(found);
        Assert.Equal(2, found.Count);
        Assert.True(found[0].Counted);
        Assert.False(found[1].Counted);
        Assert.Equal(0.49, found[1].Offset);
        Assert.Equal([0.3, 0.52, 0.4, 0.24], found[1].Box);
        Assert.Null(m.Events[1].FoundBy);

        // Saving the clip's metadata (a favorite, marks) keeps them.
        var clip = Path.Combine(_dir, "kept.mp4");
        ClipMetadataFile.WriteSidecar(clip, m);
        var again = ClipMetadataFile.ReadSidecar(clip);
        Assert.Equal(2, again?.Events[0].FoundBy?.Count);
        Assert.DoesNotContain("found_by\": null", File.ReadAllText(ClipMetadataFile.SidecarPath(clip)));

        var marker = new ViewModels.MarkerViewModel("kill", 15.5, "", new ViewModels.RelayCommand(() => { })) { FoundBy = found };
        Assert.True(marker.IsConfirmed);
        Assert.Equal("Killfeed", marker.Witnesses[0].Name);
        Assert.Equal("Counted from the killfeed, and the score line saw it too.", marker.WhySummary);
        Assert.Equal("Agreed · +0.5s", marker.Witnesses[1].Detail);
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

    [Fact]
    public void ReadsMatchTagsAndKeepsFavoritesInTheSidecar()
    {
        const string json = """
            { "version": 2, "game": "Overwatch", "trigger": "auto", "seconds": 15.00,
              "session": "2026-10-27T20:41:05", "session_seconds": 812.50, "match": 3,
              "title": "", "events": [] }
            """;
        var m = ClipMetadataFile.Parse(json);
        Assert.NotNull(m);
        Assert.Equal(("2026-10-27T20:41:05", 812.5, 3), (m.Session, m.SessionSeconds, m.Match));
        Assert.True(m.HasSession);
        Assert.False(m.Favorite);
        Assert.False(ClipMetadataFile.Parse(Json)!.HasSession);

        var clip = Path.Combine(_dir, "clip.mp4");
        m.Favorite = true;
        ClipMetadataFile.WriteSidecar(clip, m);
        var back = ClipMetadataFile.ReadSidecar(clip);
        Assert.True(back?.Favorite);
        Assert.Equal(3, back?.Match);
    }

    [Fact]
    public void OneKillReadTwiceIsOneMoment()
    {
        var metadata = ClipMetadataFile.Parse("""
            {
              "title": "3 kills, 1 assist",
              "events": [
                { "type": "kill", "time": 4.0, "text": "ENTERRO ELIMINATED VEX" },
                { "type": "kill", "time": 6.5, "text": "ENTERRO ELIMINATED VEX" },
                { "type": "assist", "time": 7.0, "text": "ASSIST VEX" },
                { "type": "kill", "time": 9.0, "text": "ENTERRO ELIMINATED MIRA" }
              ]
            }
            """)!;

        Assert.Equal(["kill", "assist", "kill"], metadata.Events.Select(e => e.Type));
        Assert.Equal("2 kills, 1 assist", metadata.Title);
    }
}

