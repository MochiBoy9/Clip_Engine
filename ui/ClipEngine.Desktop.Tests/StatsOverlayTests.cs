using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ClipEngine.Desktop.Services;
using ClipEngine.Desktop.Services.Stats;
using ClipEngine.Desktop.ViewModels;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class FrameTimesTests
{
    private const long Second = FrameTimes.TicksPerSecond;

    private static FrameTimes Steady(double fps, double seconds, long start = 1_000_000)
    {
        var frames = new FrameTimes();
        var count = (int)(fps * seconds);
        for (var i = 0; i <= count; i++) frames.Add(start + (long)(i * Second / fps));
        return frames;
    }

    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    [InlineData(500)]
    public void SteadyFrameRate(double fps)
    {
        var measured = Steady(fps, 5).Measure();
        Assert.NotNull(measured);
        Assert.InRange(measured.Value.Fps, fps * 0.99, fps * 1.01);
        // Even pacing: the slowest frames are as quick as the rest.
        Assert.InRange(measured.Value.OnePercentLow, fps * 0.98, fps * 1.01);
    }

    [Fact]
    public void StuttersPullTheOnePercentLowDown()
    {
        var frames = new FrameTimes();
        var t = 0L;
        // 10 seconds at 144 fps with a 50 ms hitch every second.
        for (var i = 0; i < 1440; i++)
        {
            t += i % 144 == 0 ? Second / 20 : Second / 144;
            frames.Add(t);
        }
        var measured = frames.Measure()!.Value;
        Assert.InRange(measured.Fps, 120, 145);
        Assert.InRange(measured.OnePercentLow, 15, 35);
    }

    [Fact]
    public void OnlyTheLastSecondCountsForFps()
    {
        var frames = new FrameTimes();
        var t = 0L;
        for (var i = 0; i < 300; i++) frames.Add(t += Second / 30);   // 10 s at 30
        for (var i = 0; i < 240; i++) frames.Add(t += Second / 240);  // then 1 s at 240
        Assert.InRange(frames.Measure()!.Value.Fps, 235, 245);
    }

    [Fact]
    public void BelowOneFrameASecond()
    {
        var frames = new FrameTimes();
        frames.Add(0);
        frames.Add(2 * Second);
        Assert.Equal(0.5, frames.Measure()!.Value.Fps, 3);
    }

    [Fact]
    public void NothingUntilTwoFrames()
    {
        var frames = new FrameTimes();
        Assert.Null(frames.Measure());
        frames.Add(5);
        Assert.Null(frames.Measure());
        frames.Add(5);  // a repeated stamp says nothing
        Assert.Null(frames.Measure());
        frames.Clear();
        Assert.Null(frames.Measure());
    }

    [Fact]
    public void KeepsOnlyWhatFits()
    {
        var frames = new FrameTimes(capacity: 100);
        var t = 0L;
        for (var i = 0; i < 1000; i++) frames.Add(t += Second / 100);
        Assert.InRange(frames.Measure()!.Value.Fps, 99, 101);
    }
}

public class StatsReadoutsTests
{
    private static readonly StatsChoice All = new(true, true, true, true, true, true, true);

    [Fact]
    public void ShowsEachReading()
    {
        var stats = new GameStats
        {
            Frames = FrameTiming.Reading,
            Fps = 143.6,
            OnePercentLow = 101.2,
            CpuPercent = 23.4,
            GpuPercent = 87.5,
            GpuCelsius = 64.3,
            VramUsed = 6L << 30,
            VramTotal = 12L << 30,
            RamUsed = (long)(12.4 * (1L << 30)),
            RamTotal = 32L << 30,
        };
        var line = StatsReadouts.For(stats, All);
        Assert.Equal(["FPS", "1% LOW", "CPU", "GPU", "GPU °C", "VRAM", "RAM"], line.Select(r => r.Label));
        Assert.Equal(["144", "101", "23%", "88%", "64", "6.0G", "12.4G"], line.Select(r => r.Value));
        Assert.All(line, r => Assert.Equal(StatLevel.Normal, r.Level));
    }

