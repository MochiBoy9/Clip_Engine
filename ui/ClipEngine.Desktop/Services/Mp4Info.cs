using System.Buffers.Binary;

namespace ClipEngine.Desktop.Services;

// Reads a clip's length from the 'mvhd' box of an MP4 file, and its picture size from the video track's 'tkhd', without
// decoding anything.
internal static class Mp4Info
{
    // The video's width and height, or null when the file can't be read.
    public static (int Width, int Height)? ReadVideoSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            foreach (var moov in Boxes(stream, 0, stream.Length, "moov"))
            {
                foreach (var trak in Boxes(stream, moov.Start, moov.End, "trak"))
                {
                    foreach (var tkhd in Boxes(stream, trak.Start, trak.End, "tkhd"))
                    {
                        if (ReadTrackSize(stream, tkhd.Start, tkhd.End) is { Width: > 0, Height: > 0 } size) return size;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    // The contents of each box of this type between start and end.
    private static List<(long Start, long End)> Boxes(Stream stream, long start, long end, string wanted)
    {
        var found = new List<(long, long)>();
        Span<byte> header = stackalloc byte[16];
        var position = start;
        while (position + 8 <= end)
        {
            stream.Position = position;
            if (stream.Read(header[..8]) != 8) break;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header[4..8]);
            var headerSize = 8L;
            if (size == 1)
            {
                if (stream.Read(header[8..16]) != 8) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < headerSize || position + size > end) break;
            if (type == wanted) found.Add((position + headerSize, position + size));
            position += size;
        }
        return found;
    }

    // A track header ends with the width and height, 16.16 fixed point; an audio track's are 0.
    private static (int Width, int Height)? ReadTrackSize(Stream stream, long start, long end)
    {
        stream.Position = start;
        var version = stream.ReadByte();
        var at = start + (version == 1 ? 88 : 76);
        if (at + 8 > end) return null;
        Span<byte> data = stackalloc byte[8];
        stream.Position = at;
        if (stream.Read(data) != 8) return null;
        return ((int)(BinaryPrimitives.ReadUInt32BigEndian(data) >> 16), (int)(BinaryPrimitives.ReadUInt32BigEndian(data[4..]) >> 16));
    }

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
