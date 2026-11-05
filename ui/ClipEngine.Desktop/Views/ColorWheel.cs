using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.Views;

// Ultra's colour wheel: hue round the edge, saturation from the grey centre out, at the brightness the slider beside
// it sets. It shows each colour as it would apply, so colours too dark for the panels appear already lifted, and a
// ring of ticks like a dial on a printed instrument panel goes round it. The disc is drawn once per size and
// brightness, and dragging only moves the reticle.
internal sealed class ColorWheel : Control
{
    public static readonly StyledProperty<double> HueProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(Hue), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> SaturationProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(Saturation), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> BrightnessProperty =
        AvaloniaProperty.Register<ColorWheel, double>(nameof(Brightness), 1);

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<ColorWheel, IBrush?>(nameof(TickBrush));

    // Space for the ring of ticks round the disc.
    private const double Ring = 12;

    private WriteableBitmap? _disc;
    private (int Size, double Brightness) _discKey;
    private bool _dragging;

    static ColorWheel()
    {
        AffectsRender<ColorWheel>(HueProperty, SaturationProperty, BrightnessProperty, TickBrushProperty);
        FocusableProperty.OverrideDefaultValue<ColorWheel>(true);
    }

    public ColorWheel()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public IBrush? TickBrush { get => GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }

    // A drag or a click ended: the colour picked is the one to keep.
    public event EventHandler? Committed;

    private double Radius => Math.Max(0, Math.Min(Bounds.Width, Bounds.Height) / 2 - Ring);
    private Point Centre => new(Bounds.Width / 2, Bounds.Height / 2);

    public override void Render(DrawingContext context)
    {
        var r = Radius;
        if (r < 4) return;
        var centre = Centre;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = (int)Math.Ceiling(r * 2 * scale);
        if (_disc is null || _discKey != (size, Brightness))
        {
            _disc?.Dispose();
            _disc = DrawDisc(size, Brightness);
            _discKey = (size, Brightness);
        }
        context.DrawImage(_disc, new Rect(centre.X - r, centre.Y - r, r * 2, r * 2));

        // The dial: a tick every 5 degrees, longer every 30, from the top round clockwise.
        if (TickBrush is { } ticks)
        {
            var pen = new Pen(ticks, 1);
            for (var i = 0; i < 72; i++)
            {
                var a = (i * 5 - 90) * Math.PI / 180;
                var (inner, outer) = i % 6 == 0 ? (r + 3, r + Ring - 1) : (r + 3, r + 7);
                var d = new Vector(Math.Cos(a), Math.Sin(a));
                context.DrawLine(pen, centre + d * inner, centre + d * outer);
            }
        }

        // The reticle: a square with its corners open, and a mark on the dial at the hue.
        var at = PointFor(Hue, Saturation);
        var dark = new Pen(new SolidColorBrush(Color.Parse("#0B0C0E")), 3);
        var light = new Pen(Brushes.White, 1.5);
        foreach (var pen in new[] { dark, light })
        {
            const double h = 7, g = 3;
            context.DrawLine(pen, at + new Vector(-h, -h), at + new Vector(-g, -h));
            context.DrawLine(pen, at + new Vector(-h, -h), at + new Vector(-h, -g));
            context.DrawLine(pen, at + new Vector(h, -h), at + new Vector(g, -h));
            context.DrawLine(pen, at + new Vector(h, -h), at + new Vector(h, -g));
            context.DrawLine(pen, at + new Vector(-h, h), at + new Vector(-g, h));
            context.DrawLine(pen, at + new Vector(-h, h), at + new Vector(-h, g));
            context.DrawLine(pen, at + new Vector(h, h), at + new Vector(g, h));
            context.DrawLine(pen, at + new Vector(h, h), at + new Vector(h, g));
        }
        var ha = (Hue - 90) * Math.PI / 180;
        var hd = new Vector(Math.Cos(ha), Math.Sin(ha));
        context.DrawLine(new Pen(Brushes.White, 2), centre + hd * (r + 2), centre + hd * (r + Ring));
    }

    private Point PointFor(double hue, double saturation)
    {
        var a = (hue - 90) * Math.PI / 180;
        return Centre + new Vector(Math.Cos(a), Math.Sin(a)) * (Math.Clamp(saturation, 0, 1) * Radius);
    }

    private void Pick(Point p)
    {
        var r = Radius;
        if (r < 4) return;
        var v = p - Centre;
        var hue = Math.Atan2(v.Y, v.X) * 180 / Math.PI + 90;
        if (hue < 0) hue += 360;
        Hue = Math.Round(hue, 1) % 360;
        Saturation = Math.Round(Math.Clamp(Math.Sqrt(v.X * v.X + v.Y * v.Y) / r, 0, 1), 3);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Pick(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        Committed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging) return;
        _dragging = false;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    // Arrow keys turn the hue and move toward or away from grey.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 2;
        switch (e.Key)
        {
            case Key.Left: Hue = (Hue - step + 360) % 360; break;
            case Key.Right: Hue = (Hue + step) % 360; break;
            case Key.Up: Saturation = Math.Min(1, Saturation + step / 100.0); break;
            case Key.Down: Saturation = Math.Max(0, Saturation - step / 100.0); break;
            default: return;
        }
        e.Handled = true;
        Committed?.Invoke(this, EventArgs.Empty);
    }

    // One pixel per colour the wheel can give, as it would apply.
    private static unsafe WriteableBitmap DrawDisc(int size, double brightness)
    {
        var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var buffer = bitmap.Lock();
        var half = size / 2.0;
        for (var y = 0; y < size; y++)
        {
            var row = (uint*)(buffer.Address + y * buffer.RowBytes);
            for (var x = 0; x < size; x++)
            {
                var dx = x + 0.5 - half;
                var dy = y + 0.5 - half;
                var d = Math.Sqrt(dx * dx + dy * dy);
                var alpha = Math.Clamp(half - d, 0, 1);
                if (alpha <= 0)
                {
                    row[x] = 0;
                    continue;
                }
                var hue = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
                var c = AccentThemes.Legible(AdaptiveLook.FromHsv(hue, d / half, brightness));
                var a = (uint)Math.Round(alpha * 255);
                row[x] = (a << 24) | ((uint)(c.R * alpha) << 16) | ((uint)(c.G * alpha) << 8) | (uint)(c.B * alpha);
            }
        }
        return bitmap;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _disc?.Dispose();
        _disc = null;
    }
}
