using Komet.Diagnostics;
using Komet.Tessellation;
using Vintagestory.Client;

namespace Komet.Bench;

[Flags]
internal enum BenchFrameTags : byte
{
    None = 0,
    Paused = 1, // the in-world clock stood still: out of every statistic
    Unfocused = 2, // counted; reported as the focused fraction
    Discard = 4, // the first seconds of a segment, after a move or an arm switch: out of every statistic
    Warmup = 8, // a warm-up lap: its own segment statistics, out of the arm statistics
    Unmeasured = 16, // setup, climb, settle: the same
    NoStamp = 32 // the driver did not run in this frame, so nothing says what it was: out of every statistic
}

// One frame as FrameClock closed it, with the driver's stamp; allocation in KiB, queues and passes as of the next frame
internal readonly record struct BenchFrame(
    float DtMs, float GcMs, float JitMs, float RunQueueMs, int AllocKb, int MainAllocKb, byte Gen0, byte Gen1,
    byte Gen2, BenchFrameTags Flags, short Segment, int TessQ, int TessNear, int UploadQ, int Received, int TessPasses,
    float TessMs, float X, float Z, float Yaw, float GpuMs = float.NaN, float OpaqueGpuMs = float.NaN); // Vulkan's last timed frame, its opaque terrain

// The latest collection at a spike frame's end (GC.GetGCMemoryInfo); Valid false: the frame collected nothing
internal readonly record struct BenchGc(
    bool Valid, long Index, int Generation, float PauseMs, bool Compacted, bool Concurrent, long PromotedKb);

internal readonly record struct BenchSpike(int Frame, float Ms, BenchGc Gc);

// A spike frame's profiler mark. Range null: the frame itself; Name null: a child range whole; Calls summed into Ms.
internal readonly record struct BenchSpikeMark(string? Range, string? Name, float Ms, int Calls = 0)
{
    public bool Empty => Range is null && Name is null;
}

// Frame N arrives as N+1 starts, with N's Before-stage stamp; nothing allocates but a spike's GetGCMemoryInfo (288 B)
internal sealed class BenchRecorder : IFrameSink
{
    public const int MaxFrames = FrameStats.MaxFrames, MaxSpikes = 32, MarksPerSpike = 8;
    public const BenchFrameTags Excluded = BenchFrameTags.Paused | BenchFrameTags.Discard | BenchFrameTags.NoStamp;

    private readonly BenchFrame[] _frames;
    private readonly BenchSpikeMark[] _marks;
    private readonly int _segments, _perSegment;

    // Per segment, dearest first; a tie keeps the earlier frame; empty slots hold -1 ms
    private readonly BenchSpike[] _spikes;

    private BenchFrameTags _stampFlags;
    private int _stampSegment = -1, _chunks;
    private float _stampX, _stampZ, _stampYaw;
    private bool _stamped;
    private TessTotals _tess; // TessAccounting's cumulative passes and time at the previous frame

    public BenchRecorder(int capacity, int segments, int spikesPerSegment)
    {
        if (!Assert(capacity is > 0 and <= MaxFrames)) capacity = 1;
        if (!Assert(segments is > 0 and <= BenchScenario.MaxSegments)) segments = 1;
        (_frames, _segments, _perSegment) =
            (new BenchFrame[capacity], segments, Math.Clamp(spikesPerSegment, 1, MaxSpikes));
        _spikes = new BenchSpike[segments * _perSegment];
        Array.Fill(_spikes, new BenchSpike(-1, -1, default));
        _marks = new BenchSpikeMark[_spikes.Length * MarksPerSpike];
    }

    public bool Recording { get; private set; }
    public int Count { get; private set; }
    public int Dropped { get; private set; } // frames past the capacity
    public int Capacity => _frames.Length;
    public ReadOnlySpan<BenchFrame> Frames => _frames.AsSpan(0, Count);

    public void Frame(in FrameRecord frame, in FrameCounters counters)
    {
        // NaN dt fails the first test too
        if (!Assert(Recording) || !Assert(frame.DtMs >= 0) || !Finite(frame.GcMs)) return;
        var (chunks, tess) = (RuntimeStats.chunksReceived, TessAccounting.Totals());
        // tess itself after Clear() at a world change
        var done = tess.Passes >= _tess.Passes && tess.Ticks >= _tess.Ticks ? tess.Since(_tess) : tess;
        var received = chunks >= _chunks ? chunks - _chunks : chunks; // RuntimeStats.Reset zeroes the count
        var flags = _stamped ? _stampFlags : _stampFlags | BenchFrameTags.NoStamp;
        (_chunks, _tess, _stamped) = (chunks, tess, false);
        if (Count >= _frames.Length)
        {
            Dropped++;
            return;
        }

        var (i, ms) = (Count++, (float)frame.DtMs);
        _frames[i] = new BenchFrame(ms, (float)frame.GcMs, (float)frame.JitMs, (float)frame.RunQueueMs,
            Kb(counters.Allocated), Kb(counters.MainAllocated), Collections(counters.Gen0), Collections(counters.Gen1),
            Collections(counters.Gen2), flags, (short)_stampSegment, RuntimeStats.chunksAwaitingTesselation,
            TessSchedule.NearWaiting, RuntimeStats.chunksAwaitingPooling, received,
            (int)Math.Min(done.Passes, int.MaxValue), (float)done.Ms, _stampX, _stampZ, _stampYaw,
            (float)Komet.Vulkan.VulkanRenderer.FrameGpuMs, OpaqueGpu());
        if ((flags & Excluded) == 0 && _stampSegment >= 0)
            Spike(i, ms, frame.Root, counters.Gen0 > 0); // gen1 and gen2 count in Gen0 too
    }

