namespace ClipEngine.Desktop.Services;

internal sealed record ClipFile(string Path, string Game, DateTime Created, long Bytes, TimeSpan? Duration,
                                ClipMetadata? Metadata = null);

// The saved clips: Videos\Clip_Engine\<Game>\<Game>_<date>.mp4, as written by src/app/App.cpp.
internal sealed class ClipLibrary : IDisposable
{
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    // What was read from each clip, kept while neither it nor its .json changes. Every clip saved
    // while playing sets off a scan, and reading every clip in the library again each time would
    // be a burst of disk reads on the drive the game loads from.
    private readonly Dictionary<string, (DateTime Written, long Bytes, DateTime SidecarWritten, ClipFile File)> _known =
        new(StringComparer.OrdinalIgnoreCase);

    public string Folder { get; private set; } = AppPaths.DefaultClipsDir;

    // Folders outside the clips folder that hold imported clips. Scanned, not watched.
    public IReadOnlyList<string> ExtraFolders { get; set; } = [];

    // Raised on a background thread a moment after files in the folder change.
    public event Action? Changed;

    public void Watch(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) folder = AppPaths.DefaultClipsDir;
        if (_watcher is not null && string.Equals(Folder, folder, StringComparison.OrdinalIgnoreCase)) return;
        Folder = folder;
        lock (_known) _known.Clear();

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

    // Safe to call from several threads at once.
    public IReadOnlyList<ClipFile> Scan()
    {
        var clips = new List<ClipFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ScanFolder(Folder, clips, seen);
        foreach (var extra in ExtraFolders) ScanFolder(extra, clips, seen);
        clips.Sort((a, b) => b.Created.CompareTo(a.Created));
        lock (_known)
        {
            foreach (var path in _known.Keys.Where(p => !seen.Contains(p)).ToList()) _known.Remove(path);
        }
        return clips;
    }

    private void ScanFolder(string folder, List<ClipFile> clips, HashSet<string> seen)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.mp4", SearchOption.AllDirectories))
            {
                if (!seen.Add(path)) continue;
                var info = new FileInfo(path);
                var sidecar = new FileInfo(ClipMetadataFile.SidecarPath(path));
                var sidecarWritten = sidecar.Exists ? sidecar.LastWriteTimeUtc : DateTime.MinValue;
                lock (_known)
                {
                    if (_known.TryGetValue(path, out var known) && known.Written == info.LastWriteTimeUtc &&
                        known.Bytes == info.Length && known.SidecarWritten == sidecarWritten)
                    {
                        clips.Add(known.File);
                        continue;
                    }
                }
                var parent = info.Directory;
                var metadata = ClipMetadataFile.Read(path);
                var game = !string.IsNullOrWhiteSpace(metadata?.Game) ? metadata.Game
                    : parent is not null && !string.Equals(parent.FullName.TrimEnd('\\', '/'), folder.TrimEnd('\\', '/'),
                                                           StringComparison.OrdinalIgnoreCase)
                        ? parent.Name
                        : "Other";
                var clip = new ClipFile(path, game, info.LastWriteTime, info.Length, Mp4Info.ReadDuration(path), metadata);
                clips.Add(clip);
                lock (_known) _known[path] = (info.LastWriteTimeUtc, info.Length, sidecarWritten, clip);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
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
