using Avalonia.Controls;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class GamesView : UserControl
{
    public GamesView()
    {
        InitializeComponent();
        NameCard.DataContext = new PlayerNamesViewModel(AppPaths.EngineSettingsFile);
    }
}
