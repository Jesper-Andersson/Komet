using Komet.Diagnostics;

namespace Komet.Vulkan;

// Per kind: count, total, longest, for the log report (Hitches, Builds). Not thread-safe: owners lock.
internal sealed class Tally(string[] names)
{
    private const int MaxKinds = 8;
    private readonly long[] _counts = new long[names.Length], _ticks = new long[names.Length], _longest = new long[names.Length];

    public void Add(int kind, long ticks)
    {
        if (!Index(kind, Math.Min(_counts.Length, MaxKinds)) || !Assert(ticks >= 0)) return;
        (_counts[kind], _ticks[kind], _longest[kind]) = (_counts[kind] + 1, _ticks[kind] + ticks, Math.Max(_longest[kind], ticks));
    }

    // "; header: 3 ports 12.0 ms (longest 6.0), ...", empty while nothing came
    public string Report(string header)
    {
        if (!NotNull(header) || !Assert(names.Length <= MaxKinds)) return "";
        var (ms, seen) = (FrameClock.TickMs, new List<string>(MaxKinds));
        for (var i = 0; i < Math.Min(names.Length, MaxKinds); i++)
            if (_counts[i] > 0)
                seen.Add($"{_counts[i]} {names[i]} {_ticks[i] * ms:0.0} ms (longest {_longest[i] * ms:0.0})");
        return seen.Count == 0 ? "" : $"; {header}: " + string.Join(", ", seen);
    }
}