    private static float OpaqueGpu()
    {
        Span<double> groups = stackalloc double[3];
        var timed = Komet.Vulkan.VulkanRenderer.TerrainGpuMs(groups);
        return Assert(groups.Length == 3) && timed ? (float)groups[1] : float.NaN;
    }

    // Counters restart here, so the first frame's deltas cover only that frame
    public void Start()
    {
        if (!Assert(!Recording) || !Assert(Count == 0)) return;
        (_chunks, _tess, Recording) = (RuntimeStats.chunksReceived, TessAccounting.Totals(), true);
    }

    public void Stop()
    {
        _ = Assert(Recording) && Assert(Count <= Capacity);
        Recording = false;
    }

    public void Stamp(int segment, BenchFrameTags flags, float x, float z, float yaw)
    {
        if (!Index(segment, _segments) || !Assert(float.IsFinite(x) && float.IsFinite(z)) || !Finite(yaw)) return;
        (_stampSegment, _stampFlags, _stampX, _stampZ, _stampYaw, _stamped) = (segment, flags, x, z, yaw, true);
    }

    public ReadOnlySpan<BenchSpike> Spikes(int segment) =>
        Index(segment, _segments) && Assert(_spikes.Length == _segments * _perSegment)
            ? _spikes.AsSpan(segment * _perSegment, _perSegment) : [];

    public ReadOnlySpan<BenchSpikeMark> Marks(int segment, int rank) =>
        Index(segment, _segments) && Index(rank, _perSegment)
            ? _marks.AsSpan((segment * _perSegment + rank) * MarksPerSpike, MarksPerSpike) : [];

    private static int Kb(long bytes) =>
        Assert(bytes >= 0) && Assert(bytes / 1024 <= int.MaxValue) ? (int)(bytes / 1024) : 0;

    private static byte Collections(int count) =>
        Assert(count >= 0) && Assert(count < 1 << 16) ? (byte)Math.Min(count, byte.MaxValue) : (byte)0;

    private void Spike(int frame, float ms, ProfileEntryRange? root, bool collected)
    {
        if (!Index(_stampSegment, _segments) || !Assert(ms >= 0)) return;
        var spikes = _spikes.AsSpan(_stampSegment * _perSegment, _perSegment);
        var rank = Math.Min(spikes.Length, MaxSpikes);
        for (var i = 0; i < Math.Min(spikes.Length, MaxSpikes); i++)
            if (spikes[i].Ms < ms)
            {
                rank = i;
                break;
            }

        if (rank >= spikes.Length) return;
        spikes[rank..^1].CopyTo(spikes[(rank + 1)..]);
        spikes[rank] = new BenchSpike(frame, ms, collected ? LastGc() : default);
        var marks = _marks.AsSpan(_stampSegment * _perSegment * MarksPerSpike, _perSegment * MarksPerSpike);
        marks[(rank * MarksPerSpike)..^MarksPerSpike].CopyTo(marks[((rank + 1) * MarksPerSpike)..]);
        CopyMarks(marks.Slice(rank * MarksPerSpike, MarksPerSpike), root);
    }

    private static BenchGc LastGc()
    {
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var pauses = info.PauseDurations;
        var pauseMs = pauses.Length > 0 ? (float)pauses[0].TotalMilliseconds : 0f;
        return Assert(info.Index >= 0) && Finite(pauseMs) && Assert(info.Generation is >= 0 and <= 2)
            ? new BenchGc(true, info.Index, info.Generation, pauseMs, info.Compacted, info.Concurrent,
                info.PromotedBytes / 1024)
            : default;
    }

    private static void CopyMarks(Span<BenchSpikeMark> top, ProfileEntryRange? root)
    {
        top.Clear();
        if (root?.Marks == null || !Assert(top.Length == MarksPerSpike)) return;
        Rank(top, null, root.Marks);
        if (root.ChildRanges == null) return;
        using var ranges = root.ChildRanges.GetEnumerator(); // struct enumerator: nothing allocated
        for (var i = 0; i < RenderPassStats.MaxRanges && ranges.MoveNext(); i++)
        {
            var (code, range) = ranges.Current;
            if (!NotNull(range)) continue;
            Insert(top, new BenchSpikeMark(code, null, (float)FrameClock.ToMs(range.ElapsedTicks), range.CallCount));
            if (range.Marks != null) Rank(top, code, range.Marks);
        }
    }

    private static void Rank(Span<BenchSpikeMark> top, string? range, Dictionary<string, ProfileEntry> marks)
    {
        if (!Assert(top.Length == MarksPerSpike) || !Assert(marks.Count <= RenderPassStats.MaxMarks)) return;
        using var entries = marks.GetEnumerator();
        for (var i = 0; i < RenderPassStats.MaxMarks && entries.MoveNext(); i++)
        {
            var (code, entry) = entries.Current;
            if (!NotNull(entry)) continue;
            Insert(top, new BenchSpikeMark(range, code, (float)FrameClock.ToMs(entry.ElapsedTicks), entry.CallCount));
        }
    }

    private static void Insert(Span<BenchSpikeMark> top, BenchSpikeMark mark)
    {
        if (!Assert(top.Length is > 0 and <= MarksPerSpike) || !Finite(mark.Ms)) return;
        var slot = Math.Min(top.Length, MarksPerSpike);
        for (var i = 0; i < Math.Min(top.Length, MarksPerSpike); i++)
            if (top[i].Empty || top[i].Ms < mark.Ms)
            {
                slot = i;
                break;
            }

        if (slot >= top.Length) return;
        top[slot..^1].CopyTo(top[(slot + 1)..]);
        top[slot] = mark;
    }
}
