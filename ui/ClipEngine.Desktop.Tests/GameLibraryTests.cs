using System.Text.RegularExpressions;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Views;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class GameLibraryTests
{
    private static readonly IReadOnlyList<GameTarget> Games =
        GameLibrary.Executables.Select(kv => new GameTarget(kv.Key, kv.Value)).ToList();

    [Fact]
    public void KnowsEveryGameTheEngineDoes()
    {
        // src/game/GameDetector.cpp: {"id", "Name", {L"a.exe", L"b.exe"}, ...}
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "game", "GameDetector.cpp"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var source = File.ReadAllText(Path.Combine(dir.FullName, "src", "game", "GameDetector.cpp"));
        var rows = Regex.Matches(source, "\\{\"[a-z0-9_]+\", \"([^\"]+)\", \\{([^}]*)\\}");
        Assert.NotEmpty(rows);
        foreach (Match row in rows)
        {
            var exes = Regex.Matches(row.Groups[2].Value, "L\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
            Assert.True(GameLibrary.Executables.TryGetValue(row.Groups[1].Value, out var known), row.Groups[1].Value);
            Assert.Equal(exes, known);
        }
        Assert.Equal(rows.Count, GameLibrary.Executables.Count);
    }

    [Theory]
    [InlineData("Battlefield™ 6", null, "Battlefield 6")]
    [InlineData("Tom Clancy's Rainbow Six® Siege", null, "Rainbow Six Siege")]
    [InlineData("VALORANT", null, "Valorant")]
    [InlineData("Overwatch 2", null, "Overwatch")]
    [InlineData("Roblox Player", null, "Roblox")]
    [InlineData("Some Launcher", @"C:\Games\Apex\r5apex_dx12.exe", "Apex Legends")]
    [InlineData("Roblox Studio", null, null)]
    [InlineData("Overwatch Toolkit", null, null)]
    [InlineData("", null, null)]
    public void MatchesStoreEntries(string displayName, string? exe, string? game) =>
        Assert.Equal(game, GameLibrary.Match(Games, displayName, exe));

    [Fact]
    public void ReadsSteamLibraries()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "730" "123" }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLibrary.ParseLibraryFolders(vdf));

        const string acf = """
            "AppState"
            {
                "appid"		"2073850"
                "Universe"		"1"
                "name"		"THE FINALS"
                "installdir"		"The Finals"
            }
            """;
        Assert.Equal((2073850, "THE FINALS", "The Finals"), GameLibrary.ParseAppManifest(acf));
        Assert.Null(GameLibrary.ParseAppManifest("\"AppState\" { \"name\" \"x\" }"));
    }

    [Fact]
    public void ReadsEpicManifests()
    {
        const string item = """
            { "DisplayName": "Fortnite", "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe", "AppName": "Fortnite" }
            """;
        var parsed = GameLibrary.ParseEpicManifest(item);
        Assert.NotNull(parsed);
        Assert.Equal("Fortnite", parsed.Value.Name);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", parsed.Value.Location);
        Assert.EndsWith("FortniteClient-Win64-Shipping.exe", parsed.Value.Exe);
        Assert.Equal("Fortnite", GameLibrary.Match(Games, "Something else", parsed.Value.Exe));
        Assert.Null(GameLibrary.ParseEpicManifest("""{ "DisplayName": "No folder" }"""));
    }

    [Theory]
    [InlineData("\"C:\\Games\\Overwatch\\Overwatch.exe\",0", @"C:\Games\Overwatch\Overwatch.exe")]
    [InlineData(@"C:\Riot Games\VALORANT\live\VALORANT.exe", @"C:\Riot Games\VALORANT\live\VALORANT.exe")]
    [InlineData(@"C:\Games\thing.ico", null)]
    [InlineData("", null)]
    public void ReadsDisplayIcons(string value, string? path) => Assert.Equal(path, GameLibrary.IconPath(value));

    [Theory]
    [InlineData("Valve", "Steam App 730", "Steam")]
    [InlineData("Ubisoft", "Uplay Install 635", "Ubisoft Connect")]
    [InlineData("Blizzard Entertainment", "Overwatch", "Battle.net")]
    [InlineData("Electronic Arts", "{GUID}", "EA app")]
    [InlineData("Riot Games, Inc", "Riot Game valorant.live", "Riot")]
    [InlineData("Someone", "Thing", "Installed")]
    public void NamesTheStore(string publisher, string key, string store) => Assert.Equal(store, GameLibrary.StoreFor(publisher, key));

    [Fact]
    public void FindsTheProgramInItsFolder()
    {
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(root.FullName, "ShooterGame", "Binaries", "Win64"));
            File.WriteAllText(Path.Combine(bin.FullName, "VALORANT-Win64-Shipping.exe"), "");
            File.WriteAllText(Path.Combine(root.FullName, "launcher.exe"), "");
            Assert.Equal(Path.Combine(bin.FullName, "VALORANT-Win64-Shipping.exe"),
                         GameLibrary.FindExe(root.FullName, ["valorant-win64-shipping.exe"]));
            Assert.Null(GameLibrary.FindExe(root.FullName, ["VALORANT-Win64-Shipping.exe"], depth: 1));
            Assert.Null(GameLibrary.FindExe(Path.Combine(root.FullName, "missing"), ["a.exe"]));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void UnknownScanListsEverything()
    {
        Assert.False(GameScan.Unknown.Known);
        Assert.True(GameScan.Unknown.IsInstalled("Anything"));
        var scan = new GameScan(new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase)
        {
            ["Valorant"] = new("Valorant", "Riot", null, null, null),
        }, null, true);
        Assert.True(scan.IsInstalled("valorant"));
        Assert.False(scan.IsInstalled("Fortnite"));
    }
}

