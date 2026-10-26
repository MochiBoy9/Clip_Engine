using System.Buffers.Binary;

namespace ClipEngine.Desktop.Services;

// Reads a clip's length from the 'mvhd' box of an MP4 file without decoding anything.
internal static class Mp4Info
{
    public static TimeSpan? ReadDuration(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FindMovieHeader(stream, 0, stream.Length, inMoov: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TimeSpan? FindMovieHeader(Stream stream, long start, long end, bool inMoov)
    {
        Span<byte> header = stackalloc byte[16];
        var position = start;
        while (position + 8 <= end)
        {
            stream.Position = position;
            if (stream.Read(header[..8]) != 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
            var headerSize = 8L;
            if (size == 1)
            {
                if (stream.Read(header[8..16]) != 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize) return null;

            if (!inMoov && type == "moov") return FindMovieHeader(stream, position + headerSize, position + size, inMoov: true);
            if (inMoov && type == "mvhd") return ReadMovieHeader(stream, position + headerSize);
            position += size;
        }
        return null;
    }

    private static TimeSpan? ReadMovieHeader(Stream stream, long offset)
    {
        Span<byte> data = stackalloc byte[32];
        stream.Position = offset;
        if (stream.Read(data) < 28) return null;
        uint timescale;
        ulong duration;
        if (data[0] == 1)
        {
            // version, flags, 8-byte creation and modification times
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            duration = BinaryPrimitives.ReadUInt64BigEndian(data[24..]);
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        }
        if (timescale == 0) return null;
        return TimeSpan.FromSeconds((double)duration / timescale);
    }
}
