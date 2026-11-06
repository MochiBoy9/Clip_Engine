using System.Text.Json.Nodes;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Cloud;
using ClipEngine.Desktop.Services.Squad;
using Xunit;

namespace ClipEngine.Desktop.Tests;

// The desktop side against a real copy of the online service. Runs only when CLIPENGINE_TEST_SERVER
// names one, like `npx wrangler dev` in server/ (http://localhost:8787); otherwise it passes at once.
public class OnlineServiceTests
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("CLIPENGINE_TEST_SERVER");

    private sealed class Player : IDisposable
    {
        private readonly object _gate = new();
        public List<string> Commands { get; } = [];
        public SquadSettings Settings { get; } = new();
        public SquadSync? Sync { get; private set; }
        public Dictionary<string, LocalClip> Clips { get; } = [];
        public int LibraryChanges;

        public void Connect(string token, SquadInfo squad)
        {
            Settings.SquadId = squad.Id;
            Settings.MemberId = squad.You;
            Settings.Token = token;
            Sync = new SquadSync(Settings, new SquadConnection(Server!, squad.Id, token), new SquadViewIndex(null),
                                 c => { lock (_gate) Commands.Add(c); return true; },
                                 p => Clips.GetValueOrDefault(p), post: a => { lock (_gate) a(); });
            Sync.LibraryChanged += () => Interlocked.Increment(ref LibraryChanges);
            Sync.Start();
        }

        public void Do(Action<SquadSync> a)
        {
            lock (_gate) a(Sync!);
        }

        public T Get<T>(Func<SquadSync, T> f)
        {
            lock (_gate) return f(Sync!);
        }

        public void Dispose() => Sync?.Dispose();
    }

    private static async Task Until(Func<bool> done, string what)
    {
        for (var i = 0; i < 100; i++)
        {
            if (done()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timed out waiting for {what}");
    }

    [Fact]
    public async Task TwoPlayersShareAKillAndItsViews()
    {
        if (string.IsNullOrEmpty(Server)) return;
        var api = new SquadApi(Server);
        var (tokenA, squadA) = await api.CreateAsync("Enterro", "device-aaaaaaaa", "Night shift");
        var (tokenB, squadB) = await api.JoinAsync(squadA.Invite, "Kai", "device-bbbbbbbb");
        Assert.Equal(squadA.Id, squadB.Id);

        using var a = new Player();
        using var b = new Player();
        a.Connect(tokenA, squadA);
        b.Connect(tokenB, squadB);
        await Until(() => a.Get(s => s.Connected && s.Clock.Synced) && b.Get(s => s.Connected && s.Clock.Synced), "both to connect");

        a.Do(s => s.OnRecordingStarted("Valorant"));
        b.Do(s => s.OnRecordingStarted("Valorant"));
        await Until(() => a.Get(s => s.PlayingWithMe().Count == 1) && b.Get(s => s.PlayingWithMe().Count == 1), "the two to be linked");

        // Enterro's kill: Kai's app asks its engine for Kai's view of it.
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        a.Do(s =>
        {
            s.OnMomentDetected("kill", "Valorant", "ENTERRO > X");
            s.OnMomentTime("kill", now);
        });
        await Until(() => b.Get(_ => b.Commands.Any(c => c.StartsWith("squad-save "))), "Kai's view to be asked for");
        var save = b.Get(_ => b.Commands.First(c => c.StartsWith("squad-save "))).Split(' ');
        var moment = save[1];

        // Kai's engine saved it.
        var file = Path.Combine(Path.GetTempPath(), $"squad_e2e_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(file, new byte[64 * 1024]);
        try
        {
            b.Clips[file] = new LocalClip(file, long.Parse(save[2]), 15, "squad", false);
            b.Do(s => s.OnSquadViewSaved(moment, file));
            await Until(() => Volatile.Read(ref a.LibraryChanges) > 0, "Enterro to hear of Kai's view");

            await api.UploadAsync(squadB.Id, tokenB, moment, "video", file, "video/mp4");
            var (moments, _) = await api.LibraryAsync(squadA.Id, tokenA);
            var shared = Assert.Single(moments);
            Assert.Equal(moment, shared.Id);
            Assert.Equal("Enterro", shared.ByName);
            var kai = Assert.Single(shared.Views, v => v.Name == "Kai");
            Assert.True(kai.Uploaded);

            var copy = file + ".copy.mp4";
            Assert.True(await api.DownloadAsync(squadA.Id, tokenA, moment, kai.Member, "video", copy));
            Assert.Equal(64 * 1024, new FileInfo(copy).Length);
            File.Delete(copy);
        }
        finally
        {
            File.Delete(file);
        }

        await api.LeaveAsync(squadB.Id, tokenB);
        await api.LeaveAsync(squadA.Id, tokenA);
    }

    [Fact]
    public async Task AClipGetsAShortLinkAndAProfile()
    {
        if (string.IsNullOrEmpty(Server)) return;
        var api = new CloudApi(Server);
        var email = $"e2e-{Guid.NewGuid():N}@example.com";
        var (token, account) = await api.CreateAsync("Enterro", email, "correct horse battery", "device-cccccccc");
        Assert.Equal(email, account.Email);
        var (_, again2) = await api.SignInWithPasswordAsync(email.ToUpperInvariant(), "correct horse battery", "device-eeeeeeee");
        Assert.Equal(account.Id, again2.Id);
        var clip = await api.AddClipAsync(account.Id, token, new JsonObject
        {
            ["localId"] = "Valorant_2026-11-03.mp4|65536", ["title"] = "3 kills", ["game"] = "Valorant", ["seconds"] = 30,
            ["visibility"] = "public", ["markers"] = new JsonArray(new JsonObject { ["type"] = "kill", ["time"] = 12.5, ["text"] = "ENTERRO > X" }),
        });
        Assert.False(clip.Uploaded);
        var file = Path.Combine(Path.GetTempPath(), $"cloud_e2e_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(file, new byte[65536]);
        try
        {
            await api.UploadAsync(account.Id, token, clip.Id, "video", file, "video/mp4");
        }
        finally
        {
            File.Delete(file);
        }
        // Adding the same clip again finds it.
        var again = await api.AddClipAsync(account.Id, token, new JsonObject { ["localId"] = "Valorant_2026-11-03.mp4|65536", ["seconds"] = 30 });
        Assert.Equal(clip.Code, again.Code);
        Assert.True(again.Uploaded);

        using var http = new HttpClient();
        var page = await http.GetStringAsync(api.ShareLink(clip.Code));
        Assert.Contains("3 kills", page);

        var handle = $"e2e{Guid.NewGuid():N}"[..20];
        var updated = await api.UpdateAccountAsync(account.Id, token, null, handle);
        Assert.Equal(handle, updated.Handle);
        var profile = await http.GetStringAsync(api.ProfileLink(handle));
        Assert.Contains("3 kills", profile);

        var code = await api.LinkCodeAsync(account.Id, token);
        var (_, same) = await api.SignInAsync(code, "device-dddddddd");
        Assert.Equal(account.Id, same.Id);
    }
}
