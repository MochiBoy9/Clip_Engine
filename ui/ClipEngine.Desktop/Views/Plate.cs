using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;

namespace ClipEngine.Desktop.Views;

// The app's basic surface: a flat panel with its top-left and bottom-right corners cut off at 45 degrees.
// Marker, when set, fills the cut-off top-left corner as a small tab of colour.
internal sealed class Plate : Decorator, ICustomHitTest
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<Plate, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<Plate, IBrush?>(nameof(BorderBrush));

    public static readonly StyledProperty<double> BorderThicknessProperty =
        AvaloniaProperty.Register<Plate, double>(nameof(BorderThickness), 1);

    public static readonly StyledProperty<double> CutProperty =
        AvaloniaProperty.Register<Plate, double>(nameof(Cut), 10);

    public static readonly StyledProperty<IBrush?> MarkerProperty =
        AvaloniaProperty.Register<Plate, IBrush?>(nameof(Marker));

    // Brackets drawn brighter over the two corners that aren't cut, like crop marks on a print.
    public static readonly StyledProperty<IBrush?> BracketsProperty =
        AvaloniaProperty.Register<Plate, IBrush?>(nameof(Brackets));

    private StreamGeometry? _shape;
    private Size _shapeSize;
    private double _shapeCut = -1;
    private double _shapeInset = -1;

    static Plate()
    {
        AffectsRender<Plate>(BackgroundProperty, BorderBrushProperty, BorderThicknessProperty, CutProperty, MarkerProperty, BracketsProperty);
    }

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? BorderBrush { get => GetValue(BorderBrushProperty); set => SetValue(BorderBrushProperty, value); }
    public double BorderThickness { get => GetValue(BorderThicknessProperty); set => SetValue(BorderThicknessProperty, value); }
    public double Cut { get => GetValue(CutProperty); set => SetValue(CutProperty, value); }
    public IBrush? Marker { get => GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }
    public IBrush? Brackets { get => GetValue(BracketsProperty); set => SetValue(BracketsProperty, value); }

    // Cuts the content to the plate's shape too, for a picture that runs to its edges.
    public static readonly StyledProperty<bool> ClipContentProperty =
        AvaloniaProperty.Register<Plate, bool>(nameof(ClipContent));

    public bool ClipContent { get => GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if (ClipContent && Child is { } child)
        {
            var cut = Math.Min(Cut, Math.Min(size.Width, size.Height) / 2);
            var origin = new Vector(child.Bounds.X, child.Bounds.Y);
            var clip = new StreamGeometry();
            using (var g = clip.Open())
            {
                var (w, h) = (size.Width, size.Height);
                g.BeginFigure(new Point(cut, 0) - origin, true);
                g.LineTo(new Point(w, 0) - origin);
                g.LineTo(new Point(w, h - cut) - origin);
                g.LineTo(new Point(w - cut, h) - origin);
                g.LineTo(new Point(0, h) - origin);
                g.LineTo(new Point(0, cut) - origin);
                g.EndFigure(true);
            }
            child.Clip = clip;
        }
        return size;
    }

    public bool HitTest(Point point) => Background is not null && new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) return;
        var cut = Math.Min(Cut, Math.Min(size.Width, size.Height) / 2);
        var stroke = BorderBrush is not null && BorderThickness > 0 ? BorderThickness : 0;
        context.DrawGeometry(Background, stroke > 0 ? new Pen(BorderBrush, stroke) : null, Shape(size, cut, stroke / 2));

        if (Marker is { } marker && cut >= 6)
        {
            // A triangle in the corner the cut left empty, with a hairline gap so it reads as its own piece.
            var tab = cut - 3;
            var corner = new StreamGeometry();
            using (var g = corner.Open())
            {
                g.BeginFigure(new Point(0, 0), true);
                g.LineTo(new Point(tab, 0));
                g.LineTo(new Point(0, tab));
                g.EndFigure(true);
            }
            context.DrawGeometry(marker, null, corner);
        }

        if (Brackets is { } brackets && size.Width > 40 && size.Height > 40)
        {
            const double arm = 9;
            var (w, h) = (size.Width, size.Height);
            context.FillRectangle(brackets, new Rect(w - arm, 0, arm, 1));
            context.FillRectangle(brackets, new Rect(w - 1, 0, 1, arm));
            context.FillRectangle(brackets, new Rect(0, h - 1, arm, 1));
            context.FillRectangle(brackets, new Rect(0, h - arm, 1, arm));
        }
    }

    // Rebuilt only when the size or the cut changes, so redraws for hover or content changes reuse it.
    private StreamGeometry Shape(Size size, double cut, double inset)
    {
        if (_shape is not null && size == _shapeSize && cut == _shapeCut && inset == _shapeInset) return _shape;
        var (l, t, r, b) = (inset, inset, size.Width - inset, size.Height - inset);
        var c = Math.Max(0, cut - inset * 0.4);
        var shape = new StreamGeometry();
        using (var g = shape.Open())
        {
            g.BeginFigure(new Point(l + c, t), true);
            g.LineTo(new Point(r, t));
            g.LineTo(new Point(r, b - c));
            g.LineTo(new Point(r - c, b));
            g.LineTo(new Point(l, b));
            g.LineTo(new Point(l, t + c));
            g.EndFigure(true);
        }
        (_shape, _shapeSize, _shapeCut, _shapeInset) = (shape, size, cut, inset);
        return shape;
    }
}

// Diagonal stripes, like hazard tape. Drawn once per size; used as a small accent, never behind text.
internal sealed class Stripes : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Stripes, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<Stripes, double>(nameof(Spacing), 8);

    static Stripes()
    {
        AffectsRender<Stripes>(FillProperty, SpacingProperty);
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Fill is null || Spacing < 2) return;
        var (w, h) = (Bounds.Width, Bounds.Height);
        var band = Spacing / 2;
        var stripes = new StreamGeometry();
        using (var g = stripes.Open())
        {
            for (var x = -h; x < w; x += Spacing)
            {
                g.BeginFigure(new Point(x, h), true);
                g.LineTo(new Point(x + h, 0));
                g.LineTo(new Point(x + h + band, 0));
                g.LineTo(new Point(x + band, h));
                g.EndFigure(true);
            }
        }
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            context.DrawGeometry(Fill, null, stripes);
        }
    }
}
