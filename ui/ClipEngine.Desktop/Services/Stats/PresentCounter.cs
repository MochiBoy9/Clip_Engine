using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClipEngine.Desktop.Services.Stats;

// Counts the frames a game hands to Windows, from the events Direct3D already reports to Event
// Tracing for Windows (the same source as PresentMon and the Xbox Game Bar). Nothing touches the
// game's process, so it's safe with anti-cheat, and the game does no extra work: Direct3D only adds
// a small record to a buffer for each frame. Windows hands those over in batches about once a
// second, so this costs one short wake-up a second.
//
// Covers DirectX 9 to 12 (DXGI and D3D9's present events). Starting the trace needs an
// administrator or a member of the Performance Log Users group; without it, Start says so.
[SupportedOSPlatform("windows")]
internal sealed unsafe class PresentCounter : IDisposable
{
    private const string SessionName = "ClipEngine-FrameTimes";

    // Microsoft-Windows-DXGI and Microsoft-Windows-D3D9, and their Present_Start events.
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42;
    private const ushort DxgiPresentMultiplaneOverlayStart = 55;
    private const ushort D3D9PresentStart = 1;
    // The providers' "Events" and analytic channel keywords, which those events carry.
    private const ulong PresentKeywords = 0x8000000000000001;
    // Verbose takes every level; the event filter keeps it to the present events.
    private const byte LevelVerbose = 5;

    private readonly FrameTimes _frames;
    private GCHandle _self;
    private ulong _session;
    private ulong _trace = InvalidTrace;
    private Thread? _reader;
    private int _process;
    private long _lastFrameAt;

    private const ulong InvalidTrace = ulong.MaxValue;

    public PresentCounter(FrameTimes frames) => _frames = frames;

    // The process whose frames are counted; others are skipped. Changing it starts the count again.
    public int Process
    {
        get => Volatile.Read(ref _process);
        set
        {
            if (Interlocked.Exchange(ref _process, value) == value) return;
            _frames.Clear();
            Interlocked.Exchange(ref _lastFrameAt, 0);
        }
    }

    // Environment.TickCount64 when the counted process's last frame came in, or 0 before one has.
    public long LastFrameAt => Interlocked.Read(ref _lastFrameAt);

    public FrameTiming Start()
    {
        if (_reader is not null) return FrameTiming.Reading;
        if (IntPtr.Size != 8) return FrameTiming.Unavailable;

        var props = NewProperties();
        try
        {
            var error = Etw.StartTrace(out _session, SessionName, props);
            if (error == Etw.ErrorAlreadyExists)
            {
                // Left running by a copy of the app that didn't get to stop it.
                Etw.ControlTrace(0, SessionName, props, Etw.ControlStop);
                Marshal.FreeHGlobal((nint)props);
                props = NewProperties();
                error = Etw.StartTrace(out _session, SessionName, props);
            }
            if (error == Etw.ErrorAccessDenied) return FrameTiming.NeedsPermission;
            if (error != 0) return FrameTiming.Unavailable;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)props);
        }

        var enabled = Enable(DxgiProvider, [DxgiPresentStart, DxgiPresentMultiplaneOverlayStart]);
        enabled |= Enable(D3D9Provider, [D3D9PresentStart]);
        if (!enabled)
        {
            StopSession();
            return FrameTiming.Unavailable;
        }

        _self = GCHandle.Alloc(this);
        fixed (char* name = SessionName)
        {
            var logfile = new Etw.EventTraceLogfile
            {
                LoggerName = (nint)name,
                ProcessTraceMode = Etw.ProcessTraceModeRealTime | Etw.ProcessTraceModeEventRecord,
                EventRecordCallback = (nint)(delegate* unmanaged[Stdcall]<Etw.EventRecord*, void>)&OnEvent,
                Context = GCHandle.ToIntPtr(_self),
            };
            _trace = Etw.OpenTrace(ref logfile);
        }
        if (_trace == InvalidTrace || _trace == 0xFFFFFFFF)
        {
            _trace = InvalidTrace;
            StopSession();
            _self.Free();
            return FrameTiming.Unavailable;
        }

        // ProcessTrace delivers events on this thread until the session stops.
        var trace = _trace;
        _reader = new Thread(() =>
        {
            ThreadPower.Efficient();
            var handle = trace;
            Etw.ProcessTrace(&handle, 1, 0, 0);
        })
        {
            IsBackground = true,
            Name = "Frame timing",
            Priority = ThreadPriority.BelowNormal,
        };
        _reader.Start();
        return FrameTiming.Reading;
    }

    private bool Enable(Guid provider, ushort[] ids)
    {
        // Only the present events are sent, filtered out at the source.
        var filterSize = 4 + 2 * ids.Length;
        var filter = stackalloc byte[filterSize];
        filter[0] = 1;  // FilterIn
        filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)ids.Length;
        for (var i = 0; i < ids.Length; i++) *(ushort*)(filter + 4 + 2 * i) = ids[i];
        var descriptor = new Etw.EventFilterDescriptor { Ptr = (ulong)filter, Size = (uint)filterSize, Type = Etw.EventFilterTypeEventId };
        var parameters = new Etw.EnableTraceParameters
        {
            Version = Etw.EnableTraceParametersVersion2,
            EnableFilterDesc = (nint)(&descriptor),
            FilterDescCount = 1,
        };
        return Etw.EnableTraceEx2(_session, provider, Etw.ControlEnableProvider, LevelVerbose, PresentKeywords, 0, 0, &parameters) == 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnEvent(Etw.EventRecord* record)
    {
        if (record->UserContext == 0) return;
        if (GCHandle.FromIntPtr(record->UserContext).Target is not PresentCounter self) return;
        var id = record->EventId;
        if (id is not (DxgiPresentStart or DxgiPresentMultiplaneOverlayStart or D3D9PresentStart)) return;
        if ((int)record->ProcessId != Volatile.Read(ref self._process)) return;
        self._frames.Add(record->TimeStamp);
        Interlocked.Exchange(ref self._lastFrameAt, Environment.TickCount64);
    }

    private static Etw.EventTraceProperties* NewProperties()
    {
        var size = sizeof(Etw.EventTraceProperties) + (SessionName.Length + 1) * 2;
        var props = (Etw.EventTraceProperties*)Marshal.AllocHGlobal(size);
        new Span<byte>(props, size).Clear();
        props->Wnode.BufferSize = (uint)size;
        props->Wnode.Flags = Etw.WnodeFlagTracedGuid;
        props->Wnode.ClientContext = 1;  // timestamps from the performance counter
        props->LogFileMode = Etw.EventTraceRealTimeMode;
        props->BufferSize = 16;           // KB; a few hundred frames fit in one
        props->MinimumBuffers = 4;
        props->MaximumBuffers = 16;
        props->FlushTimer = 1;            // hand over what's there once a second
        props->LoggerNameOffset = (uint)sizeof(Etw.EventTraceProperties);
        return props;
    }

    private void StopSession()
    {
        if (_session == 0) return;
        var props = NewProperties();
        try
        {
            Etw.ControlTrace(_session, null, props, Etw.ControlStop);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)props);
        }
        _session = 0;
    }

    public void Dispose()
    {
        StopSession();
        if (_trace != InvalidTrace)
        {
            Etw.CloseTrace(_trace);
            _trace = InvalidTrace;
        }
        // ProcessTrace returns once the session is stopped and its last buffers are delivered.
        _reader?.Join(TimeSpan.FromSeconds(3));
        _reader = null;
        if (_self.IsAllocated) _self.Free();
    }
}

