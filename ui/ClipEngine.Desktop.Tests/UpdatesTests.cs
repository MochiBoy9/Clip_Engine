using System.IO.Compression;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class UpdatesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClipEngineUpdates-" + Guid.NewGuid().ToString("N"));

    public UpdatesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData("v0.5.1", "0.5.1")]
    [InlineData("0.6.0", "0.6.0")]
    [InlineData("v1.2", "1.2.0")]
    [InlineData("v0.6.0-beta", "0.6.0")]
    public void ParsesTags(string tag, string expected) => Assert.Equal(Version.Parse(expected), UpdateFeed.ParseVersion(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v7")]
    public void RejectsOtherTags(string tag) => Assert.Null(UpdateFeed.ParseVersion(tag));

    [Fact]
    public void ThisBuildHasAVersion() => Assert.True(UpdateFeed.CurrentVersion > new Version(0, 5, 0));

    [Fact]
    public void FindsTheWindowsZip()
    {
        const string json = """
            {
              "tag_name": "v0.6.0", "draft": false, "prerelease": false,
              "html_url": "https://github.com/MochiBoy9/Clip_Engine/releases/tag/v0.6.0",
              "body": "## Clip Engine 0.6.0\n\n- **Faster.** Lots.\n- A plain line\n\nInstall it.",
              "assets": [
                { "name": "1-home.png", "url": "https://api.github.com/repos/x/y/releases/assets/1", "size": 10 },
                { "name": "Clip_Engine-0.6.0-windows-x64.zip", "url": "https://api.github.com/repos/x/y/releases/assets/2", "size": 41000000 }
              ]
            }
            """;
        var release = UpdateFeed.ParseRelease(json);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 6, 0), release.Version);
        Assert.Equal("Clip_Engine-0.6.0-windows-x64.zip", release.AssetName);
        Assert.EndsWith("/assets/2", release.AssetUrl);
        Assert.Equal(41000000, release.AssetBytes);
        Assert.Equal("Faster · A plain line", UpdatesViewModel.Summarize(release.Notes));
    }

    [Fact]
    public void TakesTheZipUnderItsFixedName()
    {
        // Releases from 0.11.0 on carry one zip named the same every time; the tag says the version.
        var release = UpdateFeed.ParseRelease("""
            { "tag_name": "v0.11.0", "assets": [ { "name": "Clip_Engine-windows-x64.zip", "url": "u", "size": 42000000 } ] }
            """);
        Assert.NotNull(release);
        Assert.Equal(new Version(0, 11, 0), release.Version);
        Assert.Equal("Clip_Engine-windows-x64.zip", release.AssetName);
    }

    [Fact]
    public void SkipsReleasesWithoutAZipAndPrereleases()
    {
        Assert.Null(UpdateFeed.ParseRelease("""{ "tag_name": "v0.6.0", "assets": [ { "name": "notes.txt", "url": "u" } ] }"""));
        Assert.Null(UpdateFeed.ParseRelease(
            """{ "tag_name": "v0.6.0", "prerelease": true, "assets": [ { "name": "Clip_Engine-0.6.0-windows-x64.zip", "url": "u" } ] }"""));
        Assert.Null(UpdateFeed.ParseRelease("""{ "message": "Not Found" }"""));
    }

    [Theory]
    [InlineData("MochiBoy9/Clip_Engine", true)]
    [InlineData("someone/clip-engine.downloads", true)]
    [InlineData("MochiBoy9", false)]
    [InlineData("a/b/c", false)]
    [InlineData("https://github.com/a/b", false)]
    public void ChecksRepositoryNames(string text, bool ok) => Assert.Equal(ok, UpdatesViewModel.IsRepo(text));

    [Fact]
    public void UnpacksAReleaseZip()
    {
        var zip = MakeRelease("new");
        var root = UpdateInstaller.Unpack(zip, Path.Combine(_dir, "staged"));
        Assert.True(File.Exists(Path.Combine(root, "ClipEngine.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(root, "detection.ini")));
    }

    [Fact]
    public void RefusesZipsThatArentReleases()
    {
        var zip = Path.Combine(_dir, "other.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) Write(archive, "readme.txt", "hi");
        Assert.Throws<UpdateException>(() => UpdateInstaller.Unpack(zip, Path.Combine(_dir, "staged")));

        var broken = Path.Combine(_dir, "broken.zip");
        File.WriteAllText(broken, "not a zip");
        Assert.Throws<UpdateException>(() => UpdateInstaller.Unpack(broken, Path.Combine(_dir, "staged")));
    }

    [Fact]
    public void ReplacesTheInstalledFilesAndTidiesUpLater()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "ClipEngine.exe"), "old");
        File.WriteAllText(Path.Combine(install, "clip_engine.exe"), "old");
        File.WriteAllText(Path.Combine(install, "LICENSE.txt"), "kept");

        var staged = UpdateInstaller.Unpack(MakeRelease("new"), Path.Combine(_dir, "staged"));
        UpdateInstaller.Install(staged, install);

        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "ClipEngine.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "clip_engine.exe")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "detection.ini")));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(install, "LICENSE.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "ClipEngine.exe.old")));

        // A second update while the first one's leftovers are still there.
        var again = UpdateInstaller.Unpack(MakeRelease("newer"), Path.Combine(_dir, "staged"));
        UpdateInstaller.Install(again, install);
        Assert.Equal("newer", File.ReadAllText(Path.Combine(install, "ClipEngine.exe")));

        UpdateInstaller.CleanUp(install);
        Assert.Empty(Directory.EnumerateFiles(install, "*.old"));
        Assert.True(File.Exists(Path.Combine(install, "LICENSE.txt")));
    }

    [Fact]
    public void PutsEverythingBackWhenAFileCantBeReplaced()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "ClipEngine.exe"), "old");
        // A folder where the release has a file: copying over it fails part way.
        Directory.CreateDirectory(Path.Combine(install, "detection.ini"));

        var staged = UpdateInstaller.Unpack(MakeRelease("new"), Path.Combine(_dir, "staged"));
        Assert.Throws<UpdateException>(() => UpdateInstaller.Install(staged, install));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "ClipEngine.exe")));
        Assert.False(File.Exists(Path.Combine(install, "clip_engine.exe")));
        Assert.Empty(Directory.EnumerateFiles(install, "*.old"));
    }

    // A zip laid out like the Release workflow's: everything inside a Clip_Engine folder.
    private string MakeRelease(string content)
    {
        var zip = Path.Combine(_dir, $"Clip_Engine-{content}-windows-x64.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var name in new[] { "ClipEngine.exe", "clip_engine.exe", "detection.ini" }) Write(archive, $"Clip_Engine/{name}", content);
        return zip;
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }
}
