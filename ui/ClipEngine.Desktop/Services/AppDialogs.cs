namespace ClipEngine.Desktop.Services;

// What the view models need from the main window.
internal interface IAppDialogs
{
    nint WindowHandle { get; }
    Task<string?> PickClipAsync();
    Task<string?> PickFolderAsync(string title, string? startFolder);
}
