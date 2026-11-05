using Avalonia;
using Avalonia.Media;

namespace ClipEngine.Desktop.Services;

// How the app looks right now: the accent, and for Ultra whether adaptive themes are on and whether the background
// may move. Adaptive themes build everything from the accent and one partner colour next to it on the colour wheel,
// so whatever the accent, the gradients stay two neighbouring hues and never a rainbow.
internal sealed record ThemeLook(Color Accent, bool Adaptive = false, bool Motion = false)
{
    public static ThemeLook Plain(AccentTheme theme) => new(theme.Accent);
}

internal static class AdaptiveLook
{
    // How far round the wheel the partner colour sits: amber leans to orange, cyan to mint, violet to blue.
    public const double PartnerShift = -32;

    // The second colour of an adaptive theme: the accent's neighbour on the wheel, lifted to read on the panels. A grey
    // accent like Chrome has no hue to turn, so it takes a cold steel tint instead.
    public static Color Partner(Color accent)
    {
        var (h, s, v) = ToHsv(accent);
        if (s < 0.12) return AccentThemes.Legible(AccentThemes.Mix(accent, Color.Parse("#7FA8D8"), 0.45));
        return AccentThemes.Legible(FromHsv(h + PartnerShift, Math.Min(1, s * 1.05), v));
    }

    // The brushes adaptive styles (Theme/Adaptive.axaml) and the backdrop (Views/AdaptiveBackdrop.cs) use. They're set
    // for every look so the styles always resolve, but only an adaptive window shows them. All are still pictures: a
    // gradient costs the same to draw as a flat fill.
    public static void Apply(ThemeLook look)
    {
        AccentThemes.ApplyAccent(look.Accent);
        if (Application.Current is not { } app) return;
        var accent = look.Accent;
        var partner = Partner(accent);
        app.Resources["AccentPartnerColor"] = partner;
        app.Resources["AccentPartnerBrush"] = new SolidColorBrush(partner);

        // Buttons, the selected page and badges: the accent held for a third of the way, then turning to its partner,
        // like ink that ran on a printed panel.
        app.Resources["AccentGradientBrush"] = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative),
            (accent, 0), (accent, 0.35), (partner, 1));
        app.Resources["AccentGradientHoverBrush"] = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative),
            (AccentThemes.Mix(accent, Colors.White, 0.16), 0), (AccentThemes.Mix(accent, Colors.White, 0.16), 0.25),
            (AccentThemes.Mix(partner, Colors.White, 0.1), 1));

        // Switches that are on: the gradient while adaptive, otherwise Fluent's own fill from the accent.
        foreach (var key in new[] { "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver" })
        {
            if (look.Adaptive) app.Resources[key] = app.Resources["AccentGradientBrush"];
            else app.Resources.Remove(key);
        }

        // Panel edges and brackets: the accent fading out along the edge.
        app.Resources["AccentEdgeBrush"] = Linear(new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative),
            (accent, 0), (WithAlpha(partner, 0x66), 0.5), (WithAlpha(partner, 0x00), 1));

        // The wash behind the pages: the accent glowing in from the top right, the partner from the bottom left.
        app.Resources["AdaptiveWashBrush"] = Linear(new RelativePoint(1, 0, RelativeUnit.Relative), new RelativePoint(0.35, 0.75, RelativeUnit.Relative),
            (WithAlpha(accent, 0x1C), 0), (WithAlpha(accent, 0x08), 0.45), (WithAlpha(accent, 0x00), 1));
        app.Resources["AdaptiveWashPartnerBrush"] = Linear(new RelativePoint(0, 1, RelativeUnit.Relative), new RelativePoint(0.5, 0.35, RelativeUnit.Relative),
            (WithAlpha(partner, 0x14), 0), (WithAlpha(partner, 0x00), 1));

        // The light under the pointer: the accent at its heart, fading through its partner to nothing.
        var light = new RadialGradientBrush();
        light.GradientStops.Add(new GradientStop(WithAlpha(accent, 0x38), 0));
        light.GradientStops.Add(new GradientStop(WithAlpha(partner, 0x16), 0.45));
        light.GradientStops.Add(new GradientStop(WithAlpha(partner, 0x00), 1));
        app.Resources["AdaptiveLightBrush"] = light.ToImmutable();
    }

    private static IBrush Linear(RelativePoint start, RelativePoint end, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (color, offset) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
        return brush.ToImmutable();
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // Hue in degrees, saturation and value from 0 to 1.
    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte B(double t) => (byte)Math.Round(Math.Clamp(t, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }
}