    [Fact]
    public void MissingReadingsKeepTheirPlace()
    {
        var line = StatsReadouts.For(new GameStats { Frames = FrameTiming.Reading }, All);
        Assert.Equal(7, line.Count);
        Assert.All(line, r => Assert.Equal(StatsReadouts.None, r.Value));
    }

    [Fact]
    public void SaysWhenFpsIsOff()
    {
        var line = StatsReadouts.For(new GameStats { Frames = FrameTiming.NeedsPermission, CpuPercent = 10 }, All);
        Assert.Equal("OFF", line[0].Value);
        Assert.Equal("OFF", line[1].Value);
        Assert.Equal("10%", line[2].Value);
    }

    [Fact]
    public void OnlyThePickedOnes()
    {
        var line = StatsReadouts.For(new GameStats { Fps = 60, RamUsed = 1L << 30 }, new StatsChoice(true, false, false, false, false, false, true));
        Assert.Equal(["FPS", "RAM"], line.Select(r => r.Label));
    }

    [Theory]
    [InlineData(144, 120, "Normal")]
    [InlineData(144, 90, "Warn")]   // well under the average: stutter
    [InlineData(144, 60, "Bad")]
    [InlineData(40, 25, "Bad")]
    public void LowsShowStutter(double fps, double low, string level) =>
        Assert.Equal(Enum.Parse<StatLevel>(level), StatsReadouts.For(new GameStats { Fps = fps, OnePercentLow = low }, All)[1].Level);

    [Theory]
    [InlineData(25, "Bad")]
    [InlineData(45, "Warn")]
    [InlineData(60, "Normal")]
    public void LowFpsStandsOut(double fps, string level) =>
        Assert.Equal(Enum.Parse<StatLevel>(level), StatsReadouts.For(new GameStats { Fps = fps }, All)[0].Level);

    [Theory]
    [InlineData(70, "Normal")]
    [InlineData(82, "Warn")]
    [InlineData(90, "Bad")]
    public void HotGpuStandsOut(double celsius, string level) =>
        Assert.Equal(Enum.Parse<StatLevel>(level), StatsReadouts.For(new GameStats { GpuCelsius = celsius }, All)[4].Level);

    [Fact]
    public void NearlyFullMemoryStandsOut()
    {
        var line = StatsReadouts.For(new GameStats { VramUsed = 97, VramTotal = 100, RamUsed = 88, RamTotal = 100 }, All);
        Assert.Equal(StatLevel.Bad, line[5].Level);
        Assert.Equal(StatLevel.Warn, line[6].Level);
    }

    [Fact]
    public void OverlayUpdatesInPlace()
    {
        var overlay = new StatsOverlayViewModel();
        overlay.Show(new GameStats { Fps = 60 }, All);
        var first = overlay.Items[0];
        overlay.Show(new GameStats { Fps = 25 }, All);
        Assert.Same(first, overlay.Items[0]);
        Assert.Equal("25", first.Value);
        Assert.True(first.IsBad);
        overlay.Show(new GameStats(), new StatsChoice(false, false, true, false, false, false, false));
        Assert.Equal(["CPU"], overlay.Items.Select(i => i.Label));
    }
}

public class GpuCountersTests
{
    [Fact]
    public void ReadsCounterNames()
    {
        Assert.Equal(new GpuInstance(GpuCounters.Luid(0, 0xC8A1), 0, 3),
                     GpuCounters.Parse("pid_1234_luid_0x00000000_0x0000C8A1_phys_0_eng_3_engtype_3D"));
        Assert.Equal(new GpuInstance(GpuCounters.Luid(1, 0xD1A2), 1, -1), GpuCounters.Parse("luid_0x00000001_0x0000D1A2_phys_1"));
        Assert.Null(GpuCounters.Parse("_Total"));
    }

