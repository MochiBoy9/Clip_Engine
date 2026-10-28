using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// Detection Studio's frame: the picture, with a box for every rule in its event's colour. Click a
// box to pick its rule, drag it to move it, drag a corner to resize it, or drag anywhere else to
// draw a new box for the picked rule.
internal sealed class StudioCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> FrameProperty =
        AvaloniaProperty.Register<StudioCanvas, Bitmap?>(nameof(Frame));

    public static readonly StyledProperty<IReadOnlyList<StudioRuleViewModel>?> RulesProperty =
        AvaloniaProperty.Register<StudioCanvas, IReadOnlyList<StudioRuleViewModel>?>(nameof(Rules));

    public static readonly StyledProperty<StudioRuleViewModel?> SelectedRuleProperty =
        AvaloniaProperty.Register<StudioCanvas, StudioRuleViewModel?>(nameof(SelectedRule),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> RegionsVersionProperty =
        AvaloniaProperty.Register<StudioCanvas, int>(nameof(RegionsVersion));

    private const double CornerGrab = 9;
    private const double CornerSize = 8;
    private const double MinBox = 0.01;

    private static readonly IBrush EmptyBrush = new SolidColorBrush(Color.Parse("#0B0918"));
    private static readonly IBrush ShadeBrush = new SolidColorBrush(Color.Parse("#66000000"));
    private static readonly IBrush LabelText = Brushes.White;
    private static readonly Typeface LabelFace = new("Inter", FontStyle.Normal, FontWeight.SemiBold);

    private enum Drag { None, Draw, Move, Resize }

    private Drag _drag;
    private Point _anchor;  // in frame fractions
    private (double Left, double Top, double Width, double Height) _startRegion;
    private int _corner;    // 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left

    static StudioCanvas()
    {
        AffectsRender<StudioCanvas>(FrameProperty, RulesProperty, SelectedRuleProperty, RegionsVersionProperty);
        AffectsMeasure<StudioCanvas>(FrameProperty);
        CursorProperty.OverrideDefaultValue<StudioCanvas>(new Cursor(StandardCursorType.Cross));
    }

    public Bitmap? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public IReadOnlyList<StudioRuleViewModel>? Rules { get => GetValue(RulesProperty); set => SetValue(RulesProperty, value); }
    public StudioRuleViewModel? SelectedRule { get => GetValue(SelectedRuleProperty); set => SetValue(SelectedRuleProperty, value); }
    public int RegionsVersion { get => GetValue(RegionsVersionProperty); set => SetValue(RegionsVersionProperty, value); }

    // Where the picture sits in the control, keeping its shape. Without a frame, a 16:9 screen.
    private Rect Picture
    {
        get
        {
            var size = Frame?.Size ?? new Size(16, 9);
            if (size.Width <= 0 || size.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return default;
            var scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
            var w = size.Width * scale;
            var h = size.Height * scale;
            return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
        }
    }

    public override void Render(DrawingContext context)
    {
        var picture = Picture;
        if (picture.Width <= 0) return;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Frame is { } frame) context.DrawImage(frame, new Rect(frame.Size), picture);
        else context.FillRectangle(EmptyBrush, picture);

        if (Rules is not { } rules) return;
        // Dim the picture outside the picked box, so what the rule reads stands out.
        if (SelectedRule is { } picked && Frame is not null)
        {
            var box = BoxRect(picked, picture);
            using (context.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                       new RectangleGeometry(picture), new RectangleGeometry(box))))
            {
                context.FillRectangle(ShadeBrush, picture);
            }
        }

        foreach (var rule in rules.OrderBy(r => r.IsSelected))
        {
            var box = BoxRect(rule, picture);
            var selected = ReferenceEquals(rule, SelectedRule);
            var pen = new Pen(rule.Brush, selected ? 2.5 : 1.5, selected ? null : new DashStyle([4, 3], 0));
            context.DrawRectangle(selected ? null : new SolidColorBrush(Colors.Black, 0.12), pen, box, 3, 3);

            var label = new FormattedText(rule.Label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                          LabelFace, 11, LabelText);
            var tag = TagRect(box, new Size(label.Width + 12, label.Height + 4), picture,
                              rules.Where(r => !ReferenceEquals(r, rule)).Select(r => BoxRect(r, picture)).ToList());
            context.DrawRectangle(rule.Brush, null, tag, 4, 4);
            context.DrawText(label, new Point(tag.X + 6, tag.Y + 2));

            if (!selected) continue;
            foreach (var corner in Corners(box))
            {
                context.DrawRectangle(Brushes.White, new Pen(rule.Brush, 2),
                                      new Rect(corner.X - CornerSize / 2, corner.Y - CornerSize / 2, CornerSize, CornerSize), 2, 2);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Rules is not { } rules) return;
        var picture = Picture;
        var point = e.GetPosition(this);
        _anchor = ToFraction(point, picture);

        if (SelectedRule is { } picked && CornerAt(BoxRect(picked, picture), point) is { } corner)
        {
            _drag = Drag.Resize;
            _corner = corner;
        }
        else if (rules.Where(r => BoxRect(r, picture).Contains(point)).OrderByDescending(r => r.IsSelected).FirstOrDefault()
                 is { } hit)
        {
            SelectedRule = hit;
            _drag = Drag.Move;
        }
        else if (SelectedRule is not null && picture.Contains(point))
        {
            _drag = Drag.Draw;
        }
        else
        {
            return;
        }
        _startRegion = SelectedRule!.Region;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var picture = Picture;
        if (_drag == Drag.None)
        {
            Cursor = SelectedRule is { } picked && CornerAt(BoxRect(picked, picture), point) is { } c
                ? new Cursor(c is 0 or 2 ? StandardCursorType.TopLeftCorner : StandardCursorType.TopRightCorner)
                : Rules?.Any(r => BoxRect(r, picture).Contains(point)) == true
                    ? new Cursor(StandardCursorType.SizeAll)
                    : null;
            return;
        }
        if (SelectedRule is not { } rule) return;
        var at = ToFraction(point, picture);
        var (left, top, width, height) = _startRegion;
        switch (_drag)
        {
            case Drag.Draw:
            {
                var x1 = Math.Min(_anchor.X, at.X);
                var y1 = Math.Min(_anchor.Y, at.Y);
                var x2 = Math.Max(_anchor.X, at.X);
                var y2 = Math.Max(_anchor.Y, at.Y);
                if (x2 - x1 < MinBox && y2 - y1 < MinBox) return;
                rule.SetRegion(x1, y1, Math.Max(MinBox, x2 - x1), Math.Max(MinBox, y2 - y1));
                break;
            }
            case Drag.Move:
                rule.SetRegion(left + at.X - _anchor.X, top + at.Y - _anchor.Y, width, height);
                break;
            case Drag.Resize:
            {
                var x1 = _corner is 0 or 3 ? at.X : left;
                var x2 = _corner is 1 or 2 ? at.X : left + width;
                var y1 = _corner is 0 or 1 ? at.Y : top;
                var y2 = _corner is 2 or 3 ? at.Y : top + height;
                rule.SetRegion(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(MinBox, Math.Abs(x2 - x1)),
                               Math.Max(MinBox, Math.Abs(y2 - y1)));
                break;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    private static Point ToFraction(Point point, Rect picture) =>
        picture.Width <= 0 ? default
            : new Point(Math.Clamp((point.X - picture.X) / picture.Width, 0, 1), Math.Clamp((point.Y - picture.Y) / picture.Height, 0, 1));

    private static Rect BoxRect(StudioRuleViewModel rule, Rect picture)
    {
        var (left, top, width, height) = rule.Region;
        return new Rect(picture.X + left * picture.Width, picture.Y + top * picture.Height, width * picture.Width,
                        height * picture.Height);
    }

    // A rule's name goes just above its box, or below, beside or inside it when that would cover another box.
    private static Rect TagRect(Rect box, Size size, Rect picture, IReadOnlyList<Rect> others)
    {
        Rect[] spots =
        [
            new(new Point(box.X, box.Y - size.Height - 2), size),
            new(new Point(box.X, box.Bottom + 2), size),
            new(new Point(box.X - size.Width - 2, box.Y), size),
            new(new Point(box.X + 2, box.Y + 2), size),
        ];
        return spots.FirstOrDefault(s => picture.Contains(s) && !others.Any(o => o.Intersects(s)), spots[^1]);
    }

    private static Point[] Corners(Rect box) => [box.TopLeft, box.TopRight, box.BottomRight, box.BottomLeft];

    private static int? CornerAt(Rect box, Point point)
    {
        var corners = Corners(box);
        for (var i = 0; i < corners.Length; i++)
        {
            if (Math.Abs(corners[i].X - point.X) <= CornerGrab && Math.Abs(corners[i].Y - point.Y) <= CornerGrab) return i;
        }
        return null;
    }
}
