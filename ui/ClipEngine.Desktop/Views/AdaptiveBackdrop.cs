using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.Views;

// Behind the pages with an adaptive theme on: a still wash of the accent and its partner colour over the dotted grid,
// and now and then a scan, a hairline with a short lit trail that runs down the page like a scanner reading a
// printed sheet. The scan is one picture drawn once and slid by its render transform, so the window only redraws
// for the second and a half it runs, about once a minute, and never while the window is behind a game.
internal sealed class AdaptiveBackdrop : Panel
{
    // How long a scan takes to cross the page, and the quiet between scans.
    public static readonly TimeSpan SweepTime = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan MinQuiet = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan MaxQuiet = TimeSpan.FromSeconds(80);

    private readonly ScanBand _band = new();
    private readonly TranslateTransform _bandMove = new();
    private readonly DispatcherTimer _quiet = new(DispatcherPriority.Background);
    private readonly Random _random = new();
    private CancellationTokenSource? _sweep;
    private ThemeLook _look = new(AccentThemes.Default.Accent);
    private bool _awake;

    public AdaptiveBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        IsVisible = false;
        var wash = new Border();
        wash[!Border.BackgroundProperty] = wash.GetResourceObservable("AdaptiveWashBrush").ToBinding();
        var partner = new Border();
        partner[!Border.BackgroundProperty] = partner.GetResourceObservable("AdaptiveWashPartnerBrush").ToBinding();
        _band.RenderTransform = _bandMove;
        _band.IsVisible = false;
        Children.Add(wash);
        Children.Add(partner);
        Children.Add(_band);
        _quiet.Tick += (_, _) =>
        {
            _quiet.Stop();
            Sweep();
        };
    }

    // Whether the window is in front. Behind a game nothing moves.
    public bool Awake
    {
        get => _awake;
        set
        {
            if (_awake == value) return;
            _awake = value;
            Schedule();
            if (!value) StopSweep();
        }
    }

    internal bool Moves => _look.Adaptive && _look.Motion && _awake && IsEffectivelyVisible;

    public void Show(ThemeLook look)
    {
        var wasAdaptive = _look.Adaptive;
        var accentChanged = look.Accent != _look.Accent;
        _look = look;
        IsVisible = look.Adaptive;
        if (!look.Adaptive) StopSweep();
        Schedule();
        // Turning adaptive themes on or picking a new colour runs a scan straight away, so the change is seen.
        if (look.Adaptive && look.Motion && (!wasAdaptive || accentChanged)) Sweep();
    }

    private void Schedule()
    {
        if (!_look.Adaptive || !_look.Motion || !_awake)
        {
            _quiet.Stop();
            return;
        }
        if (_quiet.IsEnabled) return;
        var span = MaxQuiet - MinQuiet;
        _quiet.Interval = MinQuiet + TimeSpan.FromMilliseconds(_random.NextDouble() * span.TotalMilliseconds);
        _quiet.Start();
    }

    // One pass down the page.
    public async void Sweep()
    {
        if (!Moves || Bounds.Height < 1 || _sweep is not null) return;
        _sweep = new CancellationTokenSource();
        var token = _sweep.Token;
        _band.IsVisible = true;
        var animation = new Animation
        {
            Duration = SweepTime,
            Easing = new SineEaseInOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, -ScanBand.Trail) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, Bounds.Height + 2) } },
            },
        };
        try
        {
            await animation.RunAsync(_band, token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _band.IsVisible = false;
            _sweep.Dispose();
            _sweep = null;
            Schedule();
        }
    }

    private void StopSweep() => _sweep?.Cancel();

    // The preview tool: the scan held still part of the way down the page.
    internal void ShowScan(double fraction)
    {
        _band.IsVisible = true;
        _bandMove.Y = fraction * Bounds.Height - ScanBand.Trail;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _quiet.Stop();
        StopSweep();
    }

    // The scan: a bright hairline with ticks every 40 px, like a ruler, and a short trail of the accent above it.
    private sealed class ScanBand : Control
    {
        public const double Trail = 120;

        public ScanBand()
        {
            Height = Trail + 2;
            VerticalAlignment = VerticalAlignment.Top;
            this[!AccentProperty] = this.GetResourceObservable("AccentColor").ToBinding();
        }

        private static readonly StyledProperty<Color> AccentProperty =
            AvaloniaProperty.Register<ScanBand, Color>("Accent");

        static ScanBand()
        {
            AffectsRender<ScanBand>(AccentProperty);
        }

        public override void Render(DrawingContext context)
        {
            var accent = GetValue(AccentProperty);
            var w = Bounds.Width;
            var trail = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0),
                    new GradientStop(Color.FromArgb(0x10, accent.R, accent.G, accent.B), 0.8),
                    new GradientStop(Color.FromArgb(0x26, accent.R, accent.G, accent.B), 1),
                },
            };
            context.FillRectangle(trail, new Rect(0, 0, w, Trail));
            var line = new SolidColorBrush(Color.FromArgb(0xB0, accent.R, accent.G, accent.B));
            context.FillRectangle(line, new Rect(0, Trail, w, 1));
            for (var x = 0.0; x < w; x += 40) context.FillRectangle(line, new Rect(x, Trail - 5, 1, 5));
        }
    }
}
