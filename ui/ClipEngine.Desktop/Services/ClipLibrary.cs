namespace ClipEngine.Desktop.Services;

internal sealed record ClipFile(string Path, string Game, DateTime Created, long Bytes, TimeSpan? Duration);

// The saved clips: Videos\Clip_Engine\<Game>\<Game>_<date>.mp4, as written by src/app/App.cpp.
internal sealed class ClipLibrary : IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public string Folder { get; private set; } = AppPaths.DefaultClipsDir;

    // Raised on a background thread a moment after files in the folder change.
    public event Action? Changed;

    public void Watch(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) folder = AppPaths.DefaultClipsDir;
        if (_watcher is not null && string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
        Folder = folder;

        _watcher?.Dispose();
        _watcher = null;
        try
        {
            Directory.CreateDirectory(folder);
            _watcher = new FileSystemWatcher(folder, "*.mp4")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            _watcher.Created += (_, _) => Debounce();
            _watcher.Deleted += (_, _) => Debounce();
            _watcher.Renamed += (_, _) => Debounce();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<ClipFile> Scan()
    {
        if (!Directory.Exists(Folder)) return [];
        var clips = new List<ClipFile>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(Folder, "*.mp4", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                var parent = info.Directory;
                var game = parent is not null && !string.Equals(parent.FullName.TrimEnd('\\', '/'),
                                                               Folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                    ? parent.Name
                    : "Other";
                clips.Add(new ClipFile(path, game, info.LastWriteTime, info.Length, Mp4Info.ReadDuration(path)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        clips.Sort((a, b) => b.Created.CompareTo(a.Created));
        return clips;
    }

    private void Debounce()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Changed?.Invoke(), null, 400, Timeout.Infinite);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
