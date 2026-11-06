using System.Diagnostics;

namespace ClipEngine.Desktop.Services;

// Uploads (squad views, cloud clips) go up slowly while a game is recorded. Sent at full speed, a
// clip fills the connection's upload for half a minute, and the game's own packets queue behind it:
// ping spikes and rubber-banding. At this pace a 25 MB view takes about three minutes, roughly a round.
internal static class UploadPace
{
    // ponytail: one fixed pace for every connection; measure the uplink if 1 Mbit/s is too slow or too much.
    internal const int InGameBytesPerSecond = 125_000;

    // Set by the main window's model: true while a game (not the desktop) is recorded.
    internal static Func<bool> InGame { get; set; } = () => false;

    internal static StreamContent Content(Stream stream) => new(new PacedStream(stream, InGame), 16 * 1024);
}

// Reads the stream no faster than the pace while slow() says so, and at full speed otherwise.
internal sealed class PacedStream(Stream inner, Func<bool> slow, int bytesPerSecond = UploadPace.InGameBytesPerSecond) : Stream
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _sent;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
    {
        if (!slow())
        {
            _clock.Restart();
            _sent = 0;
            return await inner.ReadAsync(buffer, cancel).ConfigureAwait(false);
        }
        var due = TimeSpan.FromSeconds((double)_sent / bytesPerSecond) - _clock.Elapsed;
        if (due > TimeSpan.Zero) await Task.Delay(due, cancel).ConfigureAwait(false);
        var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, bytesPerSecond / 8)], cancel).ConfigureAwait(false);
        _sent += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancel) =>
        ReadAsync(buffer.AsMemory(offset, count), cancel).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
