using Avalonia;
using Avalonia.Controls;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        Activated += (_, _) => UpdateAnimation();
        Deactivated += (_, _) => UpdateAnimation();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm) vm.Settings.AnimatedBackgroundChanged += _ => UpdateAnimation();
            UpdateAnimation();
        };
    }

    // Lets the tray's Quit close the window for real.
    internal void AllowClose() => _allowClose = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
        {
            // With acrylic behind the window, let some of the desktop show through the base gradient.
            BaseLayer.Opacity = ActualTransparencyLevel == WindowTransparencyLevel.None ? 1 : 0.84;
        }
        else if (change.Property == WindowStateProperty)
        {
            UpdateAnimation();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && DataContext is MainViewModel { DesktopSettings.CloseToTray: true })
        {
            // Keep recording in the background; the tray icon brings the window back.
            e.Cancel = true;
            Hide();
            UpdateAnimation();
            return;
        }
        if (!_allowClose && Application.Current is App app)
        {
            e.Cancel = true;
            _ = app.QuitAsync();
            return;
        }
        base.OnClosing(e);
    }

    // The background only moves while you're looking at it, so it costs nothing while you play.
    private void UpdateAnimation()
    {
        var enabled = DataContext is MainViewModel vm && vm.Settings.AnimatedBackground;
        Root.Classes.Set("animated", enabled && IsActive && IsVisible && WindowState != WindowState.Minimized);
    }

    internal void ForceAnimated(bool on) => Root.Classes.Set("animated", on);
}
