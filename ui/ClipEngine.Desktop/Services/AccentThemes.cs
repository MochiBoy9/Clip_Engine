using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace ClipEngine.Desktop.Services;

// Colour themes for the accent gradient and the background glow. Aurora is everyone's; the rest are a thank-you for
// supporters.
internal sealed record AccentTheme(string Id, string Name, Color Start, Color Middle, Color End, Color Glow1, Color Glow2, Color Glow3, Color Glow4)
{
    public bool IsDefault => Id == AccentThemes.Default.Id;
}

internal static class AccentThemes
{
    public static readonly AccentTheme Default = new("aurora", "Aurora",
        Color.Parse("#B45CF6"), Color.Parse("#6C63FF"), Color.Parse("#22C8EE"),
        Color.Parse("#8B5CF6"), Color.Parse("#22C8EE"), Color.Parse("#FF4FA3"), Color.Parse("#2F6BFF"));

    public static IReadOnlyList<AccentTheme> All { get; } =
    [
        Default,
        new("ember", "Ember", Color.Parse("#FFB457"), Color.Parse("#FF6A5E"), Color.Parse("#FF3D8B"),
            Color.Parse("#FF7A45"), Color.Parse("#FFB457"), Color.Parse("#FF3D6E"), Color.Parse("#C2410C")),
        new("mint", "Mint", Color.Parse("#3EE0A6"), Color.Parse("#14B8A6"), Color.Parse("#22C8EE"),
            Color.Parse("#10B981"), Color.Parse("#22C8EE"), Color.Parse("#3EE0A6"), Color.Parse("#0E7490")),
        new("sakura", "Sakura", Color.Parse("#FF8FC7"), Color.Parse("#E86AF0"), Color.Parse("#A78BFA"),
            Color.Parse("#F472B6"), Color.Parse("#C084FC"), Color.Parse("#FF8FC7"), Color.Parse("#7C3AED")),
        new("ocean", "Ocean", Color.Parse("#38BDF8"), Color.Parse("#3B82F6"), Color.Parse("#6366F1"),
            Color.Parse("#2563EB"), Color.Parse("#38BDF8"), Color.Parse("#6366F1"), Color.Parse("#0EA5E9")),
        new("gold", "Gold", Color.Parse("#FDE68A"), Color.Parse("#F59E0B"), Color.Parse("#EA580C"),
            Color.Parse("#F59E0B"), Color.Parse("#FDE68A"), Color.Parse("#EA580C"), Color.Parse("#B45309")),
    ];

    public static AccentTheme Find(string? id) => All.FirstOrDefault(t => t.Id == id) ?? Default;

    public static IBrush Preview(AccentTheme theme) => Gradient(theme.Start, theme.Middle, theme.End);

    private static LinearGradientBrush Gradient(Color start, Color middle, Color end, double middleOffset = 0.5) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new(start, 0), new(middle, middleOffset), new(end, 1) },
    };

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    // The accent brushes are looked up as dynamic resources, so replacing them at the application level recolours every
    // button, badge and slider at once. The default theme removes the overrides and falls back to Theme/Glass.axaml.
    public static void ApplyAccent(AccentTheme theme)
    {
        if (Application.Current is not { } app) return;
        if (theme.IsDefault)
        {
            foreach (var key in Overrides) app.Resources.Remove(key);
            return;
        }
        app.Resources["AccentGradient"] = Gradient(theme.Start, theme.Middle, theme.End);
        app.Resources["AccentGradientHover"] = Gradient(Lighten(theme.Start), Lighten(theme.Middle), Lighten(theme.End));
        app.Resources["AccentWash"] = Gradient(WithAlpha(theme.Glow1, 0x44), WithAlpha(theme.Middle, 0x1A), WithAlpha(theme.End, 0x2E), 0.55);

        // Fluent's own accent, used by focus rings, toggles and text selection.
        var system = Mix(theme.Start, theme.Middle, 0.5);
        app.Resources["SystemAccentColor"] = system;
        for (var i = 1; i <= 3; i++)
        {
            app.Resources[$"SystemAccentColorLight{i}"] = Mix(system, Colors.White, 0.15 * i);
            app.Resources[$"SystemAccentColorDark{i}"] = Mix(system, Colors.Black, 0.15 * i);
        }
    }

    private static readonly string[] Overrides =
    [
        "AccentGradient", "AccentGradientHover", "AccentWash", "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // The four soft glows behind the main window, in the order they're declared.
    public static void ApplyBackground(AccentTheme theme, params Ellipse?[] glows)
    {
        Color[] colors = [theme.Glow1, theme.Glow2, theme.Glow3, theme.Glow4];
        for (var i = 0; i < glows.Length && i < colors.Length; i++)
        {
            if (glows[i]?.Fill is not GradientBrush fill) continue;
            foreach (var stop in fill.GradientStops)
            {
                stop.Color = Color.FromArgb(stop.Color.A, colors[i].R, colors[i].G, colors[i].B);
            }
        }
    }

    private static Color Lighten(Color c) => Mix(c, Colors.White, 0.15);
}
