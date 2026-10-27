using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// The player's seek bar: the played part, a tick for every detected moment, and the playhead.
// Click or drag anywhere to seek. While trimming it also shows the part being kept, with a
// handle at each end to drag.
internal sealed class MarkerTimeline : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<MarkerTimeline, double>(nameof(Position),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<MarkerTimeline, double>(nameof(Duration));

    public static readonly StyledProperty<IReadOnlyList<MarkerViewModel>?> MarkersProperty =
        AvaloniaProperty.Register<MarkerTimeline, IReadOnlyList<MarkerViewModel>?>(nameof(Markers));

    public static readonly StyledProperty<bool> IsTrimmingProperty =
        AvaloniaProperty.Register<MarkerTimeline, bool>(nameof(IsTrimming));

    public static readonly StyledProperty<double> TrimStartProperty =
        AvaloniaProperty.Register<MarkerTimeline, double>(nameof(TrimStart),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> TrimEndProperty =
        AvaloniaProperty.Register<MarkerTimeline, double>(nameof(TrimEnd),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double TrackHeight = 6;
    private const double HandleWidth = 6;
    private const double HandleHeight = 26;
    private const double HandleGrab = 10;
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

    private static readonly IBrush TrimOutsideBrush = new SolidColorBrush(Color.Parse("#B30B0918"));
    private static readonly IBrush TrimRangeBrush = new SolidColorBrush(Color.Parse("#1AFFC24B"));
    private static readonly IBrush HandleBrush = new SolidColorBrush(Color.Parse("#FFC24B"));

    private enum Drag { None, Seek, TrimStart, TrimEnd }

    private Drag _drag;

    static MarkerTimeline()
    {
        AffectsRender<MarkerTimeline>(PositionProperty, DurationProperty, MarkersProperty, IsTrimmingProperty,
                                      TrimStartProperty, TrimEndProperty);
        CursorProperty.OverrideDefaultValue<MarkerTimeline>(new Cursor(StandardCursorType.Hand));
    }

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public IReadOnlyList<MarkerViewModel>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public bool IsTrimming { get => GetValue(IsTrimmingProperty); set => SetValue(IsTrimmingProperty, value); }
    public double TrimStart { get => GetValue(TrimStartProperty); set => SetValue(TrimStartProperty, value); }
    public double TrimEnd { get => GetValue(TrimEndProperty); set => SetValue(TrimEndProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 200 : availableSize.Width, 32);

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

        if (IsTrimming && Duration > 0)
        {
            var from = left + width * Math.Clamp(TrimStart / Duration, 0, 1);
            var to = left + width * Math.Clamp(TrimEnd / Duration, 0, 1);
            var band = new Rect(0, mid - HandleHeight / 2, Bounds.Width, HandleHeight);
            context.FillRectangle(TrimOutsideBrush, band.WithWidth(from));
            context.FillRectangle(TrimOutsideBrush, new Rect(to, band.Y, Bounds.Width - to, band.Height));
            context.DrawRectangle(TrimRangeBrush, new Pen(HandleBrush, 1.5), new Rect(from, band.Y, to - from, band.Height), 4, 4);
            foreach (var x in new[] { from, to })
            {
                context.DrawRectangle(HandleBrush, null, new Rect(x - HandleWidth / 2, band.Y - 2, HandleWidth, band.Height + 4),
                                      HandleWidth / 2, HandleWidth / 2);
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
        var x = e.GetPosition(this).X;
        _drag = HandleAt(x);
        e.Pointer.Capture(this);
        if (_drag == Drag.Seek) SeekTo(x, snap: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var x = e.GetPosition(this).X;
        switch (_drag)
        {
            case Drag.Seek:
                SeekTo(x, snap: false);
                break;
            case Drag.TrimStart:
                TrimStart = SecondsAt(x);
                Position = TrimStart;
                break;
            case Drag.TrimEnd:
                TrimEnd = SecondsAt(x);
                Position = TrimEnd;
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    private double SecondsAt(double x)
    {
        var width = Bounds.Width - ThumbRadius * 2;
        return width <= 0 || Duration <= 0 ? 0 : Math.Clamp((x - ThumbRadius) / width, 0, 1) * Duration;
    }

    // Which trim handle is under x, if any; otherwise a click seeks.
    private Drag HandleAt(double x)
    {
        if (!IsTrimming || Duration <= 0) return Drag.Seek;
        var width = Bounds.Width - ThumbRadius * 2;
        var from = ThumbRadius + width * Math.Clamp(TrimStart / Duration, 0, 1);
        var to = ThumbRadius + width * Math.Clamp(TrimEnd / Duration, 0, 1);
        var toStart = Math.Abs(x - from);
        var toEnd = Math.Abs(x - to);
        if (Math.Min(toStart, toEnd) > HandleGrab) return Drag.Seek;
        return toStart < toEnd || (toStart == toEnd && x < from) ? Drag.TrimStart : Drag.TrimEnd;
    }

    private void SeekTo(double x, bool snap)
    {
        var width = Bounds.Width - ThumbRadius * 2;
        if (width <= 0 || Duration <= 0) return;
        var seconds = SecondsAt(x);

        // A click right on a marker jumps to it, a little before so the moment isn't missed.
        if (snap && Markers is { } markers)
        {
            var near = markers.MinBy(m => Math.Abs(m.Seconds - seconds));
            if (near is not null && Math.Abs(near.Seconds - seconds) / Duration * width <= 6) seconds = near.SeekSeconds;
        }
        Position = seconds;
    }
}
