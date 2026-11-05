namespace ClipEngine.Desktop.Services.Stats;

// Takes a reading of the game in front and the PC once a second, on a thread of its own in
// Windows' power-saving mode, while the overlay is up. Stopped, it costs nothing.
// The readings themselves are Windows-only; elsewhere Start does nothing.
internal sealed class StatsSampler : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    // Windows hands frame events over about once a second, so a game is only called stalled after
    // a few seconds without one.
    private const long StalledMs = 3000;

    private readonly Action<GameStats> _reading;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;

    // reading is called on the sampler's thread.
    public StatsSampler(Action<GameStats> reading) => _reading = reading;

    // What starting the frame counter said, for Settings. Raised on the sampler's thread.
    public event Action<FrameTiming>? FrameTimingChecked;

    public bool Running => _thread is not null;

    public void Start()
    {
        if (_thread is not null || !OperatingSystem.IsWindows()) return;
        _stop.Reset();
        _thread = new Thread(Run) { IsBackground = true, Name = "Stats overlay", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is null) return;
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        _thread = null;
    }

    private void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        ThreadPower.Efficient();
        var frames = new FrameTimes();
        using var presents = new PresentCounter(frames);
        using var hardware = new HardwareMonitor();
        var timing = presents.Start();
        FrameTimingChecked?.Invoke(timing);
        if (timing == FrameTiming.Reading) presents.Process = Win32.ForegroundProcessId();

        // The first reading comes after a second, so CPU load has a second to be measured over.
        while (!_stop.Wait(Every))
        {
            var stats = new GameStats { Frames = timing };
            if (timing == FrameTiming.Reading)
            {
                presents.Process = Win32.ForegroundProcessId();
                var last = presents.LastFrameAt;
                if (last != 0 && Environment.TickCount64 - last < StalledMs && frames.Measure() is { } measured)
                    stats = stats with { Fps = measured.Fps, OnePercentLow = measured.OnePercentLow };
            }
            try
            {
                stats = hardware.Sample(stats);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // Windows without the GPU counters: the frame rate still shows.
            }
            _reading(stats);
        }
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
    }
}
