using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Komet.Core;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Diagnostics;

// Terrain draw cost (GPU time includes waiting for the call's commands); Vulkan terrain fills the GPU rows itself.
internal static partial class RenderCost
{
    public const int Groups = 3; // shadows, opaque, transparent (the liquid depth pre-pass OnRenderBefore, OIT and after OIT)
    private const int MaxCalls = 16, Ring = 4;

    private static readonly string[] GroupKeys = ["shadows", "opaque", "transparent"];
    private static readonly long[] CpuTicks = new long[Groups];
    private static readonly double[] GpuGroupMs = new double[Groups];

    private static readonly int[] Queries = new int[Ring * MaxCalls * 2];
    private static readonly int[] CallGroups = new int[Ring * MaxCalls];
    private static readonly int[] Calls = new int[Ring];

    private static Harmony? _harmony;
    private static ILogger? _logger;
    private static int _depth, _group, _frame;
    private static long _entered, _submitStart;
    private static bool _timing; // the call under way has its first timestamp
    private static bool _vulkan;

    // First true patches the engine, false leaves the patches inert
    public static bool Enabled
    {
        get;
        set
        {
            field = value;
            if (value) Patch();
        }
    }

    public static bool Patched { get; private set; }

    public static long SubmitTicks { get; private set; }
    public static long Draws { get; private set; }
    public static long Ranges { get; private set; }
    public static long TotalTicks => CpuTicks[0] + CpuTicks[1] + CpuTicks[2];

    // One frame three frames back; NaN until read
    public static double GpuMs { get; private set; } = double.NaN;

    public static double FrameGpuMs(double gl) =>
        Komet.Vulkan.VulkanRenderer.FrameGpuMs is var ms && !double.IsNaN(ms) && Assert(ms >= 0) ? ms : gl;

    public static string GroupKey(int group) =>
        Index(group, Groups) && Assert(GroupKeys.Length == Groups) ? GroupKeys[group] : "";

    public static long GroupTicks(int group) =>
        Index(group, Groups) && Assert(CpuTicks[group] >= 0) ? CpuTicks[group] : 0;

    public static double GroupGpuMs(int group) =>
        Index(group, Groups) && Assert(GpuGroupMs.Length == Groups) ? GpuGroupMs[group] : double.NaN;

    public static void Install(Harmony harmony, ILogger logger)
    {
        Clear();
        (_harmony, _logger, Patched) = (null, null, false);
        if (!NotNull(harmony) || !NotNull(logger)) return;
        (_harmony, _logger) = (harmony, logger);
    }

    public static void Clear()
    {
        Enabled = false;
        if (Queries[0] != 0) GL.DeleteQueries(Queries.Length, Queries);
        Array.Clear(Queries);
        Array.Clear(Calls);
        Array.Clear(CpuTicks);
        Array.Fill(GpuGroupMs, double.NaN);
        (_depth, _frame, _timing, _vulkan, _submitStart, GpuMs) = (0, 0, false, false, 0, double.NaN);
        (SubmitTicks, Draws, Ranges) = (0, 0, 0);
        _ = Assert(Queries[^1] == 0) && Assert(Calls[0] == 0);
        ClearThreads();
    }

