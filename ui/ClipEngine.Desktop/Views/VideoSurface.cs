using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform;
using ClipEngine.Desktop.Services;

namespace ClipEngine.Desktop.Views;

// A child window the video is drawn into. Avalonia can't draw over it, so the player's controls
// sit below the video instead of on top.
[SupportedOSPlatform("windows")]
internal sealed class VideoSurface : NativeControlHost
{
    private const string ClassName = "ClipEngineVideoSurface";

    private static readonly Dictionary<nint, VideoSurface> Surfaces = [];
    private static Win32WndProc? _wndProc;  // kept alive while the class is registered
    private static bool _registered;

    private nint _hwnd;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nint Win32WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    public MfPlayer? Player { get; private set; }

    // Raised when the player is created with the window, and when it goes away with it.
    public event Action<MfPlayer?>? PlayerChanged;

    // Clicking the video plays or pauses it.
    public event Action? Clicked;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        Register();
        _hwnd = Win32.CreateWindowEx(0, ClassName, null, Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS,
                                     0, 0, 1, 1, parent.Handle, 0, Win32.GetModuleHandle(0), 0);
        if (_hwnd == 0) return base.CreateNativeControlCore(parent);
        Surfaces[_hwnd] = this;
        Player = new MfPlayer(_hwnd);
        PlayerChanged?.Invoke(Player);
        return new PlatformHandle(_hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.Handle != _hwnd)
        {
            base.DestroyNativeControlCore(control);
            return;
        }
        var player = Player;
        Player = null;
        PlayerChanged?.Invoke(null);
        player?.Dispose();
        Surfaces.Remove(_hwnd);
        Win32.DestroyWindow(_hwnd);
        _hwnd = 0;
    }

    private static void Register()
    {
        if (_registered) return;
        _wndProc = WndProc;
        var name = Marshal.StringToHGlobalUni(ClassName);  // must outlive the class
        var wc = new Win32.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            Style = Win32.CS_DBLCLKS,
            WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            Instance = Win32.GetModuleHandle(0),
            Cursor = Win32.LoadCursor(0, 32512),  // IDC_ARROW
            Background = Win32.GetStockObject(4),  // BLACK_BRUSH
            ClassName = name,
        };
        _registered = Win32.RegisterClassEx(wc) != 0;
    }

    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        Surfaces.TryGetValue(hwnd, out var surface);
        switch (msg)
        {
            case Win32.WM_PAINT when surface?.Player is { } player:
                Win32.BeginPaint(hwnd, out var paint);
                player.UpdateVideo();
                Win32.EndPaint(hwnd, paint);
                return 0;
            case Win32.WM_SIZE:
                surface?.Player?.UpdateVideo();
                break;
            case Win32.WM_LBUTTONUP:
                surface?.Clicked?.Invoke();
                return 0;
        }
        return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
