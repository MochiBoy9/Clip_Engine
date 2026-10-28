using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.ViewModels;

namespace ClipEngine.Desktop.Views;

public partial class MainWindow : Window, IAppDialogs
{
    private static readonly FilePickerFileType VideoFiles = new("Video clips") { Patterns = ["*.mp4"] };
    private static readonly FilePickerFileType ImageFiles = new("Screenshots") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"] };

    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        UpdateBaseLayer();
        Activated += (_, _) => UpdateAnimation();
        Deactivated += (_, _) => UpdateAnimation();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Settings.AnimatedBackgroundChanged += _ => UpdateAnimation();
                vm.Supporter.ThemeChanged += ApplyTheme;
                ApplyTheme(vm.Supporter.Theme);
            }
            UpdateAnimation();
        };
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

    async Task<string?> IAppDialogs.PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a screenshot of the game",
            FileTypeFilter = [ImageFiles],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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

    // Lets the tray's Quit close the window for real.
    internal void AllowClose() => _allowClose = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Windows reports the transparency level while InitializeComponent is still loading the XAML,
        // before the named controls exist, so the handlers below have to cope with them being null.
        if (change.Property == ActualTransparencyLevelProperty)
        {
            UpdateBaseLayer();
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
    private void UpdateBaseLayer()
    {
        // With acrylic behind the window, let some of the desktop show through the base gradient.
        if (BaseLayer is null) return;
        BaseLayer.Opacity = ActualTransparencyLevel == WindowTransparencyLevel.None ? 1 : 0.84;
    }

    private void UpdateAnimation()
    {
        if (Root is null) return;
        var enabled = DataContext is MainViewModel vm && vm.Settings.AnimatedBackground;
        Root.Classes.Set("animated", enabled && IsActive && IsVisible && WindowState != WindowState.Minimized);
    }

    internal void ForceAnimated(bool on) => Root.Classes.Set("animated", on);

    private void ApplyTheme(AccentTheme theme)
    {
        AccentThemes.ApplyAccent(theme);
        AccentThemes.ApplyBackground(theme, BlobViolet, BlobCyan, BlobPink, BlobBlue);
    }
}
