using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

// A game's picture wherever its initials used to be. A square badge shows the program's icon, or failing that the
// middle of its store banner; a wide one (Wide) shows the banner, or the icon large. With neither, the initials on
// the game's colours, as before. The pictures come from GameArt and swap in as they load.
internal sealed class GameBadge : Border
{
    public static readonly StyledProperty<string?> GameProperty =
        AvaloniaProperty.Register<GameBadge, string?>(nameof(Game));

    public static readonly StyledProperty<bool> WideProperty =
        AvaloniaProperty.Register<GameBadge, bool>(nameof(Wide));

    public static readonly StyledProperty<double> InitialsSizeProperty =
        AvaloniaProperty.Register<GameBadge, double>(nameof(InitialsSize), 16);

    private readonly Image _picture = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Image _icon = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly TextBlock _initials = new()
    {
        FontWeight = FontWeight.Black,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Scanlines _lines = new() { IsHitTestVisible = false };

    public GameBadge()
    {
        ClipToBounds = true;
        CornerRadius = new CornerRadius(2);
        if (Application.Current?.TryFindResource("MonoFont", out var font) == true && font is FontFamily mono)
            _initials.FontFamily = mono;
        Child = new Panel { Children = { _picture, _icon, _initials, _lines } };
    }

    public string? Game { get => GetValue(GameProperty); set => SetValue(GameProperty, value); }
    public bool Wide { get => GetValue(WideProperty); set => SetValue(WideProperty, value); }
    public double InitialsSize { get => GetValue(InitialsSizeProperty); set => SetValue(InitialsSizeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GameProperty || change.Property == WideProperty) Update();
        else if (change.Property == BoundsProperty) _icon.Margin = IconMargin(Bounds.Size);
        else if (change.Property == InitialsSizeProperty) _initials.FontSize = InitialsSize;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        GameArt.Changed += OnArtChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        GameArt.Changed -= OnArtChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnArtChanged(string game)
    {
        if (!string.Equals(game, Game, StringComparison.OrdinalIgnoreCase)) return;
        if (Dispatcher.UIThread.CheckAccess()) Update();
        else Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        var name = Game ?? "";
        var art = GameArt.Get(name);
        _initials.Text = Format.Initials(name);
        _initials.FontSize = InitialsSize;

        // Which picture fills the badge: the banner for a wide one, the icon for a square one, whichever there is.
        var banner = art?.Banner;
        var icon = art?.Icon;
        var useBanner = banner is not null && (Wide || icon is null);
        _picture.Source = useBanner ? banner : null;
        _picture.IsVisible = useBanner;
        _icon.Source = useBanner ? null : icon;
        _icon.IsVisible = !useBanner && icon is not null;
        _initials.IsVisible = art is null;
        _lines.IsVisible = useBanner && Wide;
        Background = useBanner ? Brushes.Black : Palettes.Gradient(Palettes.IndexFor(name));
        if (!useBanner && icon is not null) Background = Palettes.Plate(Palettes.IndexFor(name));
    }

    // Icons sit in the middle with room around them: a fifth of a square badge, more in a wide one.
    private Thickness IconMargin(Size size)
    {
        var side = Math.Min(size.Width, size.Height);
        return new Thickness(Wide ? side * 0.22 : side * 0.14);
    }
}

// Faint horizontal lines over a picture, like a monitor's, so store art sits in with the rest of the app.
internal sealed class Scanlines : Control
{
    private static readonly IBrush Line = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x26, 0, 0, 0));

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        for (var y = 0.0; y < h; y += 3) context.FillRectangle(Line, new Rect(0, y, w, 1));
    }
}
