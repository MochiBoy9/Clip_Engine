using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class ReplayMemoryTests
{
    private const long Gb = 1L << 30;

    [Fact]
    public void EstimatesLikeTheEngine()
    {
        // 1080p60 picks about 12 Mbps; five audio tracks add 0.96 Mbps.
        Assert.Equal(12_441_600, ReplayMemory.VideoBitrate(1080, 60, 0));
        Assert.Equal(8_000_000, ReplayMemory.VideoBitrate(480, 30, 0));
        Assert.Equal(150_000_000, ReplayMemory.VideoBitrate(2160, 240, 0));
        Assert.Equal(50_000_000, ReplayMemory.VideoBitrate(1080, 60, 50));
        Assert.Equal((12_441_600 + 960_000) / 8 * 60, ReplayMemory.EstimateBytes(60, 1080, 60, 0));
    }

    [Fact]
    public void AShortBufferIsFine()
    {
        var advice = ReplayMemory.For(60, 1080, 60, 0, 8 * Gb, 4);
        Assert.Equal(ReplayAdviceLevel.Fine, advice.Level);
        Assert.Equal("About 95 MB in memory.", advice.Text);
    }

    [Fact]
    public void AnExtendedBufferWarnsALowPowerPc()
    {
        var weak = ReplayMemory.For(1200, 1080, 60, 0, 8 * Gb, 4);
        Assert.Equal(ReplayAdviceLevel.Warning, weak.Level);
        Assert.Contains("1.9 GB", weak.Text);
        Assert.Contains("8.0 GB of memory and 4 cores", weak.Text);

        // The same buffer on a strong PC is only a note, and ten minutes isn't extended.
        Assert.Equal(ReplayAdviceLevel.Note, ReplayMemory.For(1200, 1080, 60, 0, 32 * Gb, 16).Level);
        Assert.Equal(ReplayAdviceLevel.Fine, ReplayMemory.For(600, 1080, 60, 0, 8 * Gb, 4).Level);
    }

    [Fact]
    public void ABufferBiggerThanTheEngineKeepsSaysWhatItKeeps()
    {
        // 30 minutes of 1440p165 at about 60 Mbps is some 13 GB; 16 GB of memory spares 6.4 GB.
        var advice = ReplayMemory.For(1800, 1440, 165, 0, 16 * Gb, 16);
        Assert.Equal(ReplayAdviceLevel.TooBig, advice.Level);
        Assert.Contains("6.4 GB", advice.Text);
        Assert.Contains("about 14 min", advice.Text);
    }

    [Fact]
    public void LowPowerIsLittleMemoryOrFewCores()
    {
        Assert.True(ReplayMemory.IsLowPower(8 * Gb, 16));
        Assert.True(ReplayMemory.IsLowPower(32 * Gb, 4));
        Assert.False(ReplayMemory.IsLowPower(16 * Gb - (200L << 20), 12));
    }

    [Fact]
    public void Sizes()
    {
        Assert.Equal("1 MB", ReplayMemory.Size(1000));
        Assert.Equal("450 MB", ReplayMemory.Size(450L << 20));
        Assert.Equal("2.5 GB", ReplayMemory.Size(5 * Gb / 2));
        Assert.Equal("16 GB", ReplayMemory.Size(16 * Gb));
    }
}

public class VoicePhrasesTests
{
    [Fact]
    public void ReadsPhrasesAndLengths()
    {
        var phrases = VoicePhrases.Parse("Clip that,  save   the last minute 60, clip it 2m, CLIP THAT, keep that 45s, clip this");
        Assert.Equal(
            [new("clip that", 0), new("save the last minute", 60), new("clip it", 120), new("keep that", 45), new("clip this", 0)],
            phrases);
    }

    [Fact]
    public void SaysWhatsWrong()
    {
        Assert.Null(VoicePhrases.Problem("clip that, that's a clip"));
        Assert.Contains("\"clip-that\"", VoicePhrases.Problem("clip that, clip-that"));
        Assert.Contains("\"60\"", VoicePhrases.Problem("60"));
        Assert.Contains("Add a phrase", VoicePhrases.Problem(" , "));
    }

    [Fact]
    public void AdvisesAgainstSingleWords()
    {
        Assert.Null(VoicePhrases.Advice("clip that"));
        Assert.Contains("\"clip\" on its own", VoicePhrases.Advice("clip that, clip 30"));
    }
}