// Event Tracing for Windows, as much of it as PresentCounter needs. Layouts are for 64-bit Windows.
[SupportedOSPlatform("windows")]
internal static unsafe partial class Etw
{
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorAlreadyExists = 183;
    public const uint ControlStop = 1;
    public const uint ControlEnableProvider = 1;
    public const uint WnodeFlagTracedGuid = 0x00020000;
    public const uint EventTraceRealTimeMode = 0x00000100;
    public const uint ProcessTraceModeRealTime = 0x00000100;
    public const uint ProcessTraceModeEventRecord = 0x10000000;
    public const uint EventFilterTypeEventId = 0x80000200;
    public const uint EnableTraceParametersVersion2 = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct WnodeHeader
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public nint LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventFilterDescriptor
    {
        public ulong Ptr;
        public uint Size;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EnableTraceParameters
    {
        public uint Version;
        public uint EnableProperty;
        public uint ControlFlags;
        public Guid SourceId;
        public nint EnableFilterDesc;
        public uint FilterDescCount;
    }

    // EVENT_TRACE_LOGFILEW. Only the fields set here are named; the rest is what Windows fills in.
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    public struct EventTraceLogfile
    {
        [FieldOffset(0)] public nint LogFileName;
        [FieldOffset(8)] public nint LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public nint EventRecordCallback;
        [FieldOffset(440)] public nint Context;
    }

    // EVENT_RECORD, with EVENT_HEADER and its EVENT_DESCRIPTOR laid out in place.
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct EventRecord
    {
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(104)] public nint UserContext;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTrace(out ulong session, string name, EventTraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTrace(ulong session, string? name, EventTraceProperties* properties, uint control);

    [LibraryImport("advapi32.dll")]
    public static partial uint EnableTraceEx2(ulong session, in Guid provider, uint control, byte level, ulong matchAny,
                                              ulong matchAll, uint timeout, EnableTraceParameters* parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW")]
    public static partial ulong OpenTrace(ref EventTraceLogfile logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handles, uint count, nint start, nint end);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong trace);
}

// Puts the calling thread in Windows' power-saving mode (EcoQoS), like the engine's EfficiencyMode:
// on efficiency cores where the CPU has them, leaving the fast ones to the game.
[SupportedOSPlatform("windows")]
internal static partial class ThreadPower
{
    [StructLayout(LayoutKind.Sequential)]
    private struct State
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    private const int ThreadPowerThrottling = 3;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadInformation(nint thread, int informationClass, ref State information, uint size);

    public static void Efficient()
    {
        var state = new State { Version = 1, ControlMask = 1, StateMask = 1 };
        SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref state, (uint)Marshal.SizeOf<State>());
    }
}
