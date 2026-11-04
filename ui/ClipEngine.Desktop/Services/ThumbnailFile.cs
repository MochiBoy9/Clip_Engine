namespace ClipEngine.Desktop.Services;

// A small PNG of a clip's first frame, for the online services to show before it plays.
internal static class ThumbnailFile
{
    private const long MaxBytes = 500 * 1024;

    // The path of a temporary PNG, or null when Windows has no thumbnail for the clip.
    public static async Task<string?> MakeAsync(string clip, int width = 480)
    {
        var bitmap = await Thumbnails.LoadAsync(clip, width);
        if (bitmap is null) return null;
        var path = Path.Combine(Path.GetTempPath(), $"clipengine_{Guid.NewGuid():N}.png");
        try
        {
            using (bitmap) bitmap.Save(path);
            if (new FileInfo(path).Length <= MaxBytes) return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
        Delete(path);
        return null;
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
