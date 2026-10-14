using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Core;

// Core's own: the worker pool, the GC settings and PreJit (last, once every patch is in).
// In install order; Core's Features places them among the other modules'.
internal static class CoreFeatures
{
    internal static readonly Feature[] All =
    [
        new("WorkerPool")
        {
            Knobs = [new("WorkerThreads", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => WorkerPool.Wanted,
                v => WorkerPool.Wanted = v) { Weighed = true }],
            Stop = _ => StopPool(),
            Probe = () => WorkerPool.FrameOff || WorkerPool.BackgroundOff ? Failed : Active
        },
        new("GcLatency")
        {
            Install = _ => GcLatency.Install(),
            Knobs = [Switch("GcLatency", "misc", "garbage", () => GcLatency.Enabled, on => GcLatency.Enabled = on)
                with { Weighed = true }],
            Stop = _ => GcLatency.Stop()
        },
        // heap compacted while nobody plays (paused or unfocused), returning what packing and trimming freed
        new("GcCompact")
        {
            Install = c => GcCompact.Install(c.Api),
            Knobs = [Switch("GcCompact", "misc", "garbage", () => GcCompact.Enabled, on => GcCompact.Enabled = on)],
            Stop = _ => GcCompact.Stop()
        },
        // last: every patch above is in place and left alone
        new("PreJit")
        {
            Install = c => PreJit.Start(c.Logger),
            Stage = FeatureStage.Last,
            Knobs = [Switch("PreJit", "misc", "garbage", () => PreJit.Enabled, on => PreJit.Enabled = on)],
            Stop = _ => PreJit.Stop()
        }
    ];

    // One still in a job after WorkerPool.JoinMs fails the first assertion
    private static void StopPool() => _ = Assert(WorkerPool.Stop()) && Assert(WorkerPool.Running == 0);
}
