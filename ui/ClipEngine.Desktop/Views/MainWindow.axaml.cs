using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class MainWindow : Window, IAppDialogs
{
    private static readonly FilePickerFileType VideoFiles = new("Video clips") { Patterns = ["*.mp4"] };
    private static readonly FilePickerFileType ZipFiles = new("Clip Engine release") { Patterns = ["*.zip"] };
    private static readonly FilePickerFileType FrameFiles = new("Videos and screenshots")
    {
        Patterns = ["*.mp4", "*.mkv", "*.mov", "*.avi", "*.wmv", "*.webm", "*.png", "*.jpg", "*.jpeg", "*.bmp"],
    };

    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // In front, the window lives; behind the game or out of sight, it holds still and runs light.
        Activated += (_, _) => Classes.Set("awake", true);
        Deactivated += (_, _) => Classes.Set("awake", false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Plan.LookChanged += ApplyLook;
                ApplyLook(vm.Plan.Look);
            }
        };
        // A click beside the notifications list closes it; the pointer over the pop-ups keeps them up while they're read.
        NoticeDismiss.PointerPressed += (_, e) =>
        {
            if (DataContext is MainViewModel vm) vm.Notifications.IsOpen = false;
            e.Handled = true;
        };
        PopUpStack.AddHandler(PointerMovedEvent, (_, _) => (DataContext as MainViewModel)?.Notifications.KeepPopUps(),
                              RoutingStrategies.Tunnel, true);
    }

    // The accent, and with an adaptive theme its gradients, livelier buttons (Theme/Adaptive.axaml, under the window's
    // "adaptive" class) and the backdrop behind the pages.
    internal void ApplyLook(ThemeLook look)
    {
        AdaptiveLook.Apply(look);
        Classes.Set("adaptive", look.Adaptive);
        Backdrop.Show(look);
        Classes.Set("motion", Backdrop.MotionOn);
    }

    nint IAppDialogs.WindowHandle => TryGetPlatformHandle()?.Handle ?? 0;

    async Task<string?> IAppDialogs.PickClipAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a clip",
            FileTypeFilter = [VideoFiles],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    async Task<string?> IAppDialogs.PickFrameSourceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a video or screenshot of the game",
            FileTypeFilter = [FrameFiles],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    async Task<string?> IAppDialogs.PickZipAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install an update from a zip",
            FileTypeFilter = [ZipFiles],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    async Task<bool> IAppDialogs.CopyTextAsync(string text)
    {
        if (Clipboard is not { } clipboard) return false;
        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    async Task<string?> IAppDialogs.PickFolderAsync(string title, string? startFolder)
    {
        IStorageFolder? start = null;
        if (startFolder is not null)
        {
            try
            {
                Directory.CreateDirectory(startFolder);
                start = await StorageProvider.TryGetFolderFromPathAsync(startFolder);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = start,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    // Dropping a clip someone sent onto the window imports it.
    private static string? DroppedClip(DragEventArgs e) =>
#pragma warning disable CS0618 // IDataObject is what Avalonia 11 hands drop handlers
        e.Data.GetFiles()?.Select(f => f.TryGetLocalPath())
         .FirstOrDefault(p => p is not null && p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
#pragma warning restore CS0618

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = DroppedClip(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DroppedClip(e) is { } path && DataContext is MainViewModel vm) _ = vm.ImportClipAsync(path);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (OperatingSystem.IsWindows() && (change.Property == IsVisibleProperty || change.Property == WindowStateProperty))
        {
            Win32.SetBackgroundMode(!IsVisible || WindowState == WindowState.Minimized);
        }
    }

    // Lets the tray's Quit close the window for real.
    internal void AllowClose() => _allowClose = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && DataContext is MainViewModel { DesktopSettings.CloseToTray: true })
        {
            // Keep recording in the background; the tray icon brings the window back. A clip playing
            // in the window stops with it, rather than carry on decoding and playing out of sight.
            e.Cancel = true;
            (DataContext as MainViewModel)?.PausePlayback();
            Hide();
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
}
