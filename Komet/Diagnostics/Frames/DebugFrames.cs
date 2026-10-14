namespace Komet.Diagnostics;

// At: s since capture start. Allocation KiB, read at the HUD's Ortho stage. Gens: bit n = a gen n collection.
internal readonly record struct DebugFrame(
    float At, float DtMs, float GcMs, float JitMs, float RunQueueMs, float OutsideMs, int AllocKb, int MainAllocKb, byte Gens,
    bool Steady);

// Its dearest marks are copied into _marks, so no frame's profile tree stays alive
internal readonly record struct DebugSpike(long Index, DebugFrame Frame);

internal readonly record struct DebugSummary(
    int Frames, int Steady, double Seconds, double AvgMs, double MedianMs, double P90Ms, double P99Ms, double P999Ms,
    double WorstMs, double StdevMs, float Low1Fps, float Low01Fps, int Over17, int Over33, int Over50, int Over100,
    double GcMs, double GcMaxMs, double JitMs, double RunQueueMs, double RunQueueMaxMs, double OutsideMs, double AllocMbPerSec,
    double MainAllocMbPerSec, int Gen0, int Gen1, int Gen2)
{
    public double Fps => Seconds > 0 ? Frames / Seconds : double.NaN;
}

// Main thread only; nothing allocates per frame.
internal sealed class DebugFrames
{
    public const int Capacity = 1 << 16, MaxSpikes = 16, Marks = 3;
    private readonly DebugFrame[] _ring = new DebugFrame[Capacity];
    private readonly DebugSpike[] _spikes = new DebugSpike[MaxSpikes];
    private readonly (string? Name, float Ms)[] _marks = new (string?, float)[MaxSpikes * Marks];
    private readonly long _started = Environment.TickCount64;
    private long _all, _main;
    private int _gen0, _gen1, _gen2, _spikeCount;

