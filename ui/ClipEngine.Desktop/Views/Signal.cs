using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClipEngine.Desktop.Views;

// The retro-futuristic marks the app is dressed in: printed-matter details like barcodes, ruled lines with ticks,
// numbered section marks and a dotted grid behind everything. All flat and drawn once per size.

// A barcode made from a word, so the same word always gets the same bars. Decoration only.
internal sealed class Barcode : Control
{
    public static readonly StyledProperty<string?> SeedProperty =
        AvaloniaProperty.Register<Barcode, string?>(nameof(Seed));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Barcode, IBrush?>(nameof(Fill));

    static Barcode()
    {
        AffectsRender<Barcode>(SeedProperty, FillProperty);
    }

    public string? Seed { get => GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    // Bar and gap widths in units: guard bars at both ends, then two bars and two gaps for each character.
    internal static IReadOnlyList<int> Pattern(string seed)
    {
        var widths = new List<int> { 1, 1, 1, 1 };
        var hash = 2166136261u;
        foreach (var c in seed)
        {
            hash = (hash ^ c) * 16777619u;
            for (var i = 0; i < 4; i++) widths.Add(1 + (int)((hash >> (i * 7)) % 3));
        }
        widths.AddRange([1, 1, 1]);
        return widths;
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is null || string.IsNullOrEmpty(Seed)) return;
        var pattern = Pattern(Seed);
        var total = pattern.Sum();
        var unit = Bounds.Width / total;
        var x = 0.0;
        for (var i = 0; i < pattern.Count; i++)
        {
            var w = pattern[i] * unit;
            if (i % 2 == 0) context.FillRectangle(Fill, new Rect(Math.Round(x), 0, Math.Max(1, Math.Round(w)), Bounds.Height));
            x += w;
        }
    }
}

// A hairline with ticks along it, short every 8 px and taller every 40, like a ruler.
internal sealed class TickRule : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<TickRule, IBrush?>(nameof(Fill));

    static TickRule()
    {
        AffectsRender<TickRule>(FillProperty);
    }

    public TickRule()
    {
        Height = 7;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Fill is null) return;
        var (w, h) = (Bounds.Width, Bounds.Height);
        context.FillRectangle(Fill, new Rect(0, h - 1, w, 1));
        for (var x = 0; x < w; x += 8)
        {
            var tall = x % 40 == 0;
            context.FillRectangle(Fill, new Rect(x, tall ? 0 : h - 4, 1, tall ? h : 4));
        }
    }
}

// The line over a page's title: its number, its name in small capitals, a ruler to the edge and a barcode.
//   02  CLIP LIBRARY  |''''|''''|''''|''''|''''  ||| || |
internal sealed class SectionMark : Grid
{
    public static readonly StyledProperty<string?> IndexProperty =
        AvaloniaProperty.Register<SectionMark, string?>(nameof(Index));

    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<SectionMark, string?>(nameof(Label));

    private readonly TextBlock _index = new() { Classes = { "markIndex" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { Classes = { "markLabel" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly Barcode _code = new() { Width = 56, Height = 11, VerticalAlignment = VerticalAlignment.Center };

    public SectionMark()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto");
        var rule = new TickRule { Margin = new Thickness(14, 0) };
        rule[!TickRule.FillProperty] = this.GetResourceObservable("BorderStrongBrush").ToBinding();
        _code[!Barcode.FillProperty] = this.GetResourceObservable("TextTertiary").ToBinding();
        _label.Margin = new Thickness(10, 0, 0, 0);
        SetColumn(_label, 1);
        SetColumn(rule, 2);
        SetColumn(_code, 3);
        Children.Add(_index);
        Children.Add(_label);
        Children.Add(rule);
        Children.Add(_code);
        Margin = new Thickness(0, 0, 0, 6);
    }

    public string? Index { get => GetValue(IndexProperty); set => SetValue(IndexProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IndexProperty) _index.Text = Index;
        else if (change.Property == LabelProperty)
        {
            _label.Text = Label?.ToUpperInvariant();
            _code.Seed = Label;
        }
    }
}

// Dots on a 24 px grid behind the pages, like graph paper. One tile, repeated by the GPU.
internal sealed class DotGrid : Control
{
    public const int Step = 24;
    private static IBrush? _brush;

    public override void Render(DrawingContext context)
    {
        _brush ??= CreateBrush();
        context.FillRectangle(_brush, new Rect(Bounds.Size));
    }

    private static IBrush CreateBrush()
    {
        var tile = new WriteableBitmap(new PixelSize(Step, Step), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var buffer = tile.Lock())
        {
            unsafe
            {
                var p = (uint*)buffer.Address;
                for (var i = 0; i < Step * buffer.RowBytes / 4; i++) p[i] = 0;
                // One soft dot; premultiplied BGRA.
                p[0] = 0x2E2E2E2Eu;
                p[1] = 0x14141414u;
                p[buffer.RowBytes / 4] = 0x14141414u;
            }
        }
        return new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            DestinationRect = new RelativeRect(0, 0, Step, Step, RelativeUnit.Absolute),
        }.ToImmutable();
    }
}
