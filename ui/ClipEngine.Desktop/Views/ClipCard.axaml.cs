using Avalonia;
using Avalonia.Controls;

namespace ClipEngine.Desktop.Views;

public partial class ClipCard : UserControl
{
    public static readonly StyledProperty<double> ThumbnailHeightProperty =
        AvaloniaProperty.Register<ClipCard, double>(nameof(ThumbnailHeight), 140);

    public ClipCard() => InitializeComponent();

    public double ThumbnailHeight
    {
        get => GetValue(ThumbnailHeightProperty);
        set => SetValue(ThumbnailHeightProperty, value);
    }
}
