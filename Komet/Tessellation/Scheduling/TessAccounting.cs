using System.Diagnostics;
using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

internal enum TessBucket
{
    Full,
    Edge, // index | long.MinValue in the queue: the six faces again, the centre kept
    PriorityFull, // near the player or a block edit: dirtyChunksPriority
    PriorityEdge,
    Skipped, // no chunk or an Empty one: TesselateChunk returns before any lock or unpack
    Requeued // not loaded from the server yet, or a RetryTesselationException: back into the queue it came from
}

// Time per tessellation pass for the HUD and bench (engine HUD: edge-only share only). TesselateChunk is the only way
// into the tesselator, so its prefix and postfix time every pass on any thread; a call is a pass once it reached
// NowProcessChunk, counted per thread (RuntimeStats.chunksTesselatedTotal is shared). The main thread diffs the totals.
internal static class TessAccounting
{
    private const int Buckets = 6, PassBuckets = 4, Zero = 2 * Buckets, ZeroTicks = Zero + 1;

    // [bucket] passes, [Buckets + bucket] ticks, then the zero passes
    private static readonly Tally Counts = new(ZeroTicks + 1);

    [ThreadStatic] private static int _entered; // NowProcessChunk calls on this thread, wrapping

    public static bool Installed { get; private set; }

    public static long ZeroPasses => Counts.Total(Zero);

    public static double ZeroMs => Counts.Total(ZeroTicks) * FrameClock.TickMs;

    public static void Install(Harmony harmony)
    {
        Installed = false;
        Clear();
        var target = AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager),
            nameof(ChunkTesselatorManager.TesselateChunk));
        var process = AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.NowProcessChunk));
        HarmonyMethod begin = new(typeof(TessAccounting), nameof(Begin)),
            end = new(typeof(TessAccounting), nameof(End));
        if (!NotNull(harmony) || !NotNull(target) || !NotNull(process) || !Assert(target.ReturnType == typeof(int)) ||
            !Assert(Il.Binds(target, begin.method)) || !Assert(Il.Binds(target, end.method))) return;
        _ = NotNull(harmony.Patch(process, new HarmonyMethod(Entered) { priority = Priority.First }));
        _ = NotNull(harmony.Patch(target, begin, end));
        Installed = true;
    }

    public static void Clear() => Counts.Clear();

    // A requeue after the count moved is a RetryTesselationException: the pass is thrown away and the chunk comes again
    public static TessBucket Classify(bool processed, bool requeue, bool priority, bool edge)
    {
        if (requeue) return TessBucket.Requeued;
        if (!processed) return TessBucket.Skipped;
        return (priority, edge) switch
        {
            (true, true) => TessBucket.PriorityEdge,
            (true, false) => TessBucket.PriorityFull,
            (false, true) => TessBucket.Edge,
            _ => TessBucket.Full
        };
    }

    private static void Begin(out Pass __state)
    {
        __state = new Pass(Stopwatch.GetTimestamp(), _entered);
        _ = Assert(__state.Start > 0);
    }

    // Before any prefix that may skip the work (OccludedChunks): the pass got past TesselateChunk's early exits
    private static void Entered() => _entered = unchecked(_entered + 1);

    private static void End(bool priority, bool skipChunkCenter, ref bool requeue, int __result, Pass __state)
    {
        var ticks = Stopwatch.GetTimestamp() - __state.Start;
        if (!Counting.On || !Assert(__state.Start > 0) || !Assert(ticks >= 0)) return;
        var bucket = Classify(_entered != __state.Entries, requeue, priority, skipChunkCenter);
        // The engine's early exits return 0
        _ = Assert(__result == 0 || bucket is not (TessBucket.Skipped or TessBucket.Requeued));
        Record(bucket, ticks, __result == 0);
    }

    internal static void Record(TessBucket bucket, long ticks, bool zeroVertices)
    {
        var b = (int)bucket;
        if (!Index(b, Buckets) || !Assert(ticks >= 0)) return;
        Counts.Add(b, 1);
        Counts.Add(Buckets + b, ticks);
        if (!zeroVertices || bucket is not (TessBucket.Full or TessBucket.PriorityFull)) return;
        Counts.Add(Zero, 1);
        Counts.Add(ZeroTicks, ticks);
    }

    // The benchmark's view: everything since the world loaded, of the passes only (no skipped or requeued calls)
    public static TessTotals Totals()
    {
        long passes = 0, ticks = 0;
        for (var b = 0; b < PassBuckets; b++)
            (passes, ticks) = (passes + Counts.Total(b), ticks + Counts.Total(Buckets + b));
        var edge = Counts.Total((int)TessBucket.Edge) + Counts.Total((int)TessBucket.PriorityEdge);
        return Assert(passes >= 0) && Assert(ticks >= 0) ? new TessTotals(passes, ticks, edge) : default;
    }

    public static long Count(TessBucket bucket)
    {
        var b = (int)bucket;
        return Index(b, Buckets) ? Counts.Total(b) : 0;
    }

    internal readonly record struct Pass(long Start, int Entries);
}

internal readonly record struct TessTotals(long Passes, long Ticks, long Edge)
{
    public double Ms => Ticks * FrameClock.TickMs;

    public TessTotals Since(TessTotals earlier) => Assert(Passes >= earlier.Passes) && Assert(Ticks >= earlier.Ticks)
        ? new TessTotals(Passes - earlier.Passes, Ticks - earlier.Ticks, Edge - earlier.Edge)
        : default;
}
