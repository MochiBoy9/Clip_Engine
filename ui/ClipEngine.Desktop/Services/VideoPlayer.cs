using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClipEngine.Desktop.Services;

internal enum PlaybackState { Empty, Stopped, Playing, Paused }

// What the clip player page needs from a video backend.
internal interface IVideoPlayer : IDisposable
{
    void Open(string path);
    void Close();
    void Play();
    void Pause();
    void Seek(TimeSpan position);
    TimeSpan Position { get; }
    TimeSpan? Duration { get; }
    PlaybackState State { get; }
    float Volume { get; set; }
}

// Plays clips with Media Foundation's MFPlay, which ships with Windows and renders into a window of
// ours through the hardware video path. No callback is registered: the page polls the state.
// The interface is called through its vtable; slot numbers follow IMFPMediaPlayer in mfplay.h.
[SupportedOSPlatform("windows")]
internal sealed unsafe class MfPlayer(nint hwnd) : IVideoPlayer
{
    private const int SlotPlay = 3;
    private const int SlotPause = 4;
    private const int SlotStop = 5;
    private const int SlotSetPosition = 7;
    private const int SlotGetPosition = 8;
    private const int SlotGetDuration = 9;
    private const int SlotGetState = 13;
    private const int SlotGetVolume = 19;
    private const int SlotSetVolume = 20;
    private const int SlotUpdateVideo = 32;
    private const int SlotSetBorderColor = 33;
    private const int SlotShutdown = 38;
    private const ushort VtI8 = 20;

    private nint _player;
    private float _volume = 1;

    // MFP_POSITIONTYPE_100NS is GUID_NULL.
    private static readonly Guid PositionType100ns = Guid.Empty;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    [DllImport("mfplay.dll", ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer([MarshalAs(UnmanagedType.LPWStr)] string? url, int startPlayback,
                                                   uint options, nint callback, nint hwnd, out nint player);

    public void Open(string path)
    {
        Close();
        Check(MFPCreateMediaPlayer(path, 1, 0, 0, hwnd, out _player));
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(SlotSetBorderColor))(_player, 0);
        ((delegate* unmanaged[Stdcall]<nint, float, int>)Slot(SlotSetVolume))(_player, _volume);
    }

    public void Close()
    {
        if (_player == 0) return;
        Call(SlotShutdown);
        Marshal.Release(_player);
        _player = 0;
    }

    public void Play()
    {
        if (_player != 0) Call(SlotPlay);
    }

    public void Pause()
    {
        if (_player != 0) Call(SlotPause);
    }

    public void Stop()
    {
        if (_player != 0) Call(SlotStop);
    }

    public void Seek(TimeSpan position)
    {
        if (_player == 0) return;
        var value = new PropVariant { Type = VtI8, Value = Math.Max(0, position.Ticks) };
        fixed (Guid* type = &PositionType100ns)
        {
            ((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(SlotSetPosition))(_player, type, &value);
        }
    }

    public TimeSpan Position => ReadTime(SlotGetPosition) ?? TimeSpan.Zero;
    public TimeSpan? Duration => ReadTime(SlotGetDuration);

    public PlaybackState State
    {
        get
        {
            if (_player == 0) return PlaybackState.Empty;
            int state;
            if (((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(SlotGetState))(_player, &state) < 0) return PlaybackState.Empty;
            return state switch
            {
                1 => PlaybackState.Stopped,
                2 => PlaybackState.Playing,
                3 => PlaybackState.Paused,
                _ => PlaybackState.Empty,
            };
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            if (_player != 0) ((delegate* unmanaged[Stdcall]<nint, float, int>)Slot(SlotSetVolume))(_player, _volume);
        }
    }

    // Redraws the current frame, after the window is resized or uncovered.
    public void UpdateVideo()
    {
        if (_player != 0) Call(SlotUpdateVideo);
    }

    private TimeSpan? ReadTime(int slot)
    {
        if (_player == 0) return null;
        PropVariant value = default;
        fixed (Guid* type = &PositionType100ns)
        {
            var hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(slot))(_player, type, &value);
            if (hr < 0 || value.Type != VtI8) return null;
        }
        return TimeSpan.FromTicks(value.Value);
    }

    private int Call(int slot) => ((delegate* unmanaged[Stdcall]<nint, int>)Slot(slot))(_player);

    private nint Slot(int slot) => (*(nint**)_player)[slot];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose() => Close();
}
