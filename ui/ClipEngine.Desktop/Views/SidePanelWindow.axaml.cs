using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace ClipEngine.Desktop.Views;

public partial class SidePanelWindow : Window
{
    private const double PanelWidth = 344;

    public SidePanelWindow() => InitializeComponent();

    // Docks the panel to the left or right edge of the screen, full height of the work area.
    internal void Dock(string edge, Screen? screen)
    {
        screen ??= Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Width = PanelWidth;
        Height = area.Height / scale;
        var x = edge == "left" ? area.X : area.Right - (int)Math.Ceiling(PanelWidth * scale);
        Position = new PixelPoint(x, area.Y);
    }
}
