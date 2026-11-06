using Avalonia;
using Avalonia.Controls;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.Views;

// Behind the pages with an adaptive theme on: a still wash of the accent and its partner colour over the dotted grid.
// It never moves, so it costs nothing once drawn. The theme's motion lives in the controls (Theme/Adaptive.axaml).
internal sealed class AdaptiveBackdrop : Panel
{
    private ThemeLook _look = new(AccentThemes.Default.Accent);

    public AdaptiveBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        IsVisible = false;
        var wash = new Border();
        wash[!Border.BackgroundProperty] = wash.GetResourceObservable("AdaptiveWashBrush").ToBinding();
        var partner = new Border();
        partner[!Border.BackgroundProperty] = partner.GetResourceObservable("AdaptiveWashPartnerBrush").ToBinding();
        Children.Add(wash);
        Children.Add(partner);
    }

    // Windows' "Animation effects" setting, read each time the look changes. Off, the controls hold still whatever
    // the app's own switch says.
    internal static Func<bool> SystemAnimates { get; set; } = () => !OperatingSystem.IsWindows() || Win32.ClientAreaAnimation();

    private bool _systemAnimates = true;

    // Whether the adaptive theme's motion is on at all, by the app's switch and Windows' setting.
    internal bool MotionOn => _look.Adaptive && _look.Motion && _systemAnimates;

    public void Show(ThemeLook look)
    {
        _look = look;
        _systemAnimates = SystemAnimates();
        IsVisible = look.Adaptive;
    }
}
