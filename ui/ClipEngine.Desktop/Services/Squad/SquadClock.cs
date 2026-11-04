namespace ClipEngine.Desktop.Services.Squad;

// The squad service's clock as seen from this PC. Every moment travels in server time, so each
// squadmate's app can turn it into its own PC's time and cut the same instant from its own
// recording. Each ping gives one estimate, server time minus the middle of the round trip, and
// the one with the shortest round trip of the last few wins, since it had the least room for
// the network to skew it.
internal sealed class SquadClock
{
    private const int Keep = 8;
    private readonly List<(long Rtt, long Offset)> _samples = [];

    // Server time minus local time, in ms; 0 until the first answer.
    public long OffsetMs { get; private set; }
    public bool Synced => _samples.Count > 0;

    // How much to trust the offset: half the best round trip.
    public long UncertaintyMs => _samples.Count == 0 ? long.MaxValue : _samples.Min(s => s.Rtt) / 2;

    public void AddSample(long sentLocalMs, long receivedLocalMs, long serverMs)
    {
        var rtt = receivedLocalMs - sentLocalMs;
        if (rtt < 0 || rtt > 30_000) return;
        var offset = serverMs - (sentLocalMs + receivedLocalMs) / 2;
        _samples.Add((rtt, offset));
        if (_samples.Count > Keep) _samples.RemoveAt(0);
        OffsetMs = _samples.MinBy(s => s.Rtt).Offset;
    }

    public long ToServer(long localMs) => localMs + OffsetMs;
    public long ToLocal(long serverMs) => serverMs - OffsetMs;
}
