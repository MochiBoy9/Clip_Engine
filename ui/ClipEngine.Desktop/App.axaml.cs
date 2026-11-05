using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Stats;
using ClipEngine.Desktop.ViewModels;
using ClipEngine.Desktop.Views;

namespace ClipEngine.Desktop;

public partial class App : Application
{
    private const int SidePanelHotkeyId = 1;
    private const int StatsOverlayHotkeyId = 2;

    private readonly CancellationTokenSource _quit = new();
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private SidePanelWindow? _sidePanel;
    private GameNoticeWindow? _notice;
    private IDisposable? _hotkey;
    private IDisposable? _statsHotkey;
    private StatsOverlayWindow? _statsOverlay;
    private StatsSampler? _sampler;
    private bool _toldAboutFrameTiming;

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
            _viewModel.NoticeRequested += ShowNotice;
            _viewModel.Settings.SidePanelHotkeyChanged += RegisterHotkey;
            _viewModel.Settings.StatsOverlayHotkeyChanged += RegisterStatsHotkey;
            _viewModel.StatsOverlayChanged += UpdateStatsOverlay;
            // Over Clip Engine's own window the overlay has nothing to measure.
            _window.Activated += (_, _) => UpdateStatsOverlay();
            _window.Deactivated += (_, _) => UpdateStatsOverlay();
            _viewModel.Updates.RestartRequested += () => _ = RestartAfterUpdateAsync();
            _window.Show();
            RegisterHotkey(_viewModel.DesktopSettings.SidePanelHotkey);
            RegisterStatsHotkey(_viewModel.DesktopSettings.StatsOverlay.Hotkey);
            desktop.Exit += (_, _) =>
            {
                _quit.Cancel();
                _hotkey?.Dispose();
                _statsHotkey?.Dispose();
                _sampler?.Dispose();
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

    private void RegisterStatsHotkey(string text)
    {
        if (!OperatingSystem.IsWindows() || _window is null || _viewModel is null) return;
        _statsHotkey?.Dispose();
        var hotkey = new GlobalHotkey(_window, StatsOverlayHotkeyId);
        hotkey.Pressed += () => Dispatcher.UIThread.Post(() => _viewModel.ToggleStatsOverlay());
        _statsHotkey = hotkey;
        if (!hotkey.Register(text))
        {
            _viewModel.AddActivity(ActivityKind.Warning,
                $"The stats overlay hotkey {text} couldn't be set up. Another app may use it; pick another in Settings.");
        }
    }

    // Brings the stats overlay up while a game is played and takes it down otherwise. The readings stop
    // with it, so it costs nothing while it's away.
    private void UpdateStatsOverlay()
    {
        if (!OperatingSystem.IsWindows() || _viewModel is null) return;
        var wanted = _viewModel.StatsOverlayWanted && _window is not { IsActive: true };
        if (!wanted)
        {
            _sampler?.Stop();
            _statsOverlay?.Hide();
            return;
        }

        var settings = _viewModel.DesktopSettings.StatsOverlay;
        _statsOverlay ??= new StatsOverlayWindow { DataContext = _viewModel.StatsOverlay };
        if (_viewModel.StatsOverlay.Items.Count == 0) _viewModel.StatsOverlay.Show(new GameStats(), settings.Choice);
        if (!_statsOverlay.ShowIn(settings.Corner))
        {
            _sampler?.Stop();
            return;
        }
        if (_sampler is null)
        {
            _sampler = new StatsSampler(stats => Dispatcher.UIThread.Post(() => OnStats(stats)));
            _sampler.FrameTimingChecked += timing => Dispatcher.UIThread.Post(() => OnFrameTimingChecked(timing));
        }
        _sampler.Start();
    }

    private void OnStats(GameStats stats)
    {
        if (_viewModel is null || _sampler is not { Running: true }) return;
        _viewModel.StatsOverlay.Show(stats, _viewModel.DesktopSettings.StatsOverlay.Choice);
    }

    private void OnFrameTimingChecked(FrameTiming timing)
    {
        if (_viewModel is null) return;
        _viewModel.Settings.OnFrameTimingChecked(timing);
        if (timing != FrameTiming.NeedsPermission || _toldAboutFrameTiming) return;
        _toldAboutFrameTiming = true;
        _viewModel.AddActivity(ActivityKind.Warning,
            "The FPS counter needs Windows' permission to read frame timing. Allow it in Settings > Stats overlay.");
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

    // A notification while Clip Engine's window isn't in front: the same notice, in the corner of the screen in use
    // (over the game while one is played). In front, the window's own pop-up shows it.
    private void ShowNotice(string title, string text)
    {
        if (_window is { IsActive: true }) return;
        _notice ??= new GameNoticeWindow();
        _notice.Show(title, text);
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
        _sampler?.Stop();
        _statsOverlay?.Close();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
