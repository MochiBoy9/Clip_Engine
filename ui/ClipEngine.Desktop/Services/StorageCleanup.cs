namespace ClipEngine.Desktop.Services;

// Keeps the clips folder from filling the disk: removes clips past an age, then the oldest
// clips until the folder is under a size limit. Favorites, imported clips and anything saved
// in the last hour are always kept.
internal static class StorageCleanup
{
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    // maxAgeDays and maxGigabytes of 0 turn that rule off.
    public static IReadOnlyList<ClipFile> Plan(IEnumerable<ClipFile> clips, string clipsFolder, IEnumerable<string> keepFolders,
                                               DateTime now, int maxAgeDays, int maxGigabytes)
    {
        if (maxAgeDays <= 0 && maxGigabytes <= 0) return [];
        var keep = keepFolders.ToList();
        var own = clips.Where(c => IsInside(c.Path, clipsFolder) && !keep.Any(k => IsInside(c.Path, k))).ToList();

        var removable = own.Where(c => c.Metadata?.Favorite != true && now - c.Created > Grace)
                           .OrderBy(c => c.Created)
                           .ToList();
        var remove = new List<ClipFile>();
        if (maxAgeDays > 0) remove.AddRange(removable.Where(c => now - c.Created > TimeSpan.FromDays(maxAgeDays)));

        if (maxGigabytes > 0)
        {
            var limit = maxGigabytes * (1L << 30);
            var total = own.Sum(c => c.Bytes) - remove.Sum(c => c.Bytes);
            foreach (var clip in removable)
            {
                if (total <= limit) break;
                if (remove.Contains(clip)) continue;
                remove.Add(clip);
                total -= clip.Bytes;
            }
        }
        return remove;
    }

    // Deletes the clips and their .json files for good. Returns how many and how much went.
    public static (int Count, long Bytes) Delete(IEnumerable<ClipFile> clips)
    {
        var count = 0;
        long bytes = 0;
        foreach (var clip in clips)
        {
            try
            {
                File.Delete(clip.Path);
                var sidecar = ClipMetadataFile.SidecarPath(clip.Path);
                if (File.Exists(sidecar)) File.Delete(sidecar);
                count++;
                bytes += clip.Bytes;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // In use (playing, uploading); it goes on a later run.
            }
        }
        return (count, bytes);
    }

    private static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var root = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        root = root.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
