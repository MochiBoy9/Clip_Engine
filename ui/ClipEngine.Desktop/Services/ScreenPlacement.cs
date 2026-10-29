using System.Globalization;

namespace ClipEngine.Desktop.Services;

// Where a rule's box falls on a screen of a given shape with the game's HUD at a given size, the
// way the engine places it (PlaceRegion in src/detect/ScreenRegion.cpp), and back again for a
// box drawn on a frame. Detection Studio draws boxes where the engine will look on that frame.
internal static class ScreenPlacement
{
    internal enum Anchor { Auto, Start, Center, End }

    internal const double DefaultAspect = 16.0 / 9.0;

    internal readonly record struct Box(double Left, double Top, double Width, double Height);

    // The box on a screen of width × height pixels, as fractions of it. anchor and aspect are the
    // rule's settings of those names (null when it has none).
    public static Box Place(Box box, string? anchor, string? aspect, double width, double height, double hudScale)
    {
        if (width <= 0 || height <= 0) return box;
        var (ax, ay) = ParseAnchor(anchor);
        var (designW, designH) = DesignArea(ParseAspect(aspect) ?? DefaultAspect, width, height);
        hudScale = Math.Clamp(hudScale, 0.25, 4);
        var (left, w) = PlaceAxis(Resolve(ax, box.Left, box.Width), designW * hudScale / width, box.Left, box.Width);
        var (top, h) = PlaceAxis(Resolve(ay, box.Top, box.Height), designH * hudScale / height, box.Top, box.Height);
        return new Box(left, top, w, h);
    }

    // What to save for a box drawn on a frame of width × height pixels at that HUD size: the box
    // as made for a screen that shape at 100%, the aspect setting (null for 16:9) and the anchor
    // setting (the rule's own, or one that says what Auto wouldn't, or null).
    public static (Box Box, string? Aspect, string? Anchor) Unplace(Box drawn, string? anchor, double width, double height,
                                                                     double hudScale)
    {
        hudScale = Math.Clamp(hudScale, 0.25, 4);
        var (ax, ay) = ParseAnchor(anchor);
        var useX = Resolve(ax, drawn.Left, drawn.Width);
        var useY = Resolve(ay, drawn.Top, drawn.Height);
        var (left, w) = UnplaceAxis(useX, hudScale, drawn.Left, drawn.Width);
        var (top, h) = UnplaceAxis(useY, hudScale, drawn.Top, drawn.Height);

        string? aspect = null;
        if (width > 0 && height > 0 && Math.Abs(width / height - DefaultAspect) > 0.01)
            aspect = $"{width.ToString(CultureInfo.InvariantCulture)}:{height.ToString(CultureInfo.InvariantCulture)}";

        // Auto would decide by where the saved box is, which a HUD size other than 100% moves.
        var keep = anchor;
        if (ax == Anchor.Auto && ay == Anchor.Auto && (Resolve(Anchor.Auto, left, w) != useX || Resolve(Anchor.Auto, top, h) != useY))
            keep = AnchorText(useX, useY);
        return (new Box(left, top, w, h), aspect, keep);
    }

    public static (Anchor X, Anchor Y) ParseAnchor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (Anchor.Auto, Anchor.Auto);
        Anchor x = Anchor.Auto, y = Anchor.Auto;
        bool setX = false, setY = false;
        var middles = 0;
        foreach (var word in text.ToLowerInvariant().Split(new[] { ' ', '-', '_', ',', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (word)
            {
                case "auto": break;
                case "left" or "right" when !setX: x = word == "left" ? Anchor.Start : Anchor.End; setX = true; break;
                case "top" or "bottom" when !setY: y = word == "top" ? Anchor.Start : Anchor.End; setY = true; break;
                case "center" or "centre" or "middle": middles++; break;
                default: return (Anchor.Auto, Anchor.Auto);
            }
        }
        // "center" is whichever axis the rest doesn't name, or both.
        if (middles > 0)
        {
            if (setX && setY) return (Anchor.Auto, Anchor.Auto);
            if (!setX) x = Anchor.Center;
            if (!setY) y = Anchor.Center;
        }
        return (x, y);
    }

    public static double? ParseAspect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        double value;
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            if (!double.TryParse(text[..colon], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
                || !double.TryParse(text[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || w <= 0 || h <= 0)
                return null;
            value = w / h;
        }
        else if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }
        return value is >= 0.5 and <= 6 ? value : null;
    }

    private static string AnchorText(Anchor x, Anchor y)
    {
        var across = x switch { Anchor.Start => "left", Anchor.End => "right", _ => "center" };
        var down = y switch { Anchor.Start => "top", Anchor.End => "bottom", _ => "center" };
        return across == "center" && down == "center" ? "center" : $"{down}-{across}";
    }

    private static (double W, double H) DesignArea(double aspect, double width, double height) =>
        width / height >= aspect ? (height * aspect, height) : (width, width / aspect);

    private static Anchor Resolve(Anchor anchor, double start, double size)
    {
        if (anchor != Anchor.Auto) return anchor;
        var middle = start + size / 2;
        return middle < 1.0 / 3 ? Anchor.Start : middle > 2.0 / 3 ? Anchor.End : Anchor.Center;
    }

    private static (double Start, double Size) PlaceAxis(Anchor anchor, double k, double start, double size)
    {
        var placedSize = Math.Min(size * k, 1);
        var placed = anchor switch
        {
            Anchor.End => 1 - (1 - (start + size)) * k - placedSize,
            Anchor.Center => 0.5 + (start + size / 2 - 0.5) * k - placedSize / 2,
            _ => start * k,
        };
        return (Math.Clamp(placed, 0, 1 - placedSize), placedSize);
    }

    private static (double Start, double Size) UnplaceAxis(Anchor anchor, double k, double start, double size)
    {
        var original = Math.Min(size / k, 1);
        var at = anchor switch
        {
            Anchor.End => 1 - (1 - (start + size)) / k - original,
            Anchor.Center => 0.5 + (start + size / 2 - 0.5) / k - original / 2,
            _ => start / k,
        };
        return (Math.Clamp(at, 0, 1 - original), original);
    }
}
