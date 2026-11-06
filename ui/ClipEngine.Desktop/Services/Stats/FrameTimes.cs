namespace ClipEngine.Desktop.Services.Stats;

// The times the game handed frames to Windows, newest last, and the frame rate they add up to.
// Times are in 100 ns ticks. One thread adds while another reads, so both lock.
internal sealed class FrameTimes
{
    public const long TicksPerSecond = 10_000_000;

    // Enough for 10 seconds at 1600 fps; at higher rates the 1% low covers a shorter stretch.
    private readonly long[] _times;
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public FrameTimes(int capacity = 16384) => _times = new long[capacity];

    public void Add(long time)
    {
        lock (_gate)
        {
            _times[_next] = time;
            _next = (_next + 1) % _times.Length;
            if (_count < _times.Length) _count++;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _next = 0;
            _count = 0;
        }
    }

    // Frames per second over the last second before the newest frame, and the 1% low: the frame
    // rate of the slowest 1% of frames over the last 10 seconds, which is what a stutter feels like.
    // Null until there are two frames.
    public (double Fps, double OnePercentLow)? Measure()
    {
        lock (_gate)
        {
            if (_count < 2) return null;
            var newest = _times[(_next - 1 + _times.Length) % _times.Length];
            var list = new List<long>(Math.Min(_count, 4096));
            var recent = 0;
            long recentTicks = 0;
            // Walk back from the newest frame, one interval at a time. The frame rate counts the
            // intervals wholly inside the last second.
            for (var i = 1; i < _count; i++)
            {
                var later = _times[(_next - i + _times.Length) % _times.Length];
                var earlier = _times[(_next - i - 1 + _times.Length) % _times.Length];
                if (newest - later > 10 * TicksPerSecond) break;
                var interval = later - earlier;
                // Out of order or repeated stamps say nothing about pacing.
                if (interval <= 0) continue;
                list.Add(interval);
                if (newest - earlier <= TicksPerSecond)
                {
                    recent++;
                    recentTicks += interval;
                }
            }
            if (list.Count == 0) return null;
            long[] intervals = [.. list];
            if (recent == 0)
            {
                // A frame rate below one a second: the last interval is all there is.
                recent = 1;
                recentTicks = intervals[0];
            }
            var fps = recent * (double)TicksPerSecond / recentTicks;
            Array.Sort(intervals);
            var worst = Math.Max(1, intervals.Length / 100);
            long worstTicks = 0;
            for (var i = intervals.Length - worst; i < intervals.Length; i++) worstTicks += intervals[i];
            var low = worst * (double)TicksPerSecond / worstTicks;
            return (fps, Math.Min(low, fps));
        }
    }
}
