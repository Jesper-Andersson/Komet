namespace Komet.Core;

// Feature counters on: Hud (all, while shown or benching) or Bench (tessellation only, so the engine arm pays nothing).
internal static class Counting
{
    public static bool On { get; private set; }

    // Raised when Hud turns true: a difference across it is no rate.
    public static int Epoch { get; private set; }

    public static bool Hud
    {
        get;
        set
        {
            if (value && !field) Epoch++;
            field = value;
            Update();
        }
    }

    public static bool Bench
    {
        get;
        set
        {
            field = value;
            Update();
        }
    }

    private static void Update()
    {
        On = Hud || Bench;
        _ = Assert(Epoch >= 0) && Assert(!Hud || Epoch > 0);
    }
}

// Ever-growing totals without an interlocked add: each thread adds to its own cache-line slot, a read sums them,
// threads past Slots - 1 share the last slot via Interlocked. Atomic 64-bit slots: two reads never go backwards.
internal sealed class Tally
{
    private const int MaxWidth = 16;
    private const int Slots = 64, Line = 8; // eight longs: one 64-byte cache line
    private static int _claimed;
    [ThreadStatic] private static int _slot; // this thread's slot + 1; 0 until it first counts
    private readonly long[] _cells;

    private readonly int _width, _stride;

    public Tally(int width)
    {
        _ = Assert(width is > 0 and <= MaxWidth);
        _width = Math.Clamp(width, 1, MaxWidth);
        _stride = (_width + Line - 1) / Line * Line;
        _cells = new long[Slots * _stride];
    }

    public void Add(int counter, long n)
    {
        if (!Index(counter, _width) || !Assert(n >= 0)) return;
        var slot = _slot - 1;
        if (slot < 0) slot = Claim();
        if (slot < Slots - 1) _cells[slot * _stride + counter] += n;
        else _ = Interlocked.Add(ref _cells[slot * _stride + counter], n);
    }

    public long Total(int counter)
    {
        if (!Index(counter, _width)) return 0;
        long sum = 0;
        for (var slot = 0; slot < Slots; slot++) sum += Volatile.Read(ref _cells[slot * _stride + counter]);
        return Assert(sum >= 0) ? sum : 0;
    }

    public void Clear() => Array.Clear(_cells);

    private static int Claim()
    {
        var slot = Math.Min(Interlocked.Increment(ref _claimed), Slots) - 1;
        _slot = slot + 1;
        return Assert(slot is >= 0 and < Slots) ? slot : Slots - 1;
    }
}
