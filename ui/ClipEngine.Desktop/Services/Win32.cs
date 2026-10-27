using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ClipEngine.Desktop.Services;

[SupportedOSPlatform("windows")]
internal static unsafe partial class Win32
{
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_CLIPSIBLINGS = 0x04000000;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_APPWINDOW = 0x00040000;
    public const uint CS_DBLCLKS = 0x0008;
    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public nint MenuName;
        public nint ClassName;
        public nint IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PaintStruct
    {
        public nint Hdc;
        public int Erase;
        public int Left, Top, Right, Bottom;
        public int Restore;
        public int IncUpdate;
        public fixed byte Reserved[32];
    }

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    public static partial ushort RegisterClassEx(in WndClassEx wndClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y,
                                              int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint BeginPaint(nint hwnd, out PaintStruct paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndPaint(nint hwnd, in PaintStruct paint);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("gdi32.dll")]
    public static partial nint GetStockObject(int index);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    public static partial nint GetModuleHandle(nint name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);

    // Puts files on the clipboard the way Explorer's Copy does, so they paste into Discord, chat apps
    // and folders. owner must be a window of this process.
    public static bool CopyFilesToClipboard(nint owner, IReadOnlyList<string> paths)
    {
        const uint CF_HDROP = 15;
        const uint GMEM_MOVEABLE = 0x2;
        const int DropFilesSize = 20;  // DROPFILES: pFiles, pt.x, pt.y, fNC, fWide

        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var drop = GlobalAlloc(GMEM_MOVEABLE, (nuint)(DropFilesSize + list.Length));
        var effect = GlobalAlloc(GMEM_MOVEABLE, 4);
        if (drop == 0 || effect == 0)
        {
            if (drop != 0) GlobalFree(drop);
            if (effect != 0) GlobalFree(effect);
            return false;
        }

        var p = (byte*)GlobalLock(drop);
        new Span<byte>(p, DropFilesSize).Clear();
        *(int*)p = DropFilesSize;
        *(int*)(p + 16) = 1;
        list.CopyTo(new Span<byte>(p + DropFilesSize, list.Length));
        GlobalUnlock(drop);

        *(int*)GlobalLock(effect) = 1;  // DROPEFFECT_COPY, so pasting into a folder copies instead of moving
        GlobalUnlock(effect);

        if (!OpenClipboard(owner))
        {
            GlobalFree(drop);
            GlobalFree(effect);
            return false;
        }
        try
        {
            EmptyClipboard();
            // The clipboard owns the memory once SetClipboardData succeeds.
            if (SetClipboardData(CF_HDROP, drop) == 0)
            {
                GlobalFree(drop);
                GlobalFree(effect);
                return false;
            }
            if (SetClipboardData(RegisterClipboardFormat("Preferred DropEffect"), effect) == 0) GlobalFree(effect);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
