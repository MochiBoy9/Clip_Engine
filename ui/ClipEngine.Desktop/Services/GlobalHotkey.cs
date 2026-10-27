using System.Runtime.Versioning;
using Avalonia.Controls;

namespace ClipEngine.Desktop.Services;

// Text like "Alt+Shift+C" as Win32 hotkey modifiers and a virtual key.
internal static class HotkeyText
{
    public static bool TryParse(string? text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.ToUpperInvariant();
            switch (part)
            {
                case "CTRL" or "CONTROL": modifiers |= 0x2; continue;
                case "ALT": modifiers |= 0x1; continue;
                case "SHIFT": modifiers |= 0x4; continue;
                case "WIN" or "WINDOWS": modifiers |= 0x8; continue;
            }
            if (key != 0) return false;
            key = part switch
            {
                { Length: 1 } when char.IsAsciiLetterOrDigit(part[0]) => part[0],
                ['F', ..] when int.TryParse(part[1..], out var f) && f is >= 1 and <= 24 => (uint)(0x6F + f),
                "HOME" => 0x24,
                "END" => 0x23,
                "INSERT" or "INS" => 0x2D,
                "PAGEUP" or "PGUP" => 0x21,
                "PAGEDOWN" or "PGDN" => 0x22,
                "PAUSE" => 0x13,
                "SCROLLLOCK" => 0x91,
                _ => 0,
            };
            if (key == 0) return false;
        }
        // A letter on its own would fire while typing in chat, so letters and digits need a modifier.
        return key != 0 && (modifiers != 0 || key is < 0x30 or > 0x5A);
    }
}

// A system-wide hotkey that works while a game has focus, delivered through a window of ours.
[SupportedOSPlatform("windows")]
internal sealed class GlobalHotkey : IDisposable
{
    private readonly TopLevel _window;
    private readonly int _id;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private bool _registered;

    public GlobalHotkey(TopLevel window, int id)
    {
        _window = window;
        _id = id;
        _hook = Hook;
        Win32Properties.AddWndProcHookCallback(window, _hook);
    }

    public event Action? Pressed;

    // Returns false when the text isn't a hotkey or another app already owns it.
    public bool Register(string text)
    {
        Unregister();
        if (!HotkeyText.TryParse(text, out var modifiers, out var key)) return false;
        var hwnd = _window.TryGetPlatformHandle()?.Handle ?? 0;
        _registered = hwnd != 0 && Win32.RegisterHotKey(hwnd, _id, modifiers | Win32.MOD_NOREPEAT, key);
        return _registered;
    }

    private void Unregister()
    {
        if (!_registered) return;
        if (_window.TryGetPlatformHandle()?.Handle is { } hwnd) Win32.UnregisterHotKey(hwnd, _id);
        _registered = false;
    }

    private nint Hook(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != Win32.WM_HOTKEY || wParam != _id) return 0;
        handled = true;
        Pressed?.Invoke();
        return 0;
    }

    public void Dispose()
    {
        Unregister();
        Win32Properties.RemoveWndProcHookCallback(_window, _hook);
    }
}
