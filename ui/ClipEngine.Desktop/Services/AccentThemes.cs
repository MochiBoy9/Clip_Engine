using Avalonia;
using Avalonia.Media;

namespace ClipEngine.Desktop.Services;

// Accent colours. Volt is everyone's; the rest are a thank-you for supporters.
internal sealed record AccentTheme(string Id, string Name, Color Accent)
{
    public bool IsDefault => Id == AccentThemes.Default.Id;
}

internal static class AccentThemes
{
    public static readonly AccentTheme Default = new("volt", "Volt", Color.Parse("#C5F135"));

    public static IReadOnlyList<AccentTheme> All { get; } =
    [
        Default,
        new("aurora", "Aurora", Color.Parse("#8F7CFF")),
        new("ember", "Ember", Color.Parse("#FF7A45")),
        new("mint", "Mint", Color.Parse("#3DDCB0")),
        new("sakura", "Sakura", Color.Parse("#FF7EB6")),
        new("ocean", "Ocean", Color.Parse("#3FB4FF")),
        new("gold", "Gold", Color.Parse("#F5B83D")),
    ];

    public static AccentTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;

    public static IBrush Preview(AccentTheme theme) => new SolidColorBrush(theme.Accent);

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // The accent brushes are looked up as dynamic resources, so replacing them at the application level recolours every
    // button, chip, toggle and slider at once.
    public static void ApplyAccent(AccentTheme theme)
    {
        if (Application.Current is not { } app) return;
        var accent = theme.Accent;
        var foreground = Foreground(accent);
        app.Resources["AccentColor"] = accent;
        app.Resources["AccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AccentHoverBrush"] = new SolidColorBrush(Mix(accent, Colors.White, 0.18));
        app.Resources["AccentPressedBrush"] = new SolidColorBrush(Mix(accent, Colors.Black, 0.12));
        app.Resources["AccentSoftBrush"] = new SolidColorBrush(WithAlpha(accent, 0x1F));
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

    // Text on the accent: near-black on light accents like Volt, white on darker ones, whichever reads better.
    internal static Color Foreground(Color accent)
    {
        var dark = Color.Parse("#0C0D10");
        var l = Luminance(accent);
        var onDark = (l + 0.05) / (Luminance(dark) + 0.05);
        var onWhite = 1.05 / (l + 0.05);
        return onDark >= onWhite ? dark : Colors.White;
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
}
