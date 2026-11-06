using Avalonia.Media;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class ThemeTests
{
    private static PlanViewModel Vm(Plan plan, IReadOnlyList<string>? keyThemes = null, bool special = false)
    {
        var vm = new PlanViewModel(null, null, () => "", []);
        if (plan != Plan.Free || special)
            vm.Accept("KEY", new LicenseCheck(LicenseOutcome.Valid, "", plan, "", null, special, null, keyThemes));
        return vm;
    }

    [Fact]
    public void ProGetsThirteenColoursAndUltraThreeMore()
    {
        Assert.Equal(16, AccentThemes.All.Count);
        Assert.Equal(AccentThemes.All.Count, AccentThemes.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(13, AccentThemes.All.Count(t => t.IsUnlocked(Plan.Pro)));
        Assert.Equal(1, AccentThemes.All.Count(t => t.IsUnlocked(Plan.Free)));
        Assert.Equal(["chrome", "arc", "redline"], AccentThemes.All.Where(t => t.Needs == Plan.Ultra).Select(t => t.Id));
        Assert.DoesNotContain(AccentThemes.All, t => t.Id is AccentThemes.WheelId or AccentThemes.AdaptiveId);
    }

    [Fact]
    public void EveryThemeReadsOnThePanels()
    {
        foreach (var theme in AccentThemes.All)
            Assert.True(AccentThemes.IsLegible(theme.Accent), $"{theme.Name} is too dark for the panels");
    }

    [Fact]
    public void OnlyColoursThatReadApply()
    {
        // A colour that already reads is kept as it is.
        Assert.Equal(Color.Parse("#FFC21A"), AccentThemes.Legible(Color.Parse("#FFC21A")));

        // Dark ones are lifted until they read, keeping their hue.
        foreach (var dark in new[] { "#000000", "#0000FF", "#3A0010", "#202020", "#0B0C0E" })
        {
            var raw = Color.Parse(dark);
            var lifted = AccentThemes.Legible(raw);
            Assert.True(AccentThemes.IsLegible(lifted), dark);
            Assert.True(AccentThemes.Contrast(lifted, AccentThemes.Window) < AccentThemes.MinContrast + 0.6, $"{dark} lifted too far");
            var (h, s, _) = AdaptiveLook.ToHsv(raw);
            var (lh, _, _) = AdaptiveLook.ToHsv(lifted);
            if (s > 0.5) Assert.InRange(Math.Abs(lh - h), 0, 6);
        }
    }

    [Fact]
    public void HsvRoundTrips()
    {
        foreach (var hex in new[] { "#FFC21A", "#3AA8FF", "#D06BFF", "#808080", "#FF0000" })
        {
            var c = Color.Parse(hex);
            var (h, s, v) = AdaptiveLook.ToHsv(c);
            Assert.Equal(c, AdaptiveLook.FromHsv(h, s, v));
        }
        Assert.Equal(Color.Parse("#FF0000"), AdaptiveLook.FromHsv(360, 1, 1));
    }

    [Fact]
    public void TheGradientsPartnerIsANeighbourThatReads()
    {
        foreach (var theme in AccentThemes.All)
        {
            var partner = AdaptiveLook.Partner(theme.Accent);
            Assert.True(AccentThemes.IsLegible(partner), theme.Name);
            Assert.NotEqual(theme.Accent, partner);
        }
        // Hazard amber leans to orange.
        var (h, _, _) = AdaptiveLook.ToHsv(AdaptiveLook.Partner(AccentThemes.Default.Accent));
        Assert.InRange(h, 5, 25);
    }

    [Fact]
    public void TheWheelAndAdaptiveThemesAreUltras()
    {
        var free = Vm(Plan.Free);
        Assert.False(free.CanUseWheel);
        Assert.False(free.CanUseAdaptive);
        Assert.False(free.AdaptiveOn);
        Assert.True(free.ShowAdaptiveUltraTag);

        var pro = Vm(Plan.Pro);
        Assert.True(pro.IsUnlocked(AccentThemes.Find("cobalt")), "Pro has the new colours");
        Assert.True(pro.IsUnlocked(AccentThemes.Find("bone")));
        Assert.False(pro.CanUseWheel);
        Assert.False(pro.CanUseAdaptive);
        pro.WheelHue = 200;
        Assert.Equal(AccentThemes.Default.Id, pro.Theme.Id);
        Assert.False(pro.Themes.Single(t => t.Name.StartsWith("Colour wheel")).IsAvailable);

        var ultra = Vm(Plan.Ultra);
        Assert.True(ultra.CanUseWheel);
        Assert.True(ultra.CanUseAdaptive);
        Assert.True(ultra.AdaptiveOn, "adaptive themes start on for Ultra");
        Assert.True(ultra.Look.Adaptive);
        Assert.True(ultra.Look.Motion);
        Assert.True(ultra.Themes.Single(t => t.Name.StartsWith("Colour wheel")).IsAvailable);
    }

    [Fact]
    public void ASpecialKeyCanGiveTheWheelOrAdaptiveThemesOnTheirOwn()
    {
        var wheel = Vm(Plan.Pro, ["hazard", AccentThemes.WheelId], special: true);
        Assert.True(wheel.CanUseWheel);
        Assert.False(wheel.CanUseAdaptive);

        var adaptive = Vm(Plan.Free, [AccentThemes.AdaptiveId], special: true);
        Assert.False(adaptive.CanUseWheel);
        Assert.True(adaptive.CanUseAdaptive);

        // An Ultra key with the plan's themes gets everything Ultra has, whatever its role.
        var creator = new PlanViewModel(null, null, () => "", []);
        creator.Accept("CEK", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Ultra, "", null, true, Role: "creator", RoleLabel: "Content Creator"));
        Assert.True(creator.CanUseWheel);
        Assert.True(creator.CanUseAdaptive);
    }

    [Fact]
    public void TurningTheWheelMakesItTheTheme()
    {
        var vm = Vm(Plan.Ultra);
        var looks = new List<ThemeLook>();
        vm.LookChanged += looks.Add;

        vm.WheelBrightness = 1;
        vm.WheelSaturation = 1;
        vm.WheelHue = 240;
        Assert.Equal(AccentThemes.WheelId, vm.Theme.Id);
        Assert.True(vm.ShowWheel);
        Assert.True(vm.Themes.Single(t => t.Name.StartsWith("Colour wheel")).IsSelected);
        Assert.Equal("#0000FF", vm.WheelHex);

        // Pure blue is too dark for the panels, so it applies lifted, and the card says so.
        Assert.True(vm.HasWheelNote);
        Assert.NotEqual(Color.Parse("#0000FF"), vm.Theme.Accent);
        Assert.True(AccentThemes.IsLegible(vm.Theme.Accent));
        Assert.Equal(vm.Theme.Accent, looks[^1].Accent);

        // Typing a colour moves the wheel to it.
        vm.WheelHex = "#2FD6A8";
        Assert.False(vm.HasWheelNote);
        Assert.Equal(Color.Parse("#2FD6A8"), vm.Theme.Accent);
        Assert.InRange(vm.WheelHue, 162, 166);
        vm.WheelHex = "not a colour";
        Assert.Equal(Color.Parse("#2FD6A8"), vm.Theme.Accent);

        // The brightness slider stops where every colour would lift to the same pale shade.
        vm.WheelBrightness = 0;
        Assert.Equal(PlanViewModel.MinBrightness, vm.WheelBrightness);

        // Picking a swatch leaves the wheel.
        vm.Themes.Single(t => t.Name == "Ocean").IsSelected = true;
        Assert.Equal("ocean", vm.Theme.Id);
        Assert.False(vm.ShowWheel);
    }

    [Fact]
    public void AdaptiveThemesAndTheirMotionCanBeTurnedOff()
    {
        var vm = Vm(Plan.Ultra);
        var looks = new List<ThemeLook>();
        vm.LookChanged += looks.Add;

        vm.BackgroundMotion = false;
        Assert.True(looks[^1].Adaptive);
        Assert.False(looks[^1].Motion);

        vm.AdaptiveOn = false;
        Assert.False(looks[^1].Adaptive);
        Assert.False(looks[^1].Motion);

        vm.BackgroundMotion = true;
        Assert.False(looks[^1].Motion, "no motion without adaptive themes");
    }

    [Fact]
    public void LeavingUltraGoesBackToThePlansLook()
    {
        var vm = Vm(Plan.Ultra);
        vm.WheelHex = "#FF6FAE";
        Assert.Equal(AccentThemes.WheelId, vm.Theme.Id);
        var looks = new List<ThemeLook>();
        vm.LookChanged += looks.Add;

        vm.Accept("POLAR", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Pro));
        Assert.Equal(AccentThemes.Default.Id, vm.Theme.Id);
        Assert.False(vm.AdaptiveOn);
        Assert.False(looks[^1].Adaptive);
        Assert.Equal(AccentThemes.Default.Accent, looks[^1].Accent);

        // Ultra again: the colour picked before comes back.
        vm.Accept("POLAR", new LicenseCheck(LicenseOutcome.Valid, "", Plan.Ultra));
        Assert.Equal(Color.Parse("#FF6FAE"), vm.Theme.Accent);
        Assert.True(looks[^1].Adaptive);
    }
}
