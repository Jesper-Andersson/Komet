using System.Diagnostics;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Tessellation;

// Pool threads tessellating at once, set every Window from the main thread's wait for a core (schedstat): any wait is
// render time lost to workers. Eight on six cores: 20-36 ms waits a frame during a load; fixed two: minutes of backlog.
internal static class TessGovernor
{
    private const long WindowMs = 250;
    private const int GrowAt = 64;

    // Main thread core wait, ms/frame: below Calm workers may grow, from Pressed shrink, from Severe halve; times Scale
    internal const double Calm = 0.3, Pressed = 1.0, Severe = 4.0;

    private static long _windowStart, _frames, _lastDelayNs = -1;
    private static int _limit = 1;

    // Pool threads that may tessellate now; the ceiling while nothing was measured (no schedstat, or no frame yet)
    public static int Limit(int ceiling) =>
        Assert(ceiling >= 0) && Measured ? Math.Min(Volatile.Read(ref _limit), ceiling) : ceiling;

    private static bool Measured { get; set; }

    // The ceiling TessWorkers allows at the moment, set on the engine's tessellation thread
    public static int Ceiling
    {
        get => Volatile.Read(ref _ceiling);
        set => Volatile.Write(ref _ceiling, Assert(value >= 0) ? Math.Clamp(value, 0, WorkerPool.MaxThreads) : 0);
    }

    private static int _ceiling = 2;

    // TessWorkers.Priority as a factor on the thresholds (divisor on GrowAt): 0.5 at 0, 1 at default 25, 2.5 at 100
    public static double Scale
    {
        get => Volatile.Read(ref _scale);
        set => Volatile.Write(ref _scale, Finite(value) && Assert(value > 0) ? value : 1);
    }

    private static double _scale = 1;

    // Main thread, each frame start (FrameClock's prefix): count frames; once per Window read schedstat
    public static void Frame()
    {
        _frames++;
        if (!Assert(_frames > 0)) return;
        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        if ((now - _windowStart) * 1000 < WindowMs * Stopwatch.Frequency) return;
        var delay = SchedStat.MainWaitNs();
        var frames = _frames;
        (_windowStart, _frames) = (now, 0);
        if (delay < 0 || !Assert(frames > 0)) return;
        var previous = _lastDelayNs;
        _lastDelayNs = delay;
        if (previous < 0 || !Assert(delay >= previous)) return;
        var waitMs = (delay - previous) / 1e6 / frames;
        var ceiling = Ceiling;
        Volatile.Write(ref _limit, Decide(Volatile.Read(ref _limit), ceiling, waitMs,
            (TessSchedule.Backlog, WorkerPool.InBackground), Scale));
        Measured = true;
    }

    internal static int Decide(int limit, int ceiling, double waitMs, (int Backlog, int Busy) work, double scale = 1)
    {
        if (!Assert(ceiling >= 0) || !Assert(work.Busy >= 0) || !Assert(scale > 0) || ceiling == 0) return 0;
        limit = Math.Clamp(limit, 1, ceiling);
        if (!Finite(waitMs)) return limit;
        if (waitMs >= Severe * scale) return Math.Max(1, limit / 2);
        if (waitMs >= Pressed * scale) return Math.Max(1, limit - 1);
        if (work.Backlog == 0) return Math.Max(1, limit - 1);
        var grow = waitMs < Calm * scale && work.Backlog > GrowAt / scale && work.Busy >= limit;
        return grow ? Math.Min(ceiling, limit + 1) : limit;
    }

    public static void Reset()
    {
        (_windowStart, _frames, _lastDelayNs, Measured) = (0, 0, -1, false);
        Volatile.Write(ref _limit, 1);
        _ = Assert(!Measured);
    }
}
