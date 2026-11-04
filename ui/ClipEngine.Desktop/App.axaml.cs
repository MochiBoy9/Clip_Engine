using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;
using ClipEngine.Desktop.Views;

namespace ClipEngine.Desktop;

public partial class App : Application
{
    private const int SidePanelHotkeyId = 1;

    private readonly CancellationTokenSource _quit = new();
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private SidePanelWindow? _sidePanel;
    private GameNoticeWindow? _notice;
    private IDisposable? _hotkey;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The window hides to the tray instead of closing, so only Quit ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _viewModel = new MainViewModel();
            _window = new MainWindow { DataContext = _viewModel };
            _viewModel.Dialogs = _window;
            _viewModel.ShowMainWindowRequested += ShowWindow;
            _viewModel.SidePanelToggleRequested += ToggleSidePanel;
            _viewModel.GameNoticeRequested += ShowGameNotice;
            _viewModel.Settings.SidePanelHotkeyChanged += RegisterHotkey;
            _viewModel.Updates.RestartRequested += () => _ = RestartAfterUpdateAsync();
            _window.Show();
            RegisterHotkey(_viewModel.DesktopSettings.SidePanelHotkey);
            desktop.Exit += (_, _) =>
            {
                _quit.Cancel();
                _hotkey?.Dispose();
                _viewModel.Dispose();
            };

            // Opening a clip with Clip Engine, or launching it again, lands here.
            InstanceChannel.Listen(message => Dispatcher.UIThread.Post(() => OnOpenRequest(message)), _quit.Token);
            if (desktop.Args is [var file, ..]) OnOpenRequest(file);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnOpenRequest(string message)
    {
        ShowWindow();
        var path = message.Trim().Trim('"');
        if (path.StartsWith("clipengine://", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel?.OpenLink(path);
            return;
        }
        if (path.Length > 0 && File.Exists(path)) _ = _viewModel?.ImportClipAsync(path);
    }

    private void RegisterHotkey(string text)
    {
        if (!OperatingSystem.IsWindows() || _window is null || _viewModel is null) return;
        _hotkey?.Dispose();
        var hotkey = new GlobalHotkey(_window, SidePanelHotkeyId);
        hotkey.Pressed += () => Dispatcher.UIThread.Post(ToggleSidePanel);
        _hotkey = hotkey;
        if (!hotkey.Register(text))
        {
            _viewModel.AddActivity(ActivityKind.Warning,
                $"The side panel hotkey {text} couldn't be set up. Another app may use it; pick another in Settings.");
        }
    }

    private void ToggleSidePanel()
    {
        if (_viewModel is null) return;
        if (_sidePanel is { IsVisible: true })
        {
            _sidePanel.Hide();
            return;
        }
        _sidePanel ??= new SidePanelWindow { DataContext = _viewModel };
        // Dock on the screen the game is likely on: the one under the main window, else the primary.
        var screen = _window is { IsVisible: true } ? _window.Screens.ScreenFromWindow(_window) : null;
        _sidePanel.Dock(_viewModel.DesktopSettings.SidePanelEdge, screen);
        _sidePanel.Show();
        // It sits over the game, so keep it out of the clips the engine records.
        if (OperatingSystem.IsWindows()) Win32.KeepOutOfCaptures(_sidePanel.TryGetPlatformHandle()?.Handle ?? 0);
    }

    // Only while playing: when Clip Engine's own window is in front, its toast says the same thing.
    private void ShowGameNotice(string text)
    {
        if (_window is { IsActive: true }) return;
        _notice ??= new GameNoticeWindow();
        _notice.Show(text);
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _sidePanel?.Hide();
        _window.Show();
        if (_window.WindowState == Avalonia.Controls.WindowState.Minimized)
            _window.WindowState = Avalonia.Controls.WindowState.Normal;
        _window.Activate();
    }

    private void OnTrayClicked(object? sender, EventArgs e) => ShowWindow();
    private void OnTrayOpen(object? sender, EventArgs e) => ShowWindow();
    private void OnTraySidePanel(object? sender, EventArgs e) => ToggleSidePanel();
    private void OnTraySaveClip(object? sender, EventArgs e) => _viewModel?.SaveClipCommand.Execute(null);
    private void OnTrayOpenFolder(object? sender, EventArgs e) => _viewModel?.OpenClipsFolderCommand.Execute(null);

    private void OnTrayQuit(object? sender, EventArgs e) => _ = QuitAsync();

    // The new files are in place: start them, and make way.
    private async Task RestartAfterUpdateAsync()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, UpdateInstaller.AppExe);
            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(Program.AfterUpdateArg);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            _viewModel?.AddActivity(ActivityKind.Error, $"The update is installed, but Clip Engine couldn't restart itself ({e.Message}). Start it again.");
        }
        await QuitAsync();
    }

    internal async Task QuitAsync()
    {
        if (_viewModel is not null) await _viewModel.StopEngineAsync();
        _window?.AllowClose();
        _sidePanel?.Close();
        _notice?.Close();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