    private static void Patch()
    {
        if (Patched || _harmony is null || !NotNull(_logger)) return;
        Patched = true;
        var type = typeof(ChunkRenderer);
        var render = AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderMesh),
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);
        string[] stages =
        [
            "OnRenderBefore", nameof(ChunkRenderer.RenderShadow), "OnBeforeRenderOpaque",
            nameof(ChunkRenderer.RenderOpaque), "RenderOIT", "RenderAfterOIT"
        ];
        try
        {
            foreach (var method in stages.Bounded(MaxCalls))
                Around(AccessTools.DeclaredMethod(type, method, [typeof(float)]), nameof(Enter), nameof(Leave));
            if (NotNull(render) && Assert(Il.Binds(render, AccessTools.Method(typeof(RenderCost), nameof(Submitted)))))
                Around(render, nameof(Submitting), nameof(Submitted));
        }
        catch (Exception e) when (e is HarmonyException or ArgumentException or InvalidOperationException
                                      or NotSupportedException)
        {
            _logger.Warning("Komet: the render cost could not patch the engine ({0})", e.Message);
        }
    }

    // First prefix and last postfix: covers every other patch
    private static void Around(MethodInfo? target, string prefix, string postfix)
    {
        if (!NotNull(target) || !NotNull(_harmony)) return;
        var self = typeof(RenderCost);
        _ = NotNull(_harmony.Patch(target,
            new HarmonyMethod(AccessTools.Method(self, prefix)) { priority = Priority.First },
            new HarmonyMethod(AccessTools.Method(self, postfix)) { priority = Priority.Last }));
    }

    private static void Enter(MethodBase __originalMethod)
    {
        if (!Enabled || _depth++ > 0 || !NotNull(__originalMethod)) return;
        var group = __originalMethod.Name switch
        {
            nameof(ChunkRenderer.RenderShadow) => 0,
            nameof(ChunkRenderer.RenderOpaque) or "OnBeforeRenderOpaque" => 1,
            _ => 2
        };
        if (!Index(group, Groups)) return;
        (_group, _entered) = (group, Stopwatch.GetTimestamp());
        var slot = _frame % Ring;
        _timing = Calls[slot] < MaxCalls && !_vulkan;
        if (!_timing) return;
        if (Queries[0] == 0) GL.GenQueries(Queries.Length, Queries);
        var call = slot * MaxCalls + Calls[slot];
        CallGroups[call] = group;
        GL.QueryCounter(Queries[2 * call], QueryCounterTarget.Timestamp);
    }

    // Skipped on exception; EndFrame closes what was left open
    private static void Leave()
    {
        if (!Enabled || _depth == 0 || --_depth > 0 || !Assert(_depth == 0)) return;
        CpuTicks[_group] += Stopwatch.GetTimestamp() - _entered;
        if (!_timing) return;
        var slot = _frame % Ring;
        GL.QueryCounter(Queries[2 * (slot * MaxCalls + Calls[slot]) + 1], QueryCounterTarget.Timestamp);
        Calls[slot]++;
        _timing = false;
    }

    private static void Submitting()
    {
        if (!Enabled || _depth == 0 || !Assert(_depth > 0)) return;
        _submitStart = Stopwatch.GetTimestamp();
        _ = Assert(_submitStart > 0);
    }

    private static void Submitted(int groupCount)
    {
        if (_submitStart == 0) return;
        var now = Stopwatch.GetTimestamp();
        if (Assert(now >= _submitStart) && Assert(groupCount >= 0))
            (SubmitTicks, Draws, Ranges) = (SubmitTicks + now - _submitStart, Draws + 1, Ranges + groupCount);
        _submitStart = 0;
    }

    public static void EndFrame()
    {
        if (!Enabled || !Assert(_frame >= 0)) return;
        if (_depth != 0) (_depth, _timing, _submitStart) = (0, false, 0); // a call threw past its postfix
        _frame++;
        Calls[_frame % Ring] = 0;
        _vulkan = Komet.Vulkan.VulkanRenderer.TimesTerrain || Komet.Vulkan.VulkanRenderer.GlIdle; // the next frame's calls
        _ = Assert(Calls.Length == Ring);
    }

    // Once per HUD interval from the oldest ended frame: one glthread sync, later reads find the queue empty
    public static void Sample()
    {
        if (!Enabled || !Assert(Ring > 1)) return;
        SampleThreads();
        Span<double> ms = stackalloc double[Groups];
        if (Komet.Vulkan.VulkanRenderer.TerrainGpuMs(ms))
        {
            Took(ms);
            return;
        }

        if (_frame < Ring || Queries[0] == 0) return;
        var slot = (_frame + 1) % Ring;
        var calls = Math.Min(Calls[slot], MaxCalls);
        if (calls == 0) return;
        GL.GetQueryObject(Queries[2 * (slot * MaxCalls + calls - 1) + 1], GetQueryObjectParam.QueryResultAvailable,
            out int ready);
        if (!Assert(ready is 0 or 1) || ready == 0) return; // the GPU lags: the read would block
        for (var i = 0; i < Math.Min(calls, MaxCalls); i++)
        {
            var call = slot * MaxCalls + i;
            GL.GetQueryObject(Queries[2 * call], GetQueryObjectParam.QueryResult, out long begin);
            GL.GetQueryObject(Queries[2 * call + 1], GetQueryObjectParam.QueryResult, out long end);
            if (!Assert(end >= begin) || !Index(CallGroups[call], Groups)) return;
            ms[CallGroups[call]] += (end - begin) / 1e6;
        }

        Took(ms);
    }

    private static void Took(ReadOnlySpan<double> ms)
    {
        if (!Assert(ms.Length == Groups)) return;
        ms.CopyTo(GpuGroupMs);
        GpuMs = ms[0] + ms[1] + ms[2];
    }
}
