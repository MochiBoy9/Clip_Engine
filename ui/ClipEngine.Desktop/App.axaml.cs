using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClipEngine.Desktop.ViewModels;
using ClipEngine.Desktop.Views;

namespace ClipEngine.Desktop;

public partial class App : Application
{
    private MainViewModel? _viewModel;
    private MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window hides to the tray instead of closing, so only Quit ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _viewModel = new MainViewModel();
            _window = new MainWindow { DataContext = _viewModel };
            _window.Show();
            desktop.Exit += (_, _) => _viewModel.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == Avalonia.Controls.WindowState.Minimized)
            _window.WindowState = Avalonia.Controls.WindowState.Normal;
        _window.Activate();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();
    private void OnTrayOpen(object? sender, EventArgs e) => ShowWindow();
    private void OnTraySaveClip(object? sender, EventArgs e) => _viewModel?.SaveClipCommand.Execute(null);
    private void OnTrayOpenFolder(object? sender, EventArgs e) => _viewModel?.OpenClipsFolderCommand.Execute(null);

    private void OnTrayQuit(object? sender, EventArgs e) => _ = QuitAsync();

    internal async Task QuitAsync()
    {
        if (_viewModel is not null) await _viewModel.StopEngineAsync();
        _window?.AllowClose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
