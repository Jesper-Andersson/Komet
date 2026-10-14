using System.Diagnostics;
using System.Runtime;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Diagnostics;

// Deltas between frame starts. Outside: end to next start (OpenTK input/window events, no engine mark). NaN:
// unmeasured.
internal readonly record struct FrameRecord(
    long Index, double DtMs, double GcMs, double OutsideMs, double JitMs, double RunQueueMs, ProfileEntryRange? Root);

// A gen2 counts in all three generations, as GC.CollectionCount does
internal readonly record struct FrameCounters(int Gen0, int Gen1, int Gen2, long Allocated, long MainAllocated)
{
    public FrameCounters Since(in FrameCounters before) =>
        Assert(Gen0 >= before.Gen0 && Gen1 >= before.Gen1 && Gen2 >= before.Gen2) &&
        Assert(Allocated >= before.Allocated && MainAllocated >= before.MainAllocated)
            ? new FrameCounters(Gen0 - before.Gen0, Gen1 - before.Gen1, Gen2 - before.Gen2,
                Allocated - before.Allocated, MainAllocated - before.MainAllocated)
            : default;
}

// Called inside window_RenderFrame's prefix: must not throw
internal interface IFrameSink
{
    void Frame(in FrameRecord frame, in FrameCounters counters);
}

// Patches window_RenderFrame (Begin..End, limiter sleep, SwapBuffers): read at Ortho, a GC in the ~85 % before would
// hit the previous dt and PrevRootEntry could be OnMouseDown/Up's profile. The postfix takes the root before input
// runs.
internal static class FrameClock
{
    internal static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    private static long _start, _end;
    private static TimeSpan _pause, _jit;
    private static double _runDelayNs;
    private static ProfileEntryRange? _root, _seen;
    private static FrameCounters _counters;
    private static bool _counted;

    public static bool Stats { get; set; }

    public static IFrameSink? Sink { get; set; }

    public static FrameRecord Last { get; private set; }

    public static long FrameStart { get; private set; }

    // Readers compare it to see whether Last is new; counts only while Stats or a Sink is on
    public static long Completed { get; private set; }

    // HUD or not
    public static long Frames { get; private set; }

    public static void Install(Harmony harmony)
    {
        (_start, _end, _root, _seen, Last, Completed, Sink, _counted, FrameStart, Frames) = (0, 0, null, null, default, 0, null, false, 0, 0);
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        if (!NotNull(harmony) || !NotNull(frame) || !Assert(frame.GetParameters().Length == 1)) return;
        _ = NotNull(harmony.Patch(frame, new HarmonyMethod(Begin), new HarmonyMethod(End)));
    }

    // The previous frame ends here; a skipped postfix leaves _end behind _start (dt known, split not).
    internal static void Begin()
    {
        (FrameStart, Frames) = (Stopwatch.GetTimestamp(), Frames + 1);
        Komet.Tessellation.TessGovernor.Frame(); // every frame, HUD or not: the tessellation workers follow the main thread
        var sink = Sink;
        if (!Stats && sink is null)
        {
            (_start, _counted) = (0, false);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var (pause, jit, delay) = (GC.GetTotalPauseDuration(), JitInfo.GetCompilationTime(true), SchedStat.MainWaitNs() is >= 0 and var ns ? ns : double.NaN);
        var counters = sink is null
            ? default
            : new FrameCounters(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalAllocatedBytes(), GC.GetAllocatedBytesForCurrentThread());
        // Cumulative: a step back is a broken reading, the frame is dropped. delay may be NaN (no schedstat).
        if (_start != 0 && Assert(now >= _start) && Assert(pause >= _pause) && Assert(jit >= _jit) &&
            Assert(double.IsNaN(delay) || double.IsNaN(_runDelayNs) || delay >= _runDelayNs))
        {
            Last = new FrameRecord(Completed, (now - _start) * TickMs, (pause - _pause).TotalMilliseconds,
                _end >= _start ? (now - _end) * TickMs : double.NaN, (jit - _jit).TotalMilliseconds,
                (delay - _runDelayNs) / 1e6, _root);
            Completed++;
            if (sink is not null && _counted) sink.Frame(Last, counters.Since(_counters));
        }

        (_start, _end, _root, _pause, _jit, _runDelayNs) = (now, 0, null, pause, jit, delay);
        (_counters, _counted) = (counters, sink is not null);
    }

    // A root seen before is stale (profiler off this frame).
    internal static void End()
    {
        if (_start == 0) return;
        _end = Stopwatch.GetTimestamp();
        if (!Assert(_end >= _start)) return;
        var profiler = ScreenManager.FrameProfiler;
        var root = profiler?.PrevRootEntry;
        _root = profiler is { Enabled: true } && root != null && !ReferenceEquals(root, _seen) ? root : null;
        _seen = root;
        if (_root != null && !Assert(_root.ElapsedTicks >= 0)) _root = null;
    }

    // Engine profiler ranges can go negative across a long stall (seen after a 2.7 s compaction)
    internal static double ToMs(long ticks) =>
        ticks > 0 && Assert(Stopwatch.Frequency > 0) ? ticks * 1000.0 / Stopwatch.Frequency : 0;
}
