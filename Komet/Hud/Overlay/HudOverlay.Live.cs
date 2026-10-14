using Komet.Core;
using Komet.Diagnostics;
using Komet.Rendering;

namespace Komet.Hud;

// Snapshot taken each interval before collectors reset: views draw from it anytime, never a half interval.
internal sealed class HudLive
{
    public const int MaxPasses = 9, MaxThreads = 8;
    public double Fps = double.NaN, Low1 = double.NaN, Low01 = double.NaN, AvgMs = double.NaN,
        P99 = double.NaN, GpuMs = double.NaN, ModsMs, RunQueueMs = double.NaN, CulledPercent = double.NaN;

    public double Under8, Under12, Under25, Over25; // shares of the frame history, 0..100
    public int DrawCalls, Triangles;
    public double TotalMs;
    public readonly (string Key, double Ms)[] Passes = new (string, double)[MaxPasses];
    public int PassCount;
    public readonly (string Name, double Busy, double MsPerFrame)[] Threads = new (string, double, double)[MaxThreads];
    public int ThreadCount;
    public (double Draws, double Left, double Segments, long MemoryMb, string[] Closers) Vulkan = (0, 0, 0, 0, []);
    public double WorkerFrame = double.NaN, WorkerTess = double.NaN; // percent of one thread, per worker
    public double Locks = double.NaN;
}

internal sealed partial class HudOverlay
{
    private readonly HudLive _live = new();
    private readonly float[] _sortedHistory = new float[HudCanvas.GraphFrames * 4];
    private System.Func<double>? _locks, _workerFrame, _workerTess;
    private readonly System.Func<double>?[] _threadBusy = new System.Func<double>?[RenderCost.Kinds], _threadMs = new System.Func<double>?[RenderCost.Kinds];

    private void TakeLive()
    {
        var l = _live;
        (l.Fps, l.Low1, l.Low01, l.AvgMs) = (_frames.Fps, _frames.Low1Fps, _frames.Low01Fps, _frames.AverageMs);
        (l.GpuMs, l.DrawCalls, l.Triangles) = (RenderCost.FrameGpuMs(_gpu.Ms), _frames.DrawCallsPerFrame, _frames.RenderedTriangles);
        (l.ModsMs, l.TotalMs, l.RunQueueMs) = (ModsTotal(), _passes.AverageTotalMs, FrameClock.Last.RunQueueMs);
        l.CulledPercent = OcclusionCulling.Enabled ? OcclusionCulling.CulledPercent : double.NaN;
        History(l);
        l.PassCount = 0;
        for (var i = 0; i < Math.Min(RenderPassStats.Count, HudLive.MaxPasses); i++)
            if (_passes.Key(i) is { Length: > 0 } key && _passes.AverageMs(i) > 0.005)
                l.Passes[l.PassCount++] = (key, _passes.AverageMs(i));
        Threads(l);
        l.Vulkan = Komet.Vulkan.VulkanRenderer.Snapshot();
        _ = Assert(l.PassCount <= HudLive.MaxPasses) && Assert(l.ThreadCount <= HudLive.MaxThreads);
    }

    private void History(HudLive l)
    {
        if (!NotNull(l) || !Assert(_sortedHistory.Length > 0)) return;
        var n = Math.Min(_frames.Recorded, Math.Min(_frames.HistoryLength, _sortedHistory.Length));
        if (n == 0)
        {
            (l.P99, l.Under8, l.Under12, l.Under25, l.Over25) = (double.NaN, 0, 0, 0, 0);
            return;
        }

        var first = _frames.HistoryLength - n;
        for (var i = 0; i < Math.Min(n, HudCanvas.GraphFrames * 4); i++) _sortedHistory[i] = _frames.HistoryMs(first + i);
        var sorted = _sortedHistory.AsSpan(0, n);
        sorted.Sort();
        l.P99 = FrameStats.Percentile(sorted, 990);
        int a = 0, b = 0, c = 0;
        foreach (var ms in sorted.Bounded(_sortedHistory.Length))
            if (ms < 8) a++;
            else if (ms < 12) b++;
            else if (ms < 25) c++;
        (l.Under8, l.Under12, l.Under25, l.Over25) = (100.0 * a / n, 100.0 * b / n, 100.0 * c / n, 100.0 * (n - a - b - c) / n);
        _ = Assert(a + b + c <= n);
    }

    private void Threads(HudLive l)
    {
        if (!NotNull(l)) return;
        l.ThreadCount = 0;
        for (var k = 0; k < RenderCost.Kinds; k++)
        {
            var kind = k;
            _threadBusy[k] ??= PerSecond(() => RenderCost.ThreadNs(kind), 1e-7);
            _threadMs[k] ??= PerFrame(() => RenderCost.ThreadNs(kind), 1e-6);
            var (busy, ms) = (_threadBusy[k]!(), _threadMs[k]!());
            if (RenderCost.Seen(k) && l.ThreadCount < HudLive.MaxThreads)
                l.Threads[l.ThreadCount++] = (HudText.Cached("hud-rc-", RenderCost.KindKey(k)), busy, ms);
        }

        _workerFrame ??= PerSecond(static () => WorkerPool.FrameTicks * FrameClock.TickMs, 0.1);
        _workerTess ??= PerSecond(static () => WorkerPool.BackgroundTicks * FrameClock.TickMs, 0.1);
        _locks ??= PerSecond(() => Monitor.LockContentionCount);
        var running = Math.Max(1, WorkerPool.Running);
        (l.WorkerFrame, l.WorkerTess, l.Locks) = (_workerFrame() / running, _workerTess() / running, _locks());
        _ = Assert(running > 0);
    }
}
