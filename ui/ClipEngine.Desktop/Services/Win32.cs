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

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

    // Windows' Settings > Accessibility > Visual effects > Animation effects. Assumed on when it can't be read.
    public static bool ClientAreaAnimation()
    {
        const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
        return !SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var on, 0) || on != 0;
    }

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

    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    // Our windows over the game (the side panel, the "Clip saved" notice) would otherwise be
    // recorded into clips, since the engine captures the whole monitor. Windows 10 2004 and newer
    // can leave a window out of screen captures; older versions would show a black box instead,
    // so there it returns false and the window stays as it is.
    public static bool KeepOutOfCaptures(nint hwnd) =>
        hwnd != 0 && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    // A notice that never takes focus from the game and lets clicks through to it.
    public static void MakeOverlay(nint hwnd)
    {
        if (hwnd == 0) return;
        var style = (nint)((uint)GetWindowLongPtr(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, style);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ProcessPowerThrottling = 4;
    private const uint PowerThrottlingCurrentVersion = 1;
    private const uint PowerThrottlingExecutionSpeed = 0x1;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(nint process, int informationClass, ref PowerThrottlingState information,
                                                      uint size);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    // While the window is out of sight the app has little to do, so it runs in Windows' power-saving
    // mode (EcoQoS): on efficiency cores and at lower clocks where the CPU has them, leaving the fast
    // cores to the game. The recording engine is a separate process and isn't affected. Off again as
    // soon as the window is back.
    public static void SetBackgroundMode(bool background)
    {
        var state = new PowerThrottlingState
        {
            Version = PowerThrottlingCurrentVersion,
            ControlMask = PowerThrottlingExecutionSpeed,
            StateMask = background ? PowerThrottlingExecutionSpeed : 0,
        };
        SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowEx(nint parent, nint after, string? className, string? title);

    // The process of the window in front: the game, when one is playing. 0 when there's none.
    public static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return 0;
        GetWindowThreadProcessId(hwnd, out var id);
        // Store (UWP) apps sit in a frame window of Windows' own; the app draws in the window inside it.
        var inner = FindWindowEx(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
        if (inner != 0 && GetWindowThreadProcessId(inner, out var innerId) != 0 && innerId != 0) id = innerId;
        return (int)id;
    }

    // The middle of the window in front, in screen pixels: where the game is when one is playing.
    public static (int X, int Y)? ForegroundCenter()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || !GetWindowRect(hwnd, out var r)) return null;
        return ((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }
}
