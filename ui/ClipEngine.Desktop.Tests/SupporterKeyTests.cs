using System.Security.Cryptography;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class SupporterKeyTests : IDisposable
{
    private static readonly DateOnly Issued = new(2026, 10, 28);

    private readonly ECDsa _signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"clip_engine_supporter_{Guid.NewGuid():N}.key");

    private string PublicKey => Convert.ToBase64String(_signing.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _signing.Dispose();
        File.Delete(_file);
    }

    [Fact]
    public void AGenuineKeyNamesItsSupporter()
    {
        var key = SupporterKey.Create(_signing, "Zoë the Clipper", Issued);

        Assert.StartsWith("CE1-", key);
        Assert.Equal(new Supporter("Zoë the Clipper", Issued), SupporterKey.Verify(key, PublicKey));
        // Pasted with stray spaces or line breaks from a message.
        Assert.NotNull(SupporterKey.Verify($"  {key[..20]}\n{key[20..]} ", PublicKey));
    }

    [Fact]
    public void ForgedOrDamagedKeysAreRefused()
    {
        var key = SupporterKey.Create(_signing, "Patron", Issued);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = SupporterKey.Create(other, "Patron", Issued);

        Assert.Null(SupporterKey.Verify(forged, PublicKey));
        Assert.Null(SupporterKey.Verify(key[..^4] + (key[^4] == 'A' ? 'B' : 'A') + key[^3..], PublicKey));
        Assert.Null(SupporterKey.Verify(key[..30], PublicKey));
        Assert.Null(SupporterKey.Verify("CE1-not base64!", PublicKey));
        Assert.Null(SupporterKey.Verify("hello", PublicKey));
        Assert.Null(SupporterKey.Verify(key, ""));
    }

    [Fact]
    public void NamesMustFit()
    {
        Assert.Throws<ArgumentException>(() => SupporterKey.Create(_signing, "  ", Issued));
        Assert.Throws<ArgumentException>(() => SupporterKey.Create(_signing, new string('x', 65), Issued));
    }

    [Fact]
    public void ActivatingUnlocksThemesAndSurvivesARestart()
    {
        var desktop = new DesktopSettings();
        var vm = new SupporterViewModel(PublicKey, _file, desktop, credits: []);
        Assert.False(vm.IsSupporter);
        Assert.All(vm.Themes.Skip(1), t => Assert.False(t.IsAvailable));
        Assert.True(vm.Themes[0].IsSelected);

        vm.KeyInput = "CE1-wrong";
        vm.Activate();
        Assert.True(vm.MessageIsError);
        Assert.False(vm.IsSupporter);

        vm.KeyInput = SupporterKey.Create(_signing, "Patron", Issued);
        vm.Activate();
        Assert.True(vm.IsSupporter);
        Assert.Equal("Thank you, Patron", vm.Title);
        Assert.All(vm.Themes, t => Assert.True(t.IsAvailable));

        var restarted = new SupporterViewModel(PublicKey, _file, desktop, credits: []);
        Assert.True(restarted.IsSupporter);
        Assert.Equal("Patron", restarted.SupporterName);
    }

    [Fact]
    public void ThemesFollowTheKey()
    {
        var desktop = new DesktopSettings { AccentTheme = "ember" };
        var vm = new SupporterViewModel(PublicKey, null, desktop, credits: []);
        // A saved theme only applies while a key is active.
        Assert.Equal("aurora", vm.Theme.Id);

        vm.Themes[2].IsSelected = true;
        Assert.Equal("ember", desktop.AccentTheme);

        vm.KeyInput = SupporterKey.Create(_signing, "Patron", Issued);
        vm.Activate();
        Assert.Equal("ember", vm.Theme.Id);

        AccentTheme? applied = null;
        vm.ThemeChanged += t => applied = t;
        vm.Themes.Single(t => t.Name == "Mint").IsSelected = true;
        Assert.Equal("mint", applied?.Id);
        Assert.True(vm.Themes.Single(t => t.Name == "Mint").IsSelected);
        Assert.False(vm.Themes.Single(t => t.Name == "Ember").IsSelected);

        vm.Remove();
        Assert.Equal("aurora", applied?.Id);
    }
}
