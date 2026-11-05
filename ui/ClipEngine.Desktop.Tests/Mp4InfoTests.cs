using System.Buffers.Binary;
using ClipEngine.Desktop.Services;
using Xunit;

namespace ClipEngine.Desktop.Tests;

public class Mp4InfoTests
{
    [Fact]
    public void ReadsDurationFromMovieHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clip_{Guid.NewGuid():N}.mp4");
        try
        {
            using (var f = File.Create(path))
            {
                WriteBox(f, "ftyp", new byte[8]);
                var mvhd = new byte[100];
                BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);   // timescale
                BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(16), 30500);  // duration
                using var moov = new MemoryStream();
                WriteBox(moov, "mvhd", mvhd);
                WriteBox(f, "moov", moov.ToArray());
            }
            Assert.Equal(TimeSpan.FromSeconds(30.5), Mp4Info.ReadDuration(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NotAnMp4()
    {
        Assert.Null(Mp4Info.ReadDuration(typeof(Mp4InfoTests).Assembly.Location));
        Assert.Null(Mp4Info.ReadVideoSize(typeof(Mp4InfoTests).Assembly.Location));
    }

    [Fact]
    public void ReadsThePictureSizeFromTheVideoTrack()
    {
        var path = Path.Combine(Path.GetTempPath(), $"clip_{Guid.NewGuid():N}.mp4");
        try
        {
            using (var f = File.Create(path))
            {
                WriteBox(f, "ftyp", new byte[8]);
                using var moov = new MemoryStream();
                WriteBox(moov, "mvhd", new byte[100]);
                // An audio track first (no picture), then the video, with a version 1 header.
                WriteBox(moov, "trak", Box("tkhd", new byte[84]));
                var video = new byte[96];
                video[0] = 1;
                BinaryPrimitives.WriteUInt32BigEndian(video.AsSpan(88), 3840u << 16);
                BinaryPrimitives.WriteUInt32BigEndian(video.AsSpan(92), 2160u << 16);
                WriteBox(moov, "trak", Box("tkhd", video));
                WriteBox(f, "moov", moov.ToArray());
            }
            Assert.Equal((3840, 2160), Mp4Info.ReadVideoSize(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Box(string type, byte[] body)
    {
        using var s = new MemoryStream();
        WriteBox(s, type, body);
        return s.ToArray();
    }

    private static void WriteBox(Stream s, string type, byte[] body)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + body.Length));
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        s.Write(header);
        s.Write(body);
    }
}
