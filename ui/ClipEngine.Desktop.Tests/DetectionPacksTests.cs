using System.IO.Compression;
using System.Text;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public sealed class DetectionPacksTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ce-packs-" + Guid.NewGuid().ToString("N"));

    private const string Shipped = """
        [detection]
        sample_ms = 250

        [valorant.kill]
        region = 0.7, 0.02, 0.28, 0.2
        words = {player}
        position = start

        [valorant.kill.banner]
        region = 0.42, 0.72, 0.16, 0.16
        image = valorant_kill_banner.png

        [valorant.scoreboard]
        kills = K | KILLS

        [overwatch.kill]
        region = 0.3, 0.55, 0.4, 0.2
        words = ELIMINATED
        """;

    private const string User = """
        # Your changes to Clip Engine's detection rules.

        [game.neon]
        name = Neon Arena
        exe = NeonArena.exe

        [neon.edited]

        [neon.kill.popup]
        region = 0.4, 0.6, 0.2, 0.1
        classify = true

        [neon.kill.chime]
        sound = sounds/neon_zap.wav | sounds/missing.wav
        confirm_only = true
        merge_ms = 4000
        """;

    public DetectionPacksTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sounds"));
        File.WriteAllBytes(Path.Combine(_dir, "valorant_kill_banner.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_dir, "sounds", "neon_zap.wav"), [4, 5, 6, 7]);
        File.WriteAllLines(Path.Combine(_dir, "learned.txt"),
        [
            "# header",
            "neon\tkill\tcenter\tVAPORIZED {P}",
            "valorant\tnone\tcenter\tSPIKE PLANTED",
            "neon\tnone\tbanner\tWAVE CLEARED",
        ]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Find(string name) => Path.Combine(_dir, name);

    [Fact]
    public void BuildsFromUserRulesWithFilesAndLearnedLines()
    {
        var pack = DetectionPacks.Build("neon", "Neon Arena", ["NeonArena.exe"], DetectionIni.Parse(User),
                                        DetectionIni.Parse(Shipped), Path.Combine(_dir, "learned.txt"), Find);
        Assert.Equal("neon", pack.Manifest.Id);
        Assert.Contains("[game.neon]", pack.Rules);
        Assert.Contains("[neon.kill.popup]", pack.Rules);
        Assert.DoesNotContain("[neon.edited]", pack.Rules);
        Assert.DoesNotContain("overwatch", pack.Rules);
        Assert.Contains("sound = neon_zap.wav", pack.Rules);  // the missing one left out, with a warning
        Assert.Single(pack.Warnings);
        Assert.Equal(["neon_zap.wav"], pack.Files.Keys);
        Assert.Equal(2, pack.Examples.Count);
        Assert.Equal(2, pack.RuleCount);
    }

    [Fact]
    public void BuildsFromBuiltInRulesWhenTheUserHasNone()
    {
        var pack = DetectionPacks.Build("valorant", "Valorant", ["VALORANT-Win64-Shipping.exe"], DetectionIni.Parse(User),
                                        DetectionIni.Parse(Shipped), null, Find);
        Assert.Contains("[valorant.kill.banner]", pack.Rules);
        Assert.Contains("[valorant.scoreboard]", pack.Rules);
        Assert.Contains("image = valorant_kill_banner.png", pack.Rules);
        Assert.True(pack.Files.ContainsKey("valorant_kill_banner.png"));
        Assert.Equal(2, pack.RuleCount);
    }

    [Fact]
    public void RoundTripsAndInstalls()
    {
        var pack = DetectionPacks.Build("neon", "Neon Arena", ["NeonArena.exe"], DetectionIni.Parse(User),
                                        DetectionIni.Parse(Shipped), Path.Combine(_dir, "learned.txt"), Find);
        var file = Path.Combine(_dir, "neon.cepack");
        DetectionPacks.Write(pack, file);
        var read = DetectionPacks.Read(file);
        Assert.Equal(("neon", "Neon Arena"), (read.Manifest.Id, read.Manifest.Name));
        Assert.Equal(pack.Files["neon_zap.wav"], read.Files["neon_zap.wav"]);
        Assert.Equal(2, read.Examples.Count);
        Assert.Empty(read.Warnings);

        var home = Path.Combine(_dir, "home");
        var user = DetectionIni.Parse("# Your changes to Clip Engine's detection rules.\n\n[overwatch.edited]\n");
        Assert.False(DetectionPacks.Install(read, user, home));
        var installed = File.ReadAllText(Path.Combine(home, "detection.ini"));
        Assert.Contains("[neon.edited]", installed);
        Assert.Contains("[overwatch.edited]", installed);  // other games untouched
        Assert.Contains("sound = packs/neon/neon_zap.wav", installed);
        Assert.True(File.Exists(Path.Combine(home, "packs", "neon", "neon_zap.wav")));
        Assert.Equal(["neon"], DetectionIni.Parse(installed).Games.Select(g => g.Id));
        var learned = File.ReadAllLines(Path.Combine(home, "hud_learned.txt"));
        Assert.Contains("neon\tkill\tcenter\tVAPORIZED {P}", learned);

        // Installing again replaces the game's rules rather than adding more, and keeps a backup.
        Assert.True(DetectionPacks.Install(read, DetectionIni.Parse(installed), home));
        var again = File.ReadAllText(Path.Combine(home, "detection.ini"));
        Assert.Single(DetectionIni.Parse(again).RulesFor("neon").Where(r => r.Suffix == "popup"));
        Assert.True(File.Exists(Path.Combine(home, "detection.before-pack.ini")));
        Assert.Equal(learned.Length, File.ReadAllLines(Path.Combine(home, "hud_learned.txt")).Length);
    }

    private static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var entry = zip.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void AHostilePackCanOnlyTouchItsOwnGame()
    {
        using var stream = Zip(
            ("pack.json", """{"format":1,"id":"neon","name":"Neon"}"""),
            ("detection.ini", """
                [detection]
                sample_ms = 100
                [valorant.kill]
                region = 0, 0, 1, 1
                words = A
                [neon.kill]
                region = 0.4, 0.4, 0.2, 0.2
                image = ..\..\Windows\system32\secret.png
                [neon.death]
                region = 0.4, 0.4, 0.2, 0.2
                image = C:\Users\x\icon.png | icon.png
                words = DEAD
                """),
            ("hud_learned.txt", "valorant\tkill\tcenter\tBOOM\nbogus line\n"),
            ("files/icon.png", "png"),
            ("files/../../evil.png", "x"),
            ("files/run.exe", "MZ"),
            ("other.txt", "x"));
        var pack = DetectionPacks.Read(stream);
        Assert.DoesNotContain("valorant", pack.Rules);
        Assert.DoesNotContain("sample_ms", pack.Rules);
        Assert.DoesNotContain("secret", pack.Rules);
        Assert.DoesNotContain(@"C:\", pack.Rules);
        Assert.Contains("image = icon.png | icon.png", pack.Rules);
        Assert.Equal(["icon.png"], pack.Files.Keys);
        Assert.Equal(["neon\tkill\tcenter\tBOOM"], pack.Examples);  // the line is for the pack's game
        Assert.Contains(pack.Warnings, w => w.Contains("[valorant.kill]"));
        Assert.Contains(pack.Warnings, w => w.Contains("run.exe"));
    }

    [Theory]
    [InlineData("""{"format":1,"id":"../x","name":"X"}""", "id")]
    [InlineData("""{"format":99,"id":"x","name":"X"}""", "newer")]
    [InlineData("not json", "pack.json")]
    public void RejectsBadManifests(string manifest, string reason)
    {
        using var stream = Zip(("pack.json", manifest), ("detection.ini", "[x.kill]\nregion = 0,0,1,1\nwords = A\n"));
        var e = Assert.Throws<PackException>(() => DetectionPacks.Read(stream));
        Assert.Contains(reason, e.Message);
    }

    [Fact]
    public void RejectsAPackWithoutRules()
    {
        using var stream = Zip(("pack.json", """{"id":"neon"}"""), ("detection.ini", "[other.kill]\nwords = A\n"));
        Assert.Throws<PackException>(() => DetectionPacks.Read(stream));
        Assert.Throws<PackException>(() => DetectionPacks.Read(Zip(("detection.ini", "[neon.kill]\nwords = A\n"))));
    }

    [Fact]
    public void LearnedLines()
    {
        Assert.Equal(new DetectionPacks.Example("*", "death", "banner", "GAME OVER"),
                     DetectionPacks.ParseExample("*\tdeath\tbanner\tGAME OVER\r"));
        Assert.Null(DetectionPacks.ParseExample("# comment"));
        Assert.Null(DetectionPacks.ParseExample("neon\tround\tcenter\tX"));
        var file = Path.Combine(_dir, "learned2.txt");
        Assert.Equal(2, DetectionPacks.AppendExamples(file, ["neon\tkill\tcenter\tA", "x\tkill\tcenter\tB"]));
        Assert.Equal(0, DetectionPacks.AppendExamples(file, ["neon\tkill\tcenter\tA"]));
        Assert.Equal(1, DetectionPacks.ForgetExamples(file, "neon"));
        Assert.DoesNotContain(File.ReadAllLines(file), l => l.StartsWith("neon"));
    }

    [Fact]
    public void SafeFileNames()
    {
        Assert.True(DetectionPacks.IsSafeFileName("kill_icon.png"));
        Assert.True(DetectionPacks.IsSafeFileName("chime.WAV"));
        Assert.False(DetectionPacks.IsSafeFileName("../x.png"));
        Assert.False(DetectionPacks.IsSafeFileName("..png"));
        Assert.False(DetectionPacks.IsSafeFileName("x.exe"));
        Assert.False(DetectionPacks.IsSafeFileName(@"C:\x.png"));
        Assert.False(DetectionPacks.IsSafeFileName(".hidden.png"));
    }

    [Fact]
    public void TeachingHelpers()
    {
        Assert.Equal("kill@12.3;death@40.15",
                     StudioTools.MarksArg([new ClipEvent { Type = "death", Time = 40.15 }, new ClipEvent { Type = "kill", Time = 12.3 }]));
        Assert.StartsWith("Nothing new", AccuracyViewModel.TeachSummary([], "Neon"));
        var summary = AccuracyViewModel.TeachSummary(
            [new LearnExample("kill", "center", "A"), new LearnExample("kill", "center", "B"), new LearnExample("none", "top", "C")], "Neon");
        Assert.StartsWith("Learned 2 lines that show a moment and 1 line that means nothing for Neon.", summary);
    }
}
