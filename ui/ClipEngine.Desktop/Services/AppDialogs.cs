namespace ClipEngine.Desktop.Services;

// What the view models need from the main window.
internal interface IAppDialogs
{
    nint WindowHandle { get; }
    Task<string?> PickClipAsync();
    // A screenshot or a video file, for Detection Studio to take a frame from.
    Task<string?> PickFrameSourceAsync();
    Task<string?> PickZipAsync();
    Task<string?> PickFolderAsync(string title, string? startFolder);
    // Puts text, like a share link or an invite, on the clipboard. False when that failed.
    Task<bool> CopyTextAsync(string text);
}
