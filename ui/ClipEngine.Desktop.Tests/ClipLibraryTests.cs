using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class ClipLibraryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("clip_engine_").FullName;
    private readonly ClipLibrary _library = new();

    public ClipLibraryTests() => _library.Watch(_dir);

    public void Dispose()
    {
        _library.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string AddClip(string game, string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, game)).FullName;
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }

    [Fact]
    public void AnUnchangedClipIsNotReadAgain()
    {
        AddClip("Valorant", "a.mp4");
        var first = _library.Scan();
        var second = _library.Scan();
        Assert.Single(second);
        Assert.Same(first[0], second[0]);
        Assert.Equal("Valorant", second[0].Game);
    }

    [Fact]
    public void ANewSidecarIsPickedUp()
    {
        var path = AddClip("Valorant", "a.mp4");
        Assert.Null(_library.Scan()[0].Metadata);

        var sidecar = ClipMetadataFile.SidecarPath(path);
        File.WriteAllText(sidecar, """{ "version": 1, "game": "Overwatch", "favorite": true }""");
        var clip = _library.Scan()[0];
        Assert.Equal("Overwatch", clip.Game);
        Assert.True(clip.Metadata?.Favorite);

        File.WriteAllText(sidecar, """{ "version": 1, "game": "Overwatch", "favorite": false }""");
        File.SetLastWriteTimeUtc(sidecar, File.GetLastWriteTimeUtc(sidecar).AddSeconds(5));
        Assert.False(_library.Scan()[0].Metadata?.Favorite);
    }

    [Fact]
    public void RemovedClipsAreForgotten()
    {
        var path = AddClip("Valorant", "a.mp4");
        AddClip("Valorant", "b.mp4");
        Assert.Equal(2, _library.Scan().Count);
        File.Delete(path);
        Assert.Equal("b.mp4", Path.GetFileName(Assert.Single(_library.Scan()).Path));
    }
}
