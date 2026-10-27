using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// The player's seek bar: the played part, a tick for every detected moment, and the playhead.
// Click or drag anywhere to seek.
internal sealed class MarkerTimeline : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<MarkerTimeline, double>(nameof(Position),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<MarkerTimeline, double>(nameof(Duration));

    public static readonly StyledProperty<IReadOnlyList<MarkerViewModel>?> MarkersProperty =
        AvaloniaProperty.Register<MarkerTimeline, IReadOnlyList<MarkerViewModel>?>(nameof(Markers));

    private const double TrackHeight = 6;
    private const double MarkerWidth = 4;
    private const double MarkerHeight = 18;
    private const double ThumbRadius = 8;

    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#26FFFFFF"));
    private static readonly IBrush ThumbBrush = Brushes.White;
    private static readonly IBrush PlayedBrush = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#B45CF6"), 0), new GradientStop(Color.Parse("#22C8EE"), 1) },
    };

    private bool _dragging;

    static MarkerTimeline()
    {
        AffectsRender<MarkerTimeline>(PositionProperty, DurationProperty, MarkersProperty);
        CursorProperty.OverrideDefaultValue<MarkerTimeline>(new Cursor(StandardCursorType.Hand));
    }

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public IReadOnlyList<MarkerViewModel>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 28);

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width - ThumbRadius * 2;
        if (width <= 0) return;
        var mid = Bounds.Height / 2;
        var left = ThumbRadius;
        var fraction = Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;

        // Transparent fill so clicks anywhere on the control land on it.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var track = new Rect(left, mid - TrackHeight / 2, width, TrackHeight);
        context.DrawRectangle(TrackBrush, null, track, TrackHeight / 2, TrackHeight / 2);
        if (fraction > 0)
        {
            var played = track.WithWidth(Math.Max(TrackHeight, width * fraction));
            context.DrawRectangle(PlayedBrush, null, played, TrackHeight / 2, TrackHeight / 2);
        }

        if (Markers is { } markers && Duration > 0)
        {
            var outline = new Pen(new SolidColorBrush(Color.Parse("#B3100C24")), 1.5);
            foreach (var marker in markers)
            {
                var x = left + width * Math.Clamp(marker.Seconds / Duration, 0, 1);
                var rect = new Rect(x - MarkerWidth / 2, mid - MarkerHeight / 2, MarkerWidth, MarkerHeight);
                context.DrawRectangle(marker.Brush, outline, rect, MarkerWidth / 2, MarkerWidth / 2);
            }
        }

        var thumb = new Point(left + width * fraction, mid);
        context.DrawEllipse(ThumbBrush, new Pen(new SolidColorBrush(Color.Parse("#668B6CFF")), 4), thumb, ThumbRadius - 2,
                            ThumbRadius - 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        SeekTo(e.GetPosition(this).X, snap: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) SeekTo(e.GetPosition(this).X, snap: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void SeekTo(double x, bool snap)
    {
        var width = Bounds.Width - ThumbRadius * 2;
        if (width <= 0 || Duration <= 0) return;
        var seconds = Math.Clamp((x - ThumbRadius) / width, 0, 1) * Duration;

        // A click right on a marker jumps to it, a little before so the moment isn't missed.
        if (snap && Markers is { } markers)
        {
            var near = markers.MinBy(m => Math.Abs(m.Seconds - seconds));
            if (near is not null && Math.Abs(near.Seconds - seconds) / Duration * width <= 6) seconds = near.SeekSeconds;
        }
        Position = seconds;
    }
}
