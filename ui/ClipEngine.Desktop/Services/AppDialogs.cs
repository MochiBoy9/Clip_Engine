namespace ClipEngine.Desktop.Services;

// What the view models need from the main window.
internal interface IAppDialogs
{
    nint WindowHandle { get; }
    Task<string?> PickClipAsync();
    // A screenshot or a video file, for Detection Studio to take a frame from.
    Task<string?> PickFrameSourceAsync();
    Task<string?> PickZipAsync();
    // A detection pack (.cepack) to install, and where to save one.
    Task<string?> PickPackAsync();
    Task<string?> SavePackAsync(string suggestedName);
    Task<string?> PickFolderAsync(string title, string? startFolder);
}
