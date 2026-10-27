using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.Views;

public partial class GameNoticeWindow : Window
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(2.5);
    private const int Inset = 24;

    private readonly DispatcherTimer _hide;
    private bool _keptOutOfCaptures;

    public GameNoticeWindow()
    {
        InitializeComponent();
        _hide = new DispatcherTimer { Interval = ShowFor };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            Hide();
        };
    }

    // Shows text in the top-right corner of the screen the game is on. Does nothing where Windows
    // can't keep the notice out of recordings, so it never ends up in a clip.
    internal void Show(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        Message.Text = text;
        if (!IsVisible)
        {
            Opacity = 0;
            base.Show();
            var hwnd = TryGetPlatformHandle()?.Handle ?? 0;
            Win32.MakeOverlay(hwnd);
            _keptOutOfCaptures = Win32.KeepOutOfCaptures(hwnd);
        }
        if (!_keptOutOfCaptures)
        {
            Hide();
            return;
        }

        var center = Win32.ForegroundCenter();
        var screen = (center is { } c ? Screens.ScreenFromPoint(new PixelPoint(c.X, c.Y)) : null) ?? Screens.Primary;
        if (screen is not null)
        {
            var area = screen.Bounds;
            var width = (int)Math.Ceiling(Width * screen.Scaling);
            Position = new PixelPoint(area.Right - width - (int)(Inset * screen.Scaling), area.Y + (int)(Inset * screen.Scaling));
        }
        Opacity = 1;
        _hide.Stop();
        _hide.Start();
    }
}
