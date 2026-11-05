using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClipEngine.Desktop.Services.Stats;

// Reads how busy the PC is: CPU and RAM from the kernel, GPU load and video memory from the
// performance counters Task Manager uses, and the GPU's temperature from its driver. Each Sample
// takes a few milliseconds of CPU, once a second, and only while the overlay is up.
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class HardwareMonitor : IDisposable
{
    private readonly Gpu? _gpu;
    private nint _query;
    private nint _engines;
    private nint _memory;
    private long _idle, _kernel, _user;
    private uint _kmtAdapter;
    private bool _noTemperature;

    public HardwareMonitor()
    {
        _gpu = Gpu.Find();
        GetSystemTimes(out _idle, out _kernel, out _user);
        if (PdhOpenQuery(null, 0, out _query) != 0)
        {
            _query = 0;
        }
        else
        {
            // The 3D engines are what a game keeps busy; the video engines are the recording's own work.
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out _engines) != 0) _engines = 0;
            if (PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _memory) != 0) _memory = 0;
            // Utilization is a rate, so it needs a reading to measure from.
            PdhCollectQueryData(_query);
        }
        if (_gpu is { } gpu)
        {
            var open = new OpenAdapterFromLuid { LuidLow = (uint)gpu.Luid, LuidHigh = (int)(gpu.Luid >> 32) };
            if (D3DKMTOpenAdapterFromLuid(ref open) == 0) _kmtAdapter = open.Adapter;
        }
    }

    public GameStats Sample(GameStats stats)
    {
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes idle time.
            var total = (kernel - _kernel) + (user - _user);
            if (total > 0) cpu = 100.0 * (total - (idle - _idle)) / total;
            (_idle, _kernel, _user) = (idle, kernel, user);
        }

        long? ramUsed = null, ramTotal = null;
        var memory = new MemoryStatus { Length = (uint)sizeof(MemoryStatus) };
        if (GlobalMemoryStatusEx(ref memory))
        {
            ramTotal = (long)memory.TotalPhys;
            ramUsed = (long)(memory.TotalPhys - memory.AvailPhys);
        }

        double? gpuPercent = null;
        long? vramUsed = null;
        if (_query != 0 && _gpu is { } gpu && PdhCollectQueryData(_query) == 0)
        {
            if (_engines != 0 && GpuCounters.Busiest(Read(_engines)).TryGetValue(gpu.Luid, out var busy)) gpuPercent = busy;
            if (_memory != 0 && GpuCounters.MemoryInUse(Read(_memory)).TryGetValue(gpu.Luid, out var used)) vramUsed = used;
        }

        return stats with
        {
            CpuPercent = cpu,
            RamUsed = ramUsed,
            RamTotal = ramTotal,
            GpuPercent = gpuPercent,
            GpuCelsius = Temperature(),
            VramUsed = vramUsed,
            VramTotal = _gpu is { Memory: > 0 } g ? g.Memory : null,
        };
    }

    // From the driver, the reading Task Manager shows. Older drivers don't report it.
    private double? Temperature()
    {
        if (_kmtAdapter == 0 || _noTemperature) return null;
        var perf = new AdapterPerfData();
        var query = new QueryAdapterInfo
        {
            Adapter = _kmtAdapter,
            Type = AdapterPerfDataType,
            PrivateDriverData = (nint)(&perf),
            PrivateDriverDataSize = (uint)sizeof(AdapterPerfData),
        };
        if (D3DKMTQueryAdapterInfo(ref query) != 0)
        {
            _noTemperature = true;
            return null;
        }
        // Tenths of a degree; 0 means the card doesn't say.
        var celsius = perf.Temperature / 10.0;
        return celsius is > 0 and < 150 ? celsius : null;
    }

    private static List<(string Name, double Value)> Read(nint counter)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out _, null);
        if (status != PdhMoreData || size == 0) return list;
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble | PdhFmtNoCap100, ref size, out var count, p) != 0) return list;
            var items = (CounterValueItem*)p;
            for (var i = 0; i < count; i++)
            {
                // The first reading of a new process's engine has nothing to measure from yet.
                if (items[i].Status is not (0 or 1)) continue;
                var name = Marshal.PtrToStringUni(items[i].Name);
                if (name is not null) list.Add((name, items[i].Value));
            }
        }
        return list;
    }

    public void Dispose()
    {
        if (_query != 0) PdhCloseQuery(_query);
        _query = 0;
        if (_kmtAdapter != 0)
        {
            var close = _kmtAdapter;
            D3DKMTCloseAdapter(ref close);
            _kmtAdapter = 0;
        }
    }

    // The graphics card games most likely run on: the one with the most memory of its own, which is
    // the dedicated card in a laptop with two.
    private sealed record Gpu(long Luid, long Memory)
    {
        private static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private const uint AdapterFlagSoftware = 2;

        public static Gpu? Find()
        {
            if (CreateDXGIFactory1(FactoryId, out var factory) < 0 || factory == 0) return null;
            Gpu? best = null;
            try
            {
                var vtable = *(nint**)factory;
                var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtable[12];
                for (uint i = 0; ; i++)
                {
                    nint adapter;
                    if (enumAdapters1(factory, i, &adapter) < 0) break;
                    var adapterVtable = *(nint**)adapter;
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, AdapterDesc1*, int>)adapterVtable[10];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & AdapterFlagSoftware) == 0)
                    {
                        var memory = (long)desc.DedicatedVideoMemory;
                        if (best is null || memory > best.Memory)
                            best = new Gpu(GpuCounters.Luid((uint)desc.LuidHigh, desc.LuidLow), memory);
                    }
                    Release(adapter);
                }
            }
            finally
            {
                Release(factory);
            }
            return best;
        }

        private static void Release(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)unknown)[2])(unknown);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValueItem
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapterFromLuid
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint Adapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo
    {
        public uint Adapter;
        public int Type;
        public nint PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    // D3DKMT_ADAPTER_PERFDATA (Windows 10 1809 and newer).
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterPerfData
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency, MaxMemoryFrequency, MaxMemoryFrequencyOC, MemoryBandwidth, PcieBandwidth;
        public uint FanRpm;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    private const int AdapterPerfDataType = 62;  // KMTQAITYPE_ADAPTERPERFDATA
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const uint PdhMoreData = 0x800007D2;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? source, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint count, byte* buffer);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(in Guid riid, out nint factory);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTOpenAdapterFromLuid(ref OpenAdapterFromLuid open);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo query);

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTCloseAdapter(ref uint adapter);
}
