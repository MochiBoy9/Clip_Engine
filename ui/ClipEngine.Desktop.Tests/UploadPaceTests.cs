using System.Diagnostics;
using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class UploadPaceTests
{
    private static async Task<TimeSpan> ReadAll(bool slow)
    {
        var data = new byte[500_000];
        Random.Shared.NextBytes(data);
        using var paced = new PacedStream(new MemoryStream(data), () => slow, bytesPerSecond: 1_000_000);
        using var copy = new MemoryStream();
        var clock = Stopwatch.StartNew();
        await paced.CopyToAsync(copy);
        Assert.Equal(data, copy.ToArray());
        return clock.Elapsed;
    }

    [Fact]
    public async Task InGameUploadsKeepToThePace()
    {
        var took = await ReadAll(slow: true);
        Assert.True(took >= TimeSpan.FromSeconds(0.35), $"500 KB at 1 MB/s took {took}");
    }

    [Fact]
    public async Task OtherwiseUploadsGoAtFullSpeed()
    {
        var took = await ReadAll(slow: false);
        Assert.True(took < TimeSpan.FromSeconds(0.2), $"500 KB unpaced took {took}");
    }
}
