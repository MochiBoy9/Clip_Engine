using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClipEngine.Desktop.Services;

// A program's icon at a large size, as Explorer draws it, saved as a PNG.
[SupportedOSPlatform("windows")]
internal static class ShellIcon
{
    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;
    private static readonly Guid ImageFactoryId = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize(int cx, int cy)
    {
        public int Cx = cx;
        public int Cy = cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid,
                                                          [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits,
                                        ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint window, nint dc);

    // Runs on a thread of its own in the single-threaded apartment the shell expects.
    public static bool SavePng(string exe, string path, int size)
    {
        var saved = false;
        var thread = new Thread(() =>
        {
            try { saved = Save(exe, path, size); }
            catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException or ExternalException
                                          or InvalidOperationException or ArgumentException) { }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return thread.Join(TimeSpan.FromSeconds(10)) && saved;
    }

    private static bool Save(string exe, string path, int size)
    {
        if (!File.Exists(exe)) return false;
        var id = ImageFactoryId;
        if (SHCreateItemFromParsingName(exe, 0, ref id, out var factory) != 0 || factory is null) return false;
        nint hbitmap = 0;
        try
        {
            if (factory.GetImage(new NativeSize(size, size), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbitmap) != 0 || hbitmap == 0)
                return false;
            if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Width <= 0 || info.Height <= 0)
                return false;

            var (w, h) = (info.Width, Math.Abs(info.Height));
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = w,
                Height = -h, // top-down rows
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[w * h * 4];
            var dc = GetDC(0);
            try
            {
                if (GetDIBits(dc, hbitmap, 0, (uint)h, pixels, ref header, 0) == 0) return false;
            }
            finally { ReleaseDC(0, dc); }

            // Icons come with alpha; a bitmap without any would otherwise save as fully transparent.
            var anyAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) { anyAlpha = true; break; }
            }
            if (!anyAlpha) for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            using var bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var buffer = bitmap.Lock())
            {
                for (var row = 0; row < h; row++)
                    Marshal.Copy(pixels, row * w * 4, buffer.Address + row * buffer.RowBytes, w * 4);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(path);
            return true;
        }
        finally
        {
            if (hbitmap != 0) DeleteObject(hbitmap);
            Marshal.ReleaseComObject(factory);
        }
    }
}
