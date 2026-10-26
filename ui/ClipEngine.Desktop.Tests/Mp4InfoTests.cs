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
    public void NotAnMp4() => Assert.Null(Mp4Info.ReadDuration(typeof(Mp4InfoTests).Assembly.Location));

    private static void WriteBox(Stream s, string type, byte[] body)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(8 + body.Length));
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(header, 4);
        s.Write(header);
        s.Write(body);
    }
}
