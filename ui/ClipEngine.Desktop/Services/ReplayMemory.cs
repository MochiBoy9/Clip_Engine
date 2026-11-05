using System.Globalization;
using System.Runtime.InteropServices;

namespace ClipEngine.Desktop.Services;

internal enum ReplayAdviceLevel { Fine, Note, Warning, TooBig }

// What the replay buffer will take in memory, and whether this PC can spare it. The engine keeps the buffer to 40% of
// the PC's memory however long it's set (LimitBufferMemory in src/app/RecordingSession.cpp), so a buffer bigger than
// that holds less than asked; past ten minutes a PC with little memory or few cores is better off with less.
internal static class ReplayMemory
{
    public const int MaxSeconds = 1800;
    // Longer than this is an extended buffer, which slower PCs are warned about.
    public const int ExtendedSeconds = 600;
    public const double EngineShare = 0.4;
    // Below either of these a PC is warned before an extended buffer.
    public const long LowMemoryBytes = 15L << 30;  // "16 GB" PCs report a little under 16
    public const int LowCores = 8;

    private const int AudioBitrate = 192_000;
    private const int AudioTracks = 5;

    public sealed record Advice(ReplayAdviceLevel Level, long Bytes, string Text);

    // The engine's own pick when the bitrate is automatic (AutoBitrate in src/app/RecordingSession.cpp).
    public static long VideoBitrate(int lines, int fps, int bitrateMbps)
    {
        if (bitrateMbps > 0) return bitrateMbps * 1_000_000L;
        var width = lines * 16.0 / 9;
        return (long)Math.Clamp(0.1 * width * lines * fps, 8e6, 150e6);
    }

    public static long EstimateBytes(int seconds, int lines, int fps, int bitrateMbps) =>
        (VideoBitrate(lines, fps, bitrateMbps) + (long)AudioTracks * AudioBitrate) / 8 * seconds;

    public static bool IsLowPower(long memoryBytes, int cores) => memoryBytes < LowMemoryBytes || cores < LowCores;

    public static Advice For(int seconds, int lines, int fps, int bitrateMbps, long memoryBytes, int cores)
    {
        var bytes = EstimateBytes(seconds, lines, fps, bitrateMbps);
        var size = Size(bytes);
        if (memoryBytes <= 0) return new(ReplayAdviceLevel.Fine, bytes, $"About {size} in memory.");

        var limit = (long)(memoryBytes * EngineShare);
        if (bytes > limit)
        {
            var keeps = (int)(limit / (double)bytes * seconds / 60);
            return new(ReplayAdviceLevel.TooBig, bytes,
                $"This needs about {size} of memory, more than the {Size(limit)} this PC can spare for it, so the replay " +
                $"will keep about {keeps} min. A lower resolution or bitrate keeps more.");
        }
        if (seconds > ExtendedSeconds && IsLowPower(memoryBytes, cores))
        {
            return new(ReplayAdviceLevel.Warning, bytes,
                $"About {size} stays in memory while you play. With {Size(memoryBytes)} of memory and {cores} cores, " +
                "this PC may stutter in games with a replay this long; 10 min or less is recommended here.");
        }
        return seconds > ExtendedSeconds
            ? new(ReplayAdviceLevel.Note, bytes, $"About {size} stays in memory while you play.")
            : new(ReplayAdviceLevel.Fine, bytes, $"About {size} in memory.");
    }

    // "450 MB", "2.4 GB", "16 GB".
    public static string Size(long bytes)
    {
        var gb = bytes / (double)(1L << 30);
        if (gb >= 10) return string.Create(CultureInfo.InvariantCulture, $"{gb:0} GB");
        if (gb >= 1) return string.Create(CultureInfo.InvariantCulture, $"{gb:0.0} GB");
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, bytes >> 20)} MB");
    }

    // The PC's memory and logical cores.
    public static (long MemoryBytes, int Cores) ThisPc() =>
        (GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount);

    // The main monitor's height and refresh rate, for estimating what "native" and "match monitor" record at.
    public static (int Lines, int Hz) MainMonitor()
    {
        if (!OperatingSystem.IsWindows()) return (1080, 60);
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        return EnumDisplaySettingsW(null, -1, ref mode) && mode.PelsHeight > 0
            ? ((int)mode.PelsHeight, mode.DisplayFrequency > 1 ? (int)mode.DisplayFrequency : 60)
            : (1080, 60);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int IcmMethod;
        public int IcmIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DevMode mode);
}