    public DebugFrames() => (_all, _main, _gen0, _gen1, _gen2) = (GC.GetTotalAllocatedBytes(),
        GC.GetAllocatedBytesForCurrentThread(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    public long Count { get; private set; }
    public float Now => (Environment.TickCount64 - _started) / 1000f;

    public DebugFrame Add(in FrameRecord frame, ReadOnlySpan<(string? Name, double Ms)> top, bool steady)
    {
        var (all, main) = (GC.GetTotalAllocatedBytes(), GC.GetAllocatedBytesForCurrentThread());
        var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var gens = (byte)((g0 > _gen0 ? 1 : 0) | (g1 > _gen1 ? 2 : 0) | (g2 > _gen2 ? 4 : 0));
        var record = new DebugFrame(Now, (float)frame.DtMs, (float)frame.GcMs, (float)frame.JitMs, Ms(frame.RunQueueMs),
            Ms(frame.OutsideMs), (int)Math.Min(int.MaxValue, (all - _all) >> 10), (int)Math.Min(int.MaxValue, (main - _main) >> 10),
            gens, steady);
        (_all, _main, _gen0, _gen1, _gen2) = (all, main, g0, g1, g2);
        _ring[(int)(Count % Capacity)] = record;
        if (Assert(record.DtMs >= 0) && Assert(Count >= 0)) Spike(Count, record, top);
        Count++;
        return record;
    }

    private static float Ms(double ms) => double.IsFinite(ms) ? (float)ms : float.NaN;

    private void Spike(long index, in DebugFrame frame, ReadOnlySpan<(string? Name, double Ms)> top)
    {
        var slot = _spikeCount < MaxSpikes ? _spikeCount : Cheapest();
        if (!Index(slot, MaxSpikes) || (_spikeCount == MaxSpikes && _spikes[slot].Frame.DtMs >= frame.DtMs)) return;
        _spikes[slot] = new DebugSpike(index, frame);
        for (var i = 0; i < Marks; i++)
            _marks[slot * Marks + i] = i < top.Length && top[i].Name is { } name ? (name, (float)top[i].Ms) : (null, 0);
        _spikeCount = Math.Min(MaxSpikes, _spikeCount + 1);
        _ = Assert(_spikeCount <= MaxSpikes);
    }

    private int Cheapest()
    {
        var cheapest = 0;
        for (var i = 1; i < MaxSpikes; i++)
            if (_spikes[i].Frame.DtMs < _spikes[cheapest].Frame.DtMs) cheapest = i;
        return Assert(_spikeCount == MaxSpikes) && Index(cheapest, MaxSpikes) ? cheapest : 0;
    }

    public (DebugSpike Spike, (string? Name, float Ms)[] Marks)[] Spikes()
    {
        var spikes = new (DebugSpike, (string?, float)[])[_spikeCount];
        for (var i = 0; i < Math.Min(_spikeCount, MaxSpikes); i++)
            spikes[i] = (_spikes[i], _marks.AsSpan(i * Marks, Marks).ToArray());
        Array.Sort(spikes, static (a, b) => b.Item1.Frame.DtMs.CompareTo(a.Item1.Frame.DtMs));
        return Assert(spikes.Length <= MaxSpikes) && NotNull(spikes) ? spikes : [];
    }

    public DebugFrame[] Since(float from)
    {
        var held = (int)Math.Min(Count, Capacity);
        var first = Count - held;
        var kept = new List<DebugFrame>(held);
        for (var i = 0; i < Math.Min(held, Capacity); i++)
            if (_ring[(int)((first + i) % Capacity)] is var frame && frame.At >= from) kept.Add(frame);
        return Assert(kept.Count <= Capacity) && Assert(first >= 0) ? [.. kept] : [];
    }

    public static DebugSummary Summarize(DebugFrame[] frames)
    {
        if (!NotNull(frames) || !Assert(frames.Length <= Capacity) || frames.Length == 0) return default;
        var sorted = new float[frames.Length];
        var (steady, seconds, sum, sq) = (0, 0.0, 0.0, 0.0);
        foreach (var frame in frames.Bounded(Capacity))
        {
            seconds += frame.DtMs / 1000.0;
            if (!frame.Steady) continue;
            sorted[steady++] = frame.DtMs;
            (sum, sq) = (sum + frame.DtMs, sq + (double)frame.DtMs * frame.DtMs);
        }

        var window = sorted.AsSpan(0, steady);
        window.Sort();
        var mean = steady > 0 ? sum / steady : double.NaN;
        var over = (Over(window, 1000 / 60.0), Over(window, 1000 / 30.0), Over(window, 50), Over(window, 100));
        var split = Split(frames, seconds);
        return split with
        {
            Frames = frames.Length, Steady = steady, Seconds = seconds, AvgMs = mean, MedianMs = FrameStats.Percentile(window, 500),
            P90Ms = FrameStats.Percentile(window, 900), P99Ms = FrameStats.Percentile(window, 990),
            P999Ms = FrameStats.Percentile(window, 999),
            WorstMs = steady > 0 ? window[^1] : double.NaN,
            StdevMs = steady > 1 ? Math.Sqrt(Math.Max(0, sq / steady - mean * mean)) : double.NaN,
            Low1Fps = FrameStats.LowFps(window, 100), Low01Fps = FrameStats.LowFps(window, 1000),
            Over17 = over.Item1, Over33 = over.Item2, Over50 = over.Item3, Over100 = over.Item4
        };
    }

    private static DebugSummary Split(DebugFrame[] frames, double seconds)
    {
        var (gc, gcMax, jit, runq, runqMax, outside, all, main) = (0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0L, 0L);
        var (gen0, gen1, gen2) = (0, 0, 0);
        foreach (var frame in frames.Bounded(Capacity))
        {
            (gc, gcMax, jit) = (gc + frame.GcMs, Math.Max(gcMax, frame.GcMs), jit + frame.JitMs);
            if (float.IsFinite(frame.RunQueueMs)) (runq, runqMax) = (runq + frame.RunQueueMs, Math.Max(runqMax, frame.RunQueueMs));
            if (float.IsFinite(frame.OutsideMs)) outside += frame.OutsideMs;
            (all, main) = (all + frame.AllocKb, main + frame.MainAllocKb);
            (gen0, gen1, gen2) = (gen0 + (frame.Gens & 1), gen1 + ((frame.Gens >> 1) & 1), gen2 + ((frame.Gens >> 2) & 1));
        }

        var perSecond = seconds > 0 ? 1 / (1024.0 * seconds) : double.NaN;
        return Assert(frames.Length > 0) && Assert(gc >= 0)
            ? new DebugSummary
            {
                GcMs = gc, GcMaxMs = gcMax, JitMs = jit, RunQueueMs = runq / frames.Length, RunQueueMaxMs = runqMax,
                OutsideMs = outside / frames.Length, AllocMbPerSec = all * perSecond, MainAllocMbPerSec = main * perSecond,
                Gen0 = gen0, Gen1 = gen1, Gen2 = gen2
            }
            : default;
    }

    private static int Over(ReadOnlySpan<float> sorted, double ms)
    {
        if (!Assert(ms > 0) || !Assert(sorted.Length <= Capacity)) return 0;
        var below = sorted.BinarySearch((float)ms);
        return sorted.Length - (below < 0 ? ~below : below + 1);
    }
}
