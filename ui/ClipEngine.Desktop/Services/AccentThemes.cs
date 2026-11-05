using Avalonia;
using Avalonia.Media;

namespace ClipEngine.Desktop.Services;

// Accent colours. Hazard amber is everyone's; Pro unlocks the rest of the first thirteen, and Ultra three more, a colour
// wheel for any colour that reads on the panels, and adaptive themes (Services/AdaptiveLook.cs).
internal sealed record AccentTheme(string Id, string Name, Color Accent, Plan Needs = Plan.Pro)
{
    public bool IsDefault => Id == AccentThemes.Default.Id;
    public bool IsUnlocked(Plan plan) => IsDefault || plan >= Needs;
}

internal static class AccentThemes
{
    public static readonly AccentTheme Default = new("hazard", "Hazard", Color.Parse("#FFC21A"), Plan.Free);

    public static IReadOnlyList<AccentTheme> All { get; } =
    [
        Default,
        new("aurora", "Aurora", Color.Parse("#9B87FF")),
        new("ember", "Ember", Color.Parse("#FF6A3D")),
        new("mint", "Mint", Color.Parse("#2FD6A8")),
        new("sakura", "Sakura", Color.Parse("#FF6FAE")),
        new("ocean", "Ocean", Color.Parse("#3AA8FF")),
        new("volt", "Volt", Color.Parse("#C5F135")),
        new("cobalt", "Cobalt", Color.Parse("#5B74FF")),
        new("toxin", "Toxin", Color.Parse("#5CFF8A")),
        new("orchid", "Orchid", Color.Parse("#D06BFF")),
        new("flare", "Flare", Color.Parse("#FF9A1F")),
        new("glacier", "Glacier", Color.Parse("#A6E6FF")),
        new("bone", "Bone", Color.Parse("#E4DAC2")),
        new("chrome", "Chrome", Color.Parse("#D3DAE3"), Plan.Ultra),
        new("arc", "Arc", Color.Parse("#22E1F0"), Plan.Ultra),
        new("redline", "Redline", Color.Parse("#FF3D5A"), Plan.Ultra),
    ];

    // Ultra's extras, as a special key's list of themes names them (server/src/plans.js, THEME_IDS): the colour wheel,
    // whose pick is kept as DesktopSettings.CustomAccent, and adaptive themes.
    public const string WheelId = "wheel";
    public const string AdaptiveId = "adaptive";

    public static AccentTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;

    // A colour from the wheel, as a theme.
    public static AccentTheme Custom(Color accent) => new(WheelId, "Your colour", Legible(accent), Plan.Ultra);

    // A swatch: the colour, or for the colour wheel the ring of hues it offers.
    public static IBrush Preview(AccentTheme theme)
    {
        if (theme.Id != WheelId) return new SolidColorBrush(theme.Accent);
        var ring = new ConicGradientBrush { Angle = 0 };
        for (var h = 0; h <= 360; h += 30) ring.GradientStops.Add(new GradientStop(Legible(AdaptiveLook.FromHsv(h, 0.9, 1)), h / 360.0));
        return ring;
    }

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static void ApplyAccent(AccentTheme theme) => ApplyAccent(theme.Accent);

    // The accent brushes are looked up as dynamic resources, so replacing them at the application level recolours every
    // button, chip, toggle and slider at once.
    public static void ApplyAccent(Color accent)
    {
        if (Application.Current is not { } app) return;
        var foreground = Foreground(accent);
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(Mix(accent, Colors.White, 0.18));
        app.Resources["AccentPressedBrush"] = new SolidColorBrush(Mix(accent, Colors.Black, 0.12));
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(WithAlpha(accent, 0x1F));
        app.Resources["AccentStripeBrush"] = new SolidColorBrush(WithAlpha(accent, 0x2E));
        app.Resources["AccentForegroundBrush"] = new SolidColorBrush(foreground);
        app.Resources["ToggleSwitchKnobFillOn"] = new SolidColorBrush(foreground);
        app.Resources["ToggleSwitchKnobFillOnPointerOver"] = new SolidColorBrush(foreground);

        // Fluent's own accent, used by focus rings, toggles, sliders and text selection.
        app.Resources["SystemAccentColor"] = accent;
        for (var i = 1; i <= 3; i++)
        {
            app.Resources[$"SystemAccentColorLight{i}"] = Mix(accent, Colors.White, 0.15 * i);
            app.Resources[$"SystemAccentColorDark{i}"] = Mix(accent, Colors.Black, 0.15 * i);
        }
    }

    // How far the accent must stand out from the window behind it, as a contrast ratio. Text in the accent (section
    // numbers, the title bar) needs about this much to read.
    internal const double MinContrast = 4.5;
    internal static readonly Color Window = Color.Parse("#0B0C0E");

    // Only colours that read on the dark panels apply: one too dark is lifted toward white until it does, keeping its hue.
    internal static Color Legible(Color c)
    {
        c = Color.FromRgb(c.R, c.G, c.B);
        if (Contrast(c, Window) >= MinContrast) return c;
        double lo = 0, hi = 1;
        for (var i = 0; i < 14; i++)
        {
            var mid = (lo + hi) / 2;
            if (Contrast(Mix(c, Colors.White, mid), Window) >= MinContrast) hi = mid;
            else lo = mid;
        }
        return Mix(c, Colors.White, hi);
    }

    internal static bool IsLegible(Color c) => Contrast(c, Window) >= MinContrast - 0.01;

    internal static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    internal static Color? ParseHex(string? text)
    {
        var t = text?.Trim() ?? "";
        if (!t.StartsWith('#')) t = "#" + t;
        return t.Length == 7 && Color.TryParse(t, out var c) ? Color.FromRgb(c.R, c.G, c.B) : null;
    }

    // Text on the accent: near-black on light accents like Hazard, white on darker ones, whichever reads better.
    internal static Color Foreground(Color accent)
    {
        var dark = Color.Parse("#0B0C0E");
        var l = Luminance(accent);
        var onDark = (l + 0.05) / (Luminance(dark) + 0.05);
        var onWhite = 1.05 / (l + 0.05);
        return onDark >= onWhite ? dark : Colors.White;
    }

    // Each channel's share of the light, worked out once: the colour wheel asks for it for every pixel it draws.
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(v =>
    {
        var s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }).ToArray();

    internal static double Luminance(Color c) => 0.2126 * Linear[c.R] + 0.7152 * Linear[c.G] + 0.0722 * Linear[c.B];

    internal static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
