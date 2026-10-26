using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClipEngine.Desktop.Services;

// Asks Windows Explorer's thumbnail cache for a frame of each clip, one at a time on a background thread.
internal static unsafe partial class Thumbnails
{
    private static readonly BlockingCollection<(string Path, int Width, TaskCompletionSource<Bitmap?> Result)> Queue = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Thumbnails" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    });

    public static Task<Bitmap?> LoadAsync(string path, int width)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<Bitmap?>(null);
        _ = Worker.Value;
        var result = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((path, width, result));
        return result.Task;
    }

    private static void Run()
    {
        foreach (var (path, width, result) in Queue.GetConsumingEnumerable())
        {
            try
            {
                result.SetResult(Extract(path, width));
            }
            catch (Exception)
            {
                result.SetResult(null);
            }
        }
    }

    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private const int BiggerSizeOk = 0x1;
    private const int ThumbnailOnly = 0x8;

    private static Bitmap? Extract(string path, int width)
    {
        var iid = ImageFactoryId;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, &iid, out var factory) < 0 || factory == IntPtr.Zero) return null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            // IShellItemImageFactory::GetImage is the fourth entry of the vtable, after IUnknown's three.
            var vtable = *(IntPtr**)factory;
            var getImage = (delegate* unmanaged[Stdcall]<IntPtr, Size, int, IntPtr*, int>)vtable[3];
            var size = new Size(width, width * 9 / 16);
            if (getImage(factory, size, BiggerSizeOk | ThumbnailOnly, &hbitmap) < 0 || hbitmap == IntPtr.Zero) return null;
            return ToBitmap(hbitmap);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)factory)[2];
            release(factory);
        }
    }

    private static Bitmap? ToBitmap(IntPtr hbitmap)
    {
        BITMAP info;
        if (GetObjectW(hbitmap, sizeof(BITMAP), &info) == 0 || info.Width <= 0 || info.Height <= 0) return null;
        int w = info.Width, h = info.Height;

        var header = new BITMAPINFOHEADER
        {
            Size = 40,  // the real header, without the room for colours below
            Width = w,
            Height = -h,  // top-down rows
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* p = pixels)
            {
                if (GetDIBits(dc, hbitmap, 0, (uint)h, p, &header, 0) == 0) return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }

        // Video thumbnails usually come back with an empty alpha channel even though they're opaque.
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
        if (!hasAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        fixed (byte* p = pixels)
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (IntPtr)p, new PixelSize(w, h), new Vector(96, 96), w * 4);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Size(int cx, int cy)
    {
        public readonly int Cx = cx;
        public readonly int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        // GetDIBits writes a colour table after the header for some formats; leave room for it.
        public fixed uint Colors[4];
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, Guid* riid, out IntPtr item);

    [LibraryImport("gdi32.dll")]
    private static partial int GetObjectW(IntPtr handle, int size, void* buffer);

    [LibraryImport("gdi32.dll")]
    private static partial int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, void* bits, void* info, uint usage);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr dc);
}