    [Fact]
    public void BusiestEngineOfEachCard()
    {
        var busy = GpuCounters.Busiest(
        [
            ("pid_10_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 70),
            ("pid_20_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D", 15),  // same engine, another process
            ("pid_10_luid_0x00000000_0x0000AAAA_phys_0_eng_1_engtype_3D", 40),
            ("pid_10_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D", 5),
            ("pid_30_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D", 99),
            ("pid_31_luid_0x00000000_0x0000BBBB_phys_0_eng_0_engtype_3D", 50),  // over 100 in all
        ]);
        Assert.Equal(85, busy[GpuCounters.Luid(0, 0xAAAA)], 3);
        Assert.Equal(100, busy[GpuCounters.Luid(0, 0xBBBB)], 3);
    }

    [Fact]
    public void MemoryOfEachCard()
    {
        var used = GpuCounters.MemoryInUse([("luid_0x00000000_0x0000AAAA_phys_0", 3e9), ("luid_0x00000000_0x0000AAAA_phys_1", 1e9), ("junk", 5)]);
        Assert.Equal(4_000_000_000L, used[GpuCounters.Luid(0, 0xAAAA)]);
        Assert.Single(used);
    }
}

#pragma warning disable CA1416 // layouts only; nothing is called
public class FrameTimingLayoutTests
{
    // These must match the Windows SDK's 64-bit layouts, or the trace sees garbage.
    [Fact]
    public void EtwStructures()
    {
        Assert.Equal(48, Unsafe.SizeOf<Etw.WnodeHeader>());
        Assert.Equal(120, Unsafe.SizeOf<Etw.EventTraceProperties>());
        Assert.Equal(116, (int)Marshal.OffsetOf<Etw.EventTraceProperties>(nameof(Etw.EventTraceProperties.LoggerNameOffset)));
        Assert.Equal(104, (int)Marshal.OffsetOf<Etw.EventTraceProperties>(nameof(Etw.EventTraceProperties.LoggerThreadId)));
        Assert.Equal(16, Unsafe.SizeOf<Etw.EventFilterDescriptor>());
        Assert.Equal(48, Unsafe.SizeOf<Etw.EnableTraceParameters>());
        Assert.Equal(32, (int)Marshal.OffsetOf<Etw.EnableTraceParameters>(nameof(Etw.EnableTraceParameters.EnableFilterDesc)));
        Assert.Equal(448, Unsafe.SizeOf<Etw.EventTraceLogfile>());
        Assert.Equal(112, Unsafe.SizeOf<Etw.EventRecord>());
    }
}
#pragma warning restore CA1416

public class StatsOverlaySettingsTests
{
    [Fact]
    public void OffUntilTurnedOn()
    {
        var settings = new DesktopSettings();
        Assert.False(settings.StatsOverlay.On);
        Assert.Equal("top-left", settings.StatsOverlay.Corner);
        Assert.Equal(new StatsChoice(true, true, true, true, true, false, true), settings.StatsOverlay.Choice);
        Assert.True(HotkeyText.TryParse(settings.StatsOverlay.Hotkey, out _, out _));
    }

    [Fact]
    public void KeptWithTheAppSettings()
    {
        var settings = new DesktopSettings();
        settings.StatsOverlay.On = true;
        settings.StatsOverlay.Corner = "bottom-right";
        settings.StatsOverlay.Vram = true;
        var json = System.Text.Json.JsonSerializer.Serialize(settings, DesktopSettingsJson.Default.DesktopSettings);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, DesktopSettingsJson.Default.DesktopSettings)!;
        Assert.True(back.StatsOverlay.On);
        Assert.Equal("bottom-right", back.StatsOverlay.Corner);
        Assert.True(back.StatsOverlay.Vram);
    }

    [Fact]
    public void OlderSettingsFilesGetTheDefaults()
    {
        var back = System.Text.Json.JsonSerializer.Deserialize("{\"CloseToTray\":false}", DesktopSettingsJson.Default.DesktopSettings)!;
        Assert.False(back.StatsOverlay.On);
        Assert.True(back.StatsOverlay.Fps);
    }
}
