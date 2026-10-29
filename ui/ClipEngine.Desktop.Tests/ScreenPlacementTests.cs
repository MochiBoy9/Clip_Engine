using ClipEngine.Desktop.Services;
using Xunit;
using Box = ClipEngine.Desktop.Services.ScreenPlacement.Box;

namespace ClipEngine.Desktop.Tests;

// The same cases as TestPlaceRegion in tests/DetectTests.cpp, so Studio draws boxes where the engine looks.
public class ScreenPlacementTests : IDisposable
{
    private static readonly Box Feed = new(0.65, 0.125, 0.33, 0.045);
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"clip_engine_{Guid.NewGuid():N}.ini");

    public void Dispose() => File.Delete(_file);

    private static void Near(double expected, double actual) => Assert.Equal(expected, actual, 6);

    [Fact]
    public void StaysPutOnTheShapeItWasMadeFor()
    {
        var placed = ScreenPlacement.Place(Feed, null, null, 2560, 1440, 1);
        Near(0.65, placed.Left);
        Near(0.33, placed.Width);
        Near(0.125, placed.Top);
    }

    [Fact]
    public void KeepsToItsEdgeOnAnUltrawide()
    {
        var wide = ScreenPlacement.Place(Feed, null, null, 3440, 1440, 1);
        Near(0.02 * 2560, (1 - wide.Left - wide.Width) * 3440);
        Near(0.33 * 2560, wide.Width * 3440);
        Near(0.125, wide.Top);

        var banner = ScreenPlacement.Place(new(0.42, 0.72, 0.16, 0.16), null, null, 3440, 1440, 1);
        Near(0.5, banner.Left + banner.Width / 2);
    }

    [Fact]
    public void KeepsToTheTopOnA16By10Screen()
    {
        var tall = ScreenPlacement.Place(Feed, null, null, 1920, 1200, 1);
        Near(0.125 * 1080, tall.Top * 1200);
        Near(0.65, tall.Left);
    }

    [Fact]
    public void ShrinksTowardItsCornerWithASmallerHud()
    {
        var small = ScreenPlacement.Place(Feed, null, null, 1920, 1080, 0.8);
        Near(1 - 0.02 * 0.8, small.Left + small.Width);
        Near(0.33 * 0.8, small.Width);
        Near(0.125 * 0.8, small.Top);
    }

    [Fact]
    public void AGivenAnchorWins()
    {
        var centered = ScreenPlacement.Place(new(0.1, 0.4, 0.2, 0.2), "center", null, 3440, 1440, 1);
        Near(0.5 + (0.2 - 0.5) * 2560 / 3440, centered.Left + centered.Width / 2);
        Assert.Equal((ScreenPlacement.Anchor.End, ScreenPlacement.Anchor.Start), ScreenPlacement.ParseAnchor("top-right"));
        Assert.Equal((ScreenPlacement.Anchor.Center, ScreenPlacement.Anchor.End), ScreenPlacement.ParseAnchor("bottom center"));
        Assert.Equal((ScreenPlacement.Anchor.Start, ScreenPlacement.Anchor.Auto), ScreenPlacement.ParseAnchor("left"));
        Assert.Equal((ScreenPlacement.Anchor.Auto, ScreenPlacement.Anchor.Auto), ScreenPlacement.ParseAnchor("sideways"));
        Near(21.0 / 9, ScreenPlacement.ParseAspect("21:9")!.Value);
        Assert.Null(ScreenPlacement.ParseAspect("wide"));
    }

    [Fact]
    public void ABoxDrawnOnAFrameComesBackWhereItWasDrawn()
    {
        // On an ultrawide frame with a 90% HUD: saved for that shape at 100%, and placed again the same.
        var drawn = new Box(0.75, 0.1, 0.22, 0.05);
        var (box, aspect, anchor) = ScreenPlacement.Unplace(drawn, null, 3440, 1440, 0.9);
        Assert.Equal("3440:1440", aspect);
        var again = ScreenPlacement.Place(box, anchor, aspect, 3440, 1440, 0.9);
        Near(drawn.Left, again.Left);
        Near(drawn.Width, again.Width);
        Near(drawn.Top, again.Top);

        // On a 16:9 frame at 100% it's saved just as drawn.
        var (plain, noAspect, noAnchor) = ScreenPlacement.Unplace(drawn, null, 1920, 1080, 1);
        Assert.Equal(drawn, plain);
        Assert.Null(noAspect);
        Assert.Null(noAnchor);
    }

    [Fact]
    public void NamesTheAnchorWhenAutoWouldPickAnother()
    {
        // At 50% a box drawn in the middle third is saved in the last third; it must still keep to the middle.
        var drawn = new Box(0.62, 0.45, 0.04, 0.1);
        var (box, _, anchor) = ScreenPlacement.Unplace(drawn, null, 1920, 1080, 0.5);
        Assert.Equal("center", anchor);
        var again = ScreenPlacement.Place(box, anchor, null, 1920, 1080, 0.5);
        Near(drawn.Left, again.Left);
    }

    [Fact]
    public void HudSizesChangeOnlyTheirOwnLine()
    {
        File.WriteAllText(_file, "codec = auto\nhud_scale =   # sizes\nplayer_names = Mochi\n");
        Assert.Equal(100, HudScales.For(_file, "overwatch"));
        HudScales.Save(_file, "overwatch", 90);
        HudScales.Save(_file, "fortnite", 120);
        Assert.Equal(["codec = auto", "hud_scale = fortnite 120, overwatch 90", "player_names = Mochi"], File.ReadAllLines(_file));
        Assert.Equal(90, HudScales.For(_file, "overwatch"));
        HudScales.Save(_file, "overwatch", 100);
        Assert.Equal(100, HudScales.For(_file, "overwatch"));
        Assert.Equal(120, HudScales.For(_file, "fortnite"));
        Assert.Equal(new Dictionary<string, int> { ["apex_legends"] = 85 }, HudScales.Parse("apex_legends 85%, bad"));
    }
}
