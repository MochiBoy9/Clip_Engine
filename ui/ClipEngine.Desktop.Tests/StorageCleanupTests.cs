using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class StorageCleanupTests
{
    private const long Gb = 1L << 30;
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0);

    private static ClipFile Clip(string name, double daysOld, long bytes, bool favorite = false, string folder = @"C:\Clips\Game") =>
        new($@"{folder}\{name}.mp4", "Game", Now.AddDays(-daysOld), bytes, null,
            favorite ? new ClipMetadata { Favorite = true } : null);

    // Windows paths, so split by hand; the tests also run on Linux.
    private static string Name(ClipFile c) => c.Path.Split('\\')[^1][..^4];

    [Fact]
    public void DoesNothingWhenOff()
    {
        Assert.Empty(StorageCleanup.Plan([Clip("a", 400, Gb)], @"C:\Clips", [], Now, 0, 0));
    }

    [Fact]
    public void RemovesOldClipsButKeepsFavorites()
    {
        var clips = new[] { Clip("old", 40, Gb), Clip("fav", 50, Gb, favorite: true), Clip("new", 2, Gb) };
        var plan = StorageCleanup.Plan(clips, @"C:\Clips", [], Now, 30, 0);
        Assert.Equal(["old"], plan.Select(Name));
    }

    [Fact]
    public void RemovesOldestUntilUnderTheLimit()
    {
        var clips = new[]
        {
            Clip("a", 5, 2 * Gb), Clip("b", 4, 2 * Gb, favorite: true), Clip("c", 3, 2 * Gb), Clip("d", 2, 2 * Gb),
            Clip("e", 0.01, 2 * Gb),  // saved minutes ago: kept
        };
        // 10 GB in total, 5 GB allowed: a and c go (b is a favorite), leaving 6 GB; d goes too.
        var plan = StorageCleanup.Plan(clips, @"C:\Clips", [], Now, 0, 5);
        Assert.Equal(["a", "c", "d"], plan.Select(Name));
    }

    [Fact]
    public void LeavesImportedAndOutsideClipsAlone()
    {
        var clips = new[]
        {
            Clip("mine", 90, Gb),
            Clip("friend", 90, Gb, folder: @"C:\Clips\Shared with me"),
            Clip("elsewhere", 90, Gb, folder: @"D:\Other"),
            Clip("lookalike", 90, Gb, folder: @"C:\Clips2\Game"),
        };
        var plan = StorageCleanup.Plan(clips, @"C:\Clips\", [@"C:\Clips\Shared with me"], Now, 30, 0);
        Assert.Equal(["mine"], plan.Select(Name));
    }
}
