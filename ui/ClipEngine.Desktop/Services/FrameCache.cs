namespace ClipEngine.Desktop.Services;

// The last frames Detection Studio took from videos, as PNG bytes, so going back to one is instant
// instead of starting the engine again. The oldest goes once there are more than `capacity`.
internal sealed class FrameCache(int capacity)
{
    private readonly LinkedList<(string Key, byte[] Png)> _frames = new();

    private static string Key(string source, double seconds) =>
        $"{source}|{Math.Round(seconds, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";

    public int Count => _frames.Count;

    public byte[]? TryGet(string source, double seconds)
    {
        var key = Key(source, seconds);
        for (var node = _frames.First; node is not null; node = node.Next)
        {
            if (node.Value.Key != key) continue;
            // Used again: it's the newest now.
            _frames.Remove(node);
            _frames.AddFirst(node);
            return node.Value.Png;
        }
        return null;
    }

    public void Add(string source, double seconds, byte[] png)
    {
        var key = Key(source, seconds);
        for (var node = _frames.First; node is not null; node = node.Next)
        {
            if (node.Value.Key != key) continue;
            _frames.Remove(node);
            break;
        }
        _frames.AddFirst((key, png));
        while (_frames.Count > Math.Max(1, capacity)) _frames.RemoveLast();
    }
}
