using Avalonia;
using Avalonia.Controls;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.Views;

public partial class StatsOverlayWindow : Window
{
    private const int Inset = 16;
    private bool _keptOutOfCaptures;
    private string _corner = "top-left";

    public StatsOverlayWindow()
    {
        InitializeComponent();
        // Its width follows the readings picked; keep it in its corner.
        SizeChanged += (_, _) => Place();
    }

    // Shows the strip in a corner of the screen the game is on. Like the notices, it only shows where
    // Windows can keep it out of recordings, so it never ends up in a clip; returns false otherwise.
    internal bool ShowIn(string corner)
    {
        if (!OperatingSystem.IsWindows()) return false;
        _corner = corner;
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
            var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
            Win32.MakeOverlay(hwnd);
            _keptOutOfCaptures = Win32.KeepOutOfCaptures(hwnd);
        }
        if (!_keptOutOfCaptures)
        {
            Hide();
            return false;
        }
        Place();
        Opacity = 1;
        return true;
    }

    private void Place()
    {
        if (!IsVisible || !OperatingSystem.IsWindows()) return;
        var center = Win32.ForegroundCenter();
        var screen = (center is { } c ? Screens.ScreenFromPoint(new PixelPoint(c.X, c.Y)) : null) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.Bounds;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var inset = (int)(Inset * scale);
        var right = _corner.EndsWith("right", StringComparison.Ordinal);
        var bottom = _corner.StartsWith("bottom", StringComparison.Ordinal);
        Position = new PixelPoint(right ? area.Right - width - inset : area.X + inset,
                                  bottom ? area.Bottom - height - inset : area.Y + inset);
    }
}