public class GameArtTests
{
    [Fact]
    public void FindsOnlyTheSameNameInTheStore()
    {
        const string json = """
            { "total": 3, "items": [
              { "type": "app", "name": "THE FINALS Soundtrack", "id": 1 },
              { "type": "app", "name": "THE FINALS", "id": 2073850 },
              { "type": "app", "name": "The Finals", "id": 3 } ] }
            """;
        Assert.Equal(2073850, GameArt.ParseStoreSearch(json, "The Finals"));
        Assert.Null(GameArt.ParseStoreSearch(json, "Finals"));
        Assert.Null(GameArt.ParseStoreSearch("""{ "total": 0 }""", "The Finals"));
    }

    [Fact]
    public void TakesTheHeaderImage()
    {
        const string json = """{ "730": { "success": true, "data": { "header_image": "https://cdn.example/steam/apps/730/header.jpg?t=1" } } }""";
        Assert.Equal("https://cdn.example/steam/apps/730/header.jpg?t=1", GameArt.ParseHeaderImage(json, 730));
        Assert.Null(GameArt.ParseHeaderImage(json, 570));
        Assert.Null(GameArt.ParseHeaderImage("""{ "730": { "success": false } }""", 730));
        Assert.Null(GameArt.ParseHeaderImage("""{ "730": { "success": true, "data": { "header_image": "http://x/h.jpg" } } }""", 730));
    }

    [Fact]
    public void FindsSteamsCachedArt()
    {
        var steam = Directory.CreateTempSubdirectory();
        try
        {
            var cache = Directory.CreateDirectory(Path.Combine(steam.FullName, "appcache", "librarycache"));
            File.WriteAllText(Path.Combine(cache.FullName, "440_header.jpg"), "");
            var hashed = Directory.CreateDirectory(Path.Combine(cache.FullName, "730", "a1b2c3"));
            File.WriteAllText(Path.Combine(hashed.FullName, "header.jpg"), "");
            Assert.Equal(Path.Combine(cache.FullName, "440_header.jpg"), GameArt.SteamCacheBanner(steam.FullName, 440));
            Assert.Equal(Path.Combine(hashed.FullName, "header.jpg"), GameArt.SteamCacheBanner(steam.FullName, 730));
            Assert.Null(GameArt.SteamCacheBanner(steam.FullName, 570));
        }
        finally { steam.Delete(true); }
    }

    [Fact]
    public void CacheFilesAreSafeNames()
    {
        Assert.Equal("counter_strike_2.banner.jpg", Path.GetFileName(GameArt.CacheFile("Counter-Strike 2", "banner.jpg")));
        Assert.Equal("game.icon.png", Path.GetFileName(GameArt.CacheFile("../..", "icon.png")));
    }

    [Fact]
    public void BarcodesStayTheSameForAWord()
    {
        Assert.Equal(Barcode.Pattern("Games"), Barcode.Pattern("Games"));
        Assert.NotEqual(Barcode.Pattern("Games"), Barcode.Pattern("Clips"));
        Assert.All(Barcode.Pattern("v0.10.0"), w => Assert.InRange(w, 1, 3));
    }
}
