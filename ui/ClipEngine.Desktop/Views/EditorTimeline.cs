using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ClipEngine.Desktop.Services.Editing;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// The editor's timeline: a ruler, the edit's parts end to end with the detected moments on them,
// and the playhead. Click or drag to scrub and pick a part; drag either end of a part to trim it,
// and the rest of the edit closes up. The wheel scrolls when zoomed in, Ctrl and the wheel zoom
// around the pointer.
internal sealed class EditorTimeline : Control
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(Position), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(Duration));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<EditorTimeline, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyList<EditPartViewModel>?> PartsProperty =
        AvaloniaProperty.Register<EditorTimeline, IReadOnlyList<EditPartViewModel>?>(nameof(Parts));

    public static readonly StyledProperty<IReadOnlyList<EditorMarker>?> MarkersProperty =
        AvaloniaProperty.Register<EditorTimeline, IReadOnlyList<EditorMarker>?>(nameof(Markers));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<EditorTimeline, int>(nameof(SelectedIndex));

    private const double Pad = 10;
    private const double RulerHeight = 22;
    private const double TrackTop = RulerHeight + 6;
    private const double TrackHeight = 56;
    private const double OverviewHeight = 4;
    private const double HandleWidth = 6;
    private const double HandleGrab = 8;

    private static readonly Typeface LabelFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface RulerFace = new("Inter", FontStyle.Normal, FontWeight.Normal);
    private static readonly IBrush PartBrush = new SolidColorBrush(Color.Parse("#22242A"));
    private static readonly IPen PartOutline = new Pen(new SolidColorBrush(Color.Parse("#363942")), 1);
    private static readonly IBrush PartText = new SolidColorBrush(Color.Parse("#B9B7AF"));
    private static readonly IBrush RulerText = new SolidColorBrush(Color.Parse("#706F69"));
    private static readonly IPen RulerTick = new Pen(new SolidColorBrush(Color.Parse("#3A3D46")), 1);
    private static readonly IPen RulerMinorTick = new Pen(new SolidColorBrush(Color.Parse("#2A2C33")), 1);
    private static readonly IBrush OverviewTrack = new SolidColorBrush(Color.Parse("#22242A"));
    private static readonly IBrush OverviewThumb = new SolidColorBrush(Color.Parse("#4A4D57"));
    private static readonly IPen MarkerOutline = new Pen(new SolidColorBrush(Color.Parse("#CC0C0D10")), 1);
    private static readonly IPen PlayheadPen = new Pen(Brushes.White, 2);
    private static readonly IPen GripPen = new Pen(new SolidColorBrush(Color.Parse("#0B0C0E")), 1.5);
    private static readonly IBrush FallbackAccent = new SolidColorBrush(Color.Parse("#FFC21A"));

    private static readonly double[] TickSteps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];

    private enum Drag { None, Scrub, Resize }

    private Drag _drag;
    private int _dragIndex;
    private SegmentEdge _dragEdge;
    private double _dragOriginSource;   // the end's source time when the drag started
    private double _dragOriginPointer;  // the pointer's edit time when the drag started
    // While an end is dragged the edit's length changes, so the scale is held where it was.
    private double? _heldScale;
    private double? _heldOffset;
    private double _offset;  // edit seconds at the left edge

    static EditorTimeline()
    {
        AffectsRender<EditorTimeline>(PositionProperty, DurationProperty, ZoomProperty, PartsProperty, MarkersProperty,
                                      SelectedIndexProperty);
        FocusableProperty.OverrideDefaultValue<EditorTimeline>(false);
    }

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public IReadOnlyList<EditPartViewModel>? Parts { get => GetValue(PartsProperty); set => SetValue(PartsProperty, value); }
    public IReadOnlyList<EditorMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public int SelectedIndex { get => GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }

    private EditorViewModel? Editor => DataContext as EditorViewModel;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width, TrackTop + TrackHeight + 10 + OverviewHeight);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PositionProperty && _drag == Drag.None) FollowPlayhead();
        if (change.Property == ZoomProperty || change.Property == DurationProperty) ClampOffset();
    }

    // Pixels per edit second, and the edit second at the left edge.
    private double Scale => _heldScale ?? (Bounds.Width - Pad * 2) / Math.Max(0.001, VisibleSeconds);
    private double Offset => _heldOffset ?? _offset;
    private double VisibleSeconds => Duration / Math.Max(1, Zoom);

    private double X(double seconds) => Pad + (seconds - Offset) * Scale;
    private double SecondsAt(double x) => Math.Clamp(Offset + (x - Pad) / Scale, 0, Duration);

    private void ClampOffset() => _offset = Math.Clamp(_offset, 0, Math.Max(0, Duration - VisibleSeconds));

    // Zoomed in, the view pages along to keep the playhead in sight.
    private void FollowPlayhead()
    {
        if (Zoom <= 1.001 || Duration <= 0) return;
        var visible = VisibleSeconds;
        if (Position >= _offset && Position <= _offset + visible) return;
        _offset = Position - visible * 0.1;
        ClampOffset();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        if (width <= Pad * 2 || Duration <= 0) return;
        var accent = this.TryFindResource("AccentBrush", out var a) && a is IBrush b ? b : FallbackAccent;
        var accentColor = (accent as ISolidColorBrush)?.Color ?? Color.Parse("#FFC21A");

        // Transparent fill so clicks anywhere on the control land on it.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        using var clip = context.PushClip(new Rect(0, 0, width, Bounds.Height));

        DrawRuler(context, width);

        // The parts, end to end.
        var parts = Parts ?? [];
        var track = new Rect(0, TrackTop, width, TrackHeight);
        foreach (var part in parts)
        {
            var left = X(part.Start);
            var right = X(part.Start + part.Length);
            if (right < 0 || left > width) continue;
            var rect = new Rect(left + 1, track.Y, Math.Max(2, right - left - 2), track.Height);
            var selected = part.Index == SelectedIndex;
            var fill = selected ? new SolidColorBrush(accentColor, 0.18) : PartBrush;
            var outline = selected ? new Pen(accent, 1.5) : PartOutline;
            context.DrawRectangle(fill, outline, rect, 2, 2);

            // Its name and length, when there's room.
            if (rect.Width > 46)
            {
                var name = Text(part.Name, LabelFace, 12, selected ? accent : PartText);
                var length = Text(part.LengthText, RulerFace, 11, RulerText);
                using (context.PushClip(rect.Deflate(new Thickness(8, 0))))
                {
                    context.DrawText(name, new Point(rect.X + 9, rect.Y + 8));
                    if (rect.Width > name.Width + length.Width + 30)
                        context.DrawText(length, new Point(rect.X + 9 + name.Width + 8, rect.Y + 9));
                }
            }
        }

        // The moments, on the parts that kept them.
        foreach (var marker in Markers ?? [])
        {
            var x = X(marker.Seconds);
            if (x < -4 || x > width + 4) continue;
            var rect = new Rect(x - 2, track.Bottom - 22, 4, 16);
            context.DrawRectangle(marker.Brush, MarkerOutline, rect);
        }

        // Handles on the picked part's ends.
        if (SelectedIndex >= 0 && SelectedIndex < parts.Count)
        {
            var part = parts[SelectedIndex];
            foreach (var x in new[] { X(part.Start) + 1, X(part.Start + part.Length) - 1 })
            {
                context.DrawRectangle(accent, null,
                    new Rect(x - HandleWidth / 2, track.Y - 3, HandleWidth, track.Height + 6), 1, 1);
                context.DrawLine(GripPen, new Point(x, track.Center.Y - 7), new Point(x, track.Center.Y + 7));
            }
        }

        // The playhead, from the ruler down through the parts.
        var head = X(Position);
        if (head >= 0 && head <= width)
        {
            context.DrawLine(PlayheadPen, new Point(head, RulerHeight - 6), new Point(head, track.Bottom + 4));
            var cap = new StreamGeometry();
            using (var g = cap.Open())
            {
                g.BeginFigure(new Point(head - 6, RulerHeight - 12), true);
                g.LineTo(new Point(head + 6, RulerHeight - 12));
                g.LineTo(new Point(head + 6, RulerHeight - 6));
                g.LineTo(new Point(head, RulerHeight));
                g.LineTo(new Point(head - 6, RulerHeight - 6));
                g.EndFigure(true);
            }
            context.DrawGeometry(Brushes.White, null, cap);
        }

        // Where the view is in the whole edit, when zoomed in.
        if (Zoom > 1.001)
        {
            var bar = new Rect(Pad, Bounds.Height - OverviewHeight, width - Pad * 2, OverviewHeight);
            context.FillRectangle(OverviewTrack, bar);
            var from = Offset / Duration;
            var span = VisibleSeconds / Duration;
            context.FillRectangle(OverviewThumb, new Rect(bar.X + bar.Width * from, bar.Y, Math.Max(8, bar.Width * span), bar.Height));
        }
    }

    private void DrawRuler(DrawingContext context, double width)
    {
        var scale = Scale;
        var step = TickSteps.FirstOrDefault(s => s * scale >= 64, TickSteps[^1]);
        var minor = step / (step >= 1 && step * scale >= 120 ? 5 : 2);
        var first = Math.Floor(Offset / minor) * minor;
        for (var t = first; t <= Duration + 0.0001; t += minor)
        {
            var x = X(t);
            if (x < -40) continue;
            if (x > width + 40) break;
            var major = Math.Abs(t / step - Math.Round(t / step)) < 0.001;
            context.DrawLine(major ? RulerTick : RulerMinorTick, new Point(x, major ? RulerHeight - 10 : RulerHeight - 5),
                             new Point(x, RulerHeight));
            if (!major) continue;
            var label = Text(TimeLabel(t, step), RulerFace, 10, RulerText);
            context.DrawText(label, new Point(x + 3, 0));
        }
    }

    private static string TimeLabel(double seconds, double step)
    {
        var whole = (int)Math.Floor(seconds + 0.0001);
        var text = $"{whole / 60}:{whole % 60:00}";
        return step < 1 ? text + (seconds - whole).ToString(".0#", CultureInfo.InvariantCulture) : text;
    }

    private static FormattedText Text(string text, Typeface face, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, brush);

    // The end of a part under x, if one is close enough to grab. At a cut the side the pointer is
    // on decides: left of it is the end of the part before, right of it the start of the next.
    private (int Index, SegmentEdge Edge)? EdgeAt(double x, double y)
    {
        if (y < TrackTop - 6 || y > TrackTop + TrackHeight + 6 || Parts is not { } parts) return null;
        (int, SegmentEdge)? best = null;
        var bestDistance = HandleGrab;
        foreach (var part in parts)
        {
            var start = X(part.Start);
            var end = X(part.Start + part.Length);
            var toStart = Math.Abs(x - start);
            var toEnd = Math.Abs(x - end);
            // Only the picked part's ends are offered when a part is too small to tell its ends apart.
            if (end - start < HandleGrab * 2.5 && part.Index != SelectedIndex) continue;
            if (toStart <= bestDistance && x >= start - 2) { best = (part.Index, SegmentEdge.Start); bestDistance = toStart; }
            if (toEnd <= bestDistance && x <= end + 2) { best = (part.Index, SegmentEdge.End); bestDistance = toEnd; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Editor is not { } editor || Duration <= 0) return;
        var p = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;

        if (EdgeAt(p.X, p.Y) is { } edge && !editor.IsExporting)
        {
            var segment = editor.Timeline.Segments[edge.Index];
            _drag = Drag.Resize;
            _dragIndex = edge.Index;
            _dragEdge = edge.Edge;
            _dragOriginSource = edge.Edge == SegmentEdge.Start ? segment.From : segment.To;
            _dragOriginPointer = SecondsAt(p.X);
            _heldScale = Scale;
            _heldOffset = Offset;
            editor.BeginResize(edge.Index);
            return;
        }

        _drag = Drag.Scrub;
        var seconds = SecondsAt(p.X);
        Position = seconds;
        if (p.Y >= TrackTop && editor.Timeline.Locate(seconds).Index is var index and >= 0) editor.SelectedIndex = index;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case Drag.Scrub:
                Position = SecondsAt(p.X);
                break;
            case Drag.Resize when Editor is { } editor:
                // Unclamped, so a drag past the end of the view still reaches the end of the clip.
                var pointer = _heldOffset!.Value + (p.X - Pad) / _heldScale!.Value;
                editor.ResizeTo(_dragIndex, _dragEdge, _dragOriginSource + (pointer - _dragOriginPointer));
                break;
            default:
                Cursor = EdgeAt(p.X, p.Y) is not null ? new Cursor(StandardCursorType.SizeWestEast) : new Cursor(StandardCursorType.Hand);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        if (_drag == Drag.Resize) Editor?.EndResize();
        _drag = Drag.None;
        _heldScale = null;
        _heldOffset = null;
        ClampOffset();
        InvalidateVisual();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag == Drag.Resize) Editor?.EndResize();
        _drag = Drag.None;
        _heldScale = null;
        _heldOffset = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Duration <= 0 || _drag != Drag.None) return;
        var x = e.GetPosition(this).X;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Zoom around the pointer, so what's under it stays there.
            var anchor = SecondsAt(x);
            Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 1, 40);
            _offset = anchor - (x - Pad) / Scale;
            ClampOffset();
        }
        else if (Zoom > 1.001)
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            _offset -= delta * VisibleSeconds * 0.1;
            ClampOffset();
        }
        else return;
        InvalidateVisual();
        e.Handled = true;
    }
}
