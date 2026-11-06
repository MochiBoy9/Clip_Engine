using System.Globalization;
using System.Text.RegularExpressions;

namespace ClipEngine.Desktop.Services.Stats;

// Which graphics card, and which of its engines, a Windows GPU performance counter is for. Counters are
// named like "pid_1234_luid_0x00000000_0x0000C8A1_phys_0_eng_0_engtype_3D" (GPU Engine) and
// "luid_0x00000000_0x0000C8A1_phys_0" (GPU Adapter Memory).
internal readonly record struct GpuInstance(long Luid, int Physical, int Engine);

internal static partial class GpuCounters
{
    public static GpuInstance? Parse(string name)
    {
        var m = InstanceRegex().Match(name);
        if (!m.Success) return null;
        var high = uint.Parse(m.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var low = uint.Parse(m.Groups[2].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var phys = int.Parse(m.Groups[3].ValueSpan, CultureInfo.InvariantCulture);
        var engine = m.Groups[4].Success ? int.Parse(m.Groups[4].ValueSpan, CultureInfo.InvariantCulture) : -1;
        return new GpuInstance(Luid(high, low), phys, engine);
    }

    // A LUID's two halves as one number, the way DXGI and these counters both name a card.
    public static long Luid(uint high, uint low) => (long)(((ulong)high << 32) | low);

    // How busy each card is: for each of its engines, every process's share added up, and the busiest
    // engine counts, as in Task Manager.
    public static Dictionary<long, double> Busiest(IEnumerable<(string Name, double Percent)> engines)
    {
        var perEngine = new Dictionary<GpuInstance, double>();
        foreach (var (name, percent) in engines)
        {
            if (Parse(name) is not { } instance || !double.IsFinite(percent)) continue;
            perEngine[instance] = perEngine.GetValueOrDefault(instance) + percent;
        }
        var perCard = new Dictionary<long, double>();
        foreach (var (instance, percent) in perEngine)
            perCard[instance.Luid] = Math.Max(perCard.GetValueOrDefault(instance.Luid), Math.Min(percent, 100));
        return perCard;
    }

    // Bytes of each card's own memory in use, from GPU Adapter Memory's Dedicated Usage.
    public static Dictionary<long, long> MemoryInUse(IEnumerable<(string Name, double Bytes)> adapters)
    {
        var perCard = new Dictionary<long, long>();
        foreach (var (name, bytes) in adapters)
        {
            if (Parse(name) is not { } instance || !double.IsFinite(bytes) || bytes < 0) continue;
            perCard[instance.Luid] = perCard.GetValueOrDefault(instance.Luid) + (long)bytes;
        }
        return perCard;
    }

    [GeneratedRegex(@"luid_0x([0-9A-Fa-f]{1,8})_0x([0-9A-Fa-f]{1,8})_phys_(\d+)(?:_eng_(\d+))?")]
    private static partial Regex InstanceRegex();
}
