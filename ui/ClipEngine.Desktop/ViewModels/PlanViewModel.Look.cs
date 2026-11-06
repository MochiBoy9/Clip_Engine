using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.ViewModels;

// Ultra's colour wheel and adaptive themes on the plan card. The wheel picks any hue and saturation at a brightness,
// and only colours that read on the panels apply: a darker pick is lifted toward white until it does. Adaptive
// themes add gradients, livelier buttons and a still wash behind the pages (Theme/Adaptive.axaml,
// Views/AdaptiveBackdrop.cs).
public sealed partial class PlanViewModel
{
    // The darkest the brightness slider goes: below it every colour lifts to much the same pale shade.
    public const double MinBrightness = 0.4;

    private double _wheelHue;
    private double _wheelSaturation;
    private double _wheelBrightness = 1;
    private bool _adaptive = true;
    private bool _motion = true;
    private bool _typingHex;
    private string? _themeId;
    private DispatcherTimer? _lookTimer;
    private DispatcherTimer? _saveTimer;

    // The look changed: the accent, or adaptive themes or the background motion going on or off.
    internal event Action<ThemeLook>? LookChanged;

    internal ThemeLook Look => new(Theme.Accent, AdaptiveOn, AdaptiveOn && BackgroundMotion);

    // The theme picked: kept in the desktop settings, or here in the preview tool and tests, which have none.
    private string? SavedThemeId
    {
        get => _desktop?.AccentTheme ?? _themeId;
        set
        {
            if (_desktop is not null) _desktop.AccentTheme = value ?? AccentThemes.Default.Id;
            else _themeId = value;
        }
    }

    // The colour on the wheel, as picked, before it's lifted to read on the panels.
    internal Color CustomAccent => AccentThemes.ParseHex(_desktop?.CustomAccent) ?? _customAccent;
    private Color _customAccent = AccentThemes.Default.Accent;

    public bool ShowWheel => CanUseWheel && Theme.Id == AccentThemes.WheelId;

    public double WheelHue
    {
        get => _wheelHue;
        set { if (Set(ref _wheelHue, value)) WheelMoved(); }
    }

    public double WheelSaturation
    {
        get => _wheelSaturation;
        set { if (Set(ref _wheelSaturation, Math.Clamp(value, 0, 1))) WheelMoved(); }
    }

    public double WheelBrightness
    {
        get => _wheelBrightness;
        set { if (Set(ref _wheelBrightness, Math.Clamp(value, MinBrightness, 1))) WheelMoved(); }
    }

    public IBrush WheelPreview => new SolidColorBrush(AccentThemes.Legible(CustomAccent));

    // The colour as #RRGGBB; typing one moves the wheel to it.
    public string WheelHex
    {
        get => AccentThemes.Hex(CustomAccent);
        set
        {
            if (AccentThemes.ParseHex(value) is not { } color || !CanUseWheel) return;
            _typingHex = true;
            try
            {
                SetCustom(color);
                LoadWheel();
            }
            finally
            {
                _typingHex = false;
            }
        }
    }

    public string? WheelNote => AccentThemes.IsLegible(CustomAccent) ? null
        : $"Too dark for the panels as it is, so it shows as {AccentThemes.Hex(AccentThemes.Legible(CustomAccent))}.";
    public bool HasWheelNote => WheelNote is not null;

    public bool AdaptiveOn
    {
        get => CanUseAdaptive && _adaptive;
        set
        {
            if (!CanUseAdaptive || value == _adaptive) return;
            _adaptive = value;
            if (_desktop is not null) _desktop.AdaptiveTheme = value;
            SaveDesktop();
            RaiseLookProperties();
            RaiseLook();
        }
    }

    public bool BackgroundMotion
    {
        get => _motion;
        set
        {
            if (value == _motion) return;
            _motion = value;
            if (_desktop is not null) _desktop.BackgroundMotion = value;
            SaveDesktop();
            Raise();
            RaiseLook();
        }
    }

    public bool ShowAdaptiveUltraTag => !CanUseAdaptive;

    public string AdaptiveCaption => CanUseAdaptive
        ? "Gradients from your colour and its neighbour on the wheel, and buttons with more snap."
        : "Comes with Ultra: gradients from your colour and its neighbour on the wheel, and buttons with more snap.";

    // Sets the wheel from the saved colour.
    private void LoadWheel()
    {
        _adaptive = _desktop?.AdaptiveTheme ?? true;
        _motion = _desktop?.BackgroundMotion ?? true;
        var (h, s, v) = AdaptiveLook.ToHsv(CustomAccent);
        _wheelHue = h;
        _wheelSaturation = s;
        _wheelBrightness = Math.Max(MinBrightness, v);
        Raise(nameof(WheelHue));
        Raise(nameof(WheelSaturation));
        Raise(nameof(WheelBrightness));
    }

    private void WheelMoved() => SetCustom(AdaptiveLook.FromHsv(_wheelHue, _wheelSaturation, _wheelBrightness));

    // A colour from the wheel or typed in: it becomes the theme, and the app follows a moment later, so dragging
    // round the wheel recolours the app a few times a second rather than on every pixel.
    private void SetCustom(Color color)
    {
        if (!CanUseWheel) return;
        _customAccent = color;
        if (_desktop is not null) _desktop.CustomAccent = AccentThemes.Hex(color);
        SavedThemeId = AccentThemes.WheelId;
        UpdateThemes();
        if (!_typingHex) Raise(nameof(WheelHex));
        if (Application.Current is null)
        {
            RaiseLook();
            return;
        }
        _lookTimer ??= Timer(90, () =>
        {
            _lookTimer!.Stop();
            RaiseLook();
        });
        if (!_lookTimer.IsEnabled) _lookTimer.Start();
        _saveTimer ??= Timer(600, () =>
        {
            _saveTimer!.Stop();
            SaveDesktop();
        });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static DispatcherTimer Timer(int ms, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private void RaiseLook() => LookChanged?.Invoke(Look);

    private void RaiseLookProperties()
    {
        Raise(nameof(CanUseWheel));
        Raise(nameof(CanUseAdaptive));
        Raise(nameof(ShowWheel));
        Raise(nameof(WheelPreview));
        Raise(nameof(WheelNote));
        Raise(nameof(HasWheelNote));
        Raise(nameof(AdaptiveOn));
        Raise(nameof(BackgroundMotion));
        Raise(nameof(ShowAdaptiveUltraTag));
        Raise(nameof(AdaptiveCaption));
    }

    private void SaveDesktop()
    {
        if (_desktop is null) return;
        try
        {
            _desktop.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
