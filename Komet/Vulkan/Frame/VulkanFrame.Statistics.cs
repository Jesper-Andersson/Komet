namespace Komet.Vulkan;

// Pipeline statistics of draws posted in the frame's counted section (opaque terrain), every Every-th frame: one query
// per run inside a rendering, ended before any other draw, clear, call or rendering end
internal sealed unsafe partial class VulkanFrame
{
    public const int Statistics = 4;
    private const int QueriesPerFrame = 64, Every = 4, ResultBytes = 8 * Statistics, MaxResults = Statistics * QueriesPerFrame;

    private readonly double[] _statisticTotals = new double[Statistics];
    private readonly double[] _lastStatistics = [double.NaN, double.NaN, double.NaN, double.NaN];
    private ulong _statistics;
    private long _counting = -1, _countedFrames; // the query open now, -1 for none
    private double _countedPixels = double.NaN; // the area of the rendering the last counted run drew into

    // The section whose draws are counted, -1 for none
    public int Counted { get; set; } = -1;

    // Last counted frame in Occlusion's order: VS, FS invocations, clipping primitives in, out; NaN before
    public double LastStatistic(int at) => Index(at, Statistics) ? _lastStatistics[at] : double.NaN;

    public double CountedPixels => _countedPixels;

    // Begin: the counted frame's queries reset in the first segment's setup, before any of them is begun
    private void CountingBegins(Segment segment)
    {
        if (_slot is not { Sampled: true, Reset: false } slot || !NotNull(segment) || !Assert(_counting < 0)) return;
        if (_statistics == 0 && !MadeStatistics()) return;
        VkApi.CmdResetQueryPool(segment.Setup, _statistics, (uint)(slot.Index * QueriesPerFrame), QueriesPerFrame);
        slot.Reset = Assert(slot.Queries == 0);
    }

    // Post: a counted draw opens a query unless one is open; anything but a counted draw or dynamic state closes it
    private void Counting(Op op)
    {
        var counted = op.Kind == OpKind.Draw && Counted >= 0 && _section == Counted;
        if (_counting >= 0 && !counted && op.Kind is not (OpKind.Viewport or OpKind.Scissor or OpKind.LineWidth or
                OpKind.Timestamp))
        {
            Enqueue(new Op { Kind = OpKind.EndQuery, Pipeline = _statistics, Count = (uint)_counting });
            _counting = -1;
        }

        if (!counted || _counting >= 0 || _slot is not { Reset: true } slot || slot.Queries >= QueriesPerFrame ||
            _rendering is not { } target) return;
        _counting = slot.Index * QueriesPerFrame + slot.Queries++;
        _countedPixels = (double)target.Width * target.Height;
        Enqueue(new Op { Kind = OpKind.BeginQuery, Pipeline = _statistics, Count = (uint)_counting });
    }

    private bool MadeStatistics()
    {
        if (!_device.PipelineStatistics || !Assert(_statistics == 0)) return false;
        _statistics = _device.QueryPool(Vk.QueryPipelineStatistics, QueriesPerFrame * Slots, Vk.CountedStatistics);
        return _statistics != 0 && Assert(_device.PipelineStatistics);
    }

    // Next, once the slot's fence passed: its queries summed; then whether the frame begun in it is counted
    private void CountedFrame(Slot slot, bool finished)
    {
        if (!NotNull(slot)) return;
        var count = slot.Queries;
        (slot.Queries, slot.Reset) = (0, false);
        // only for the HUD (and the report while counting): queries around the opaque pass cost on every fourth frame
        slot.Sampled = Measuring && Core.Counting.Hud && _device.PipelineStatistics && Number % Every == 0;
        if (count == 0 || !finished || _statistics == 0 || !Assert(count <= QueriesPerFrame)) return;
        var results = stackalloc ulong[MaxResults];
        if (VkApi.GetQueryPoolResults(_device.Handle, _statistics, (uint)(slot.Index * QueriesPerFrame), (uint)count,
                (nuint)(count * ResultBytes), results, ResultBytes, Vk.Result64) != Vk.Success) return;
        Span<double> sums = stackalloc double[Statistics];
        for (var i = 0; i < Math.Min(Statistics * count, MaxResults); i++) sums[i % Statistics] += results[i];
        // written vertex invocations, clipping invocations, clipping primitives, fragment invocations
        (_lastStatistics[0], _lastStatistics[1], _lastStatistics[2], _lastStatistics[3]) = (sums[0], sums[3], sums[1], sums[2]);
        for (var i = 0; i < Statistics; i++) _statisticTotals[i] += _lastStatistics[i];
        _countedFrames++;
        _ = Assert(_countedFrames > 0);
    }

    private string StatisticsReport()
    {
        if (_countedFrames == 0 || !Assert(_statisticTotals.Length == Statistics)) return "";
        var frames = (double)_countedFrames;
        var (vertices, fragments) = (_statisticTotals[0] / frames, _statisticTotals[1] / frames);
        var line = $"; the opaque terrain in the pipeline a frame ({_countedFrames} frames counted): {vertices / 1e6:0.00} M " +
                   $"vertex shader invocations, {_statisticTotals[2] / frames / 1e6:0.00} M primitives, " +
                   $"{_statisticTotals[3] / frames / 1e6:0.00} M after clipping, {fragments / 1e6:0.00} M fragment shader " +
                   $"invocations ({fragments / Math.Max(1, _countedPixels):0.00} a pixel)";
        Array.Clear(_statisticTotals);
        _countedFrames = 0;
        return line;
    }
}
