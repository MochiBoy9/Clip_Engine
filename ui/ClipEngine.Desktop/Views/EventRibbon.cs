using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// A thin strip with a colored tick for every moment, laid out in time, so a clip or a match
// can be read at a glance: kills in pink, deaths in gray, revives in green.
internal sealed class EventRibbon : Control
{
    public static readonly StyledProperty<IReadOnlyList<RibbonTick>?> TicksProperty =
        AvaloniaProperty.Register<EventRibbon, IReadOnlyList<RibbonTick>?>(nameof(Ticks));

    private const double TickWidth = 3;
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#2A2D35"));

    static EventRibbon() => AffectsRender<EventRibbon>(TicksProperty);

    public IReadOnlyList<RibbonTick>? Ticks { get => GetValue(TicksProperty); set => SetValue(TicksProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width, double.IsNaN(Height) ? 10 : Height);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= TickWidth || height <= 0) return;
        var trackHeight = Math.Max(2, height / 4);
        context.DrawRectangle(TrackBrush, null, new Rect(0, (height - trackHeight) / 2, width, trackHeight),
                              trackHeight / 2, trackHeight / 2);
        if (Ticks is null) return;
        foreach (var tick in Ticks)
        {
            var x = (width - TickWidth) * Math.Clamp(tick.Fraction, 0, 1);
            context.DrawRectangle(tick.Brush, null, new Rect(x, 0, TickWidth, height), TickWidth / 2, TickWidth / 2);
        }
    }
}
