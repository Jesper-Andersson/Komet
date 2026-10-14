using System.Diagnostics;
using Komet.Core;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Below ParallelRows the hand-off costs more than it saves. _pass stamps the Render call a mirror's result belongs to.
internal static partial class FrustumSweep
{
    private static readonly Action<int> Job = CullJob;

    // Per job: pool, mirror and manager within its batch
    private static MeshDataPool?[] _jobPools = [];
    private static Mirror[] _jobMirrors = [];
    private static int[] _jobManager = [];
    private static int _jobCount, _pass;
    private static FrustumCulling? _jobCuller;
    private static EnumFrustumCullMode _jobMode;
    private static bool _parallel = true; // the Render patch is in

    internal static int ParallelRows { get; set; } = 8192;
    internal static long ParallelCalls { get; private set; } // calls the workers culled

    // Mirrors update on the main thread (engine lists, shared relayout budget). Mini-dimension pools stay the engine's.
    // Whatever cannot use a batch ends it first (Stage).
    internal static void Precull(MeshDataPoolManager __instance, EnumFrustumCullMode frustumCullMode)
    {
        _pass++;
        if (!Enabled || !_parallel || frustumCullMode == EnumFrustumCullMode.NoCull || WorkerPool.Running == 0 ||
            WorkerPool.FrameOff || OcclusionCulling.Bypassing)
        {
            EndStage();
            return;
        }

        var (pools, culler) = (Pools(__instance), Culler(__instance));
        if (NotNull(pools) && pools.Count == 0) return; // a pass without pools: nothing to cull, nothing a batch holds
        // before the player exists (world load) the engine's loop culls
        if (!NotNull(pools) || !NotNull(culler) ||
            (frustumCullMode != EnumFrustumCullMode.CullInstant && PlayerPos(culler) is null) ||
            !Usable(culler, frustumCullMode))
        {
            EndStage();
            return;
        }

        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        if (!Staged(__instance, pools, culler, frustumCullMode)) Alone(pools, culler, frustumCullMode);
        if (start != 0) Ticks += Stopwatch.GetTimestamp() - start;
    }

    private static void Alone(List<MeshDataPool> pools, FrustumCulling culler, EnumFrustumCullMode frustumCullMode)
    {
        _jobCount = 0;
        var rows = Collect(pools, 0);
        if (rows < ParallelRows || _jobCount < 2)
        {
            Tally();
            return;
        }

        (_jobCuller, _jobMode) = (culler, frustumCullMode);
        var result = WorkerPool.RunFrame(Job, _jobCount, rows / RowsPerHelper - 1);
        if (result == WorkerPool.FrameResult.Done) ParallelCalls++;
        else if (result == WorkerPool.FrameResult.Failed && Assert(WorkerPool.LastError is not null))
        {
            _pass++; // no half-finished result is used; the engine culls every pool of this call
            _logger?.Error(
                "Komet: frustum culling on the worker threads failed, this frame culls on the main thread{0}: {1}",
                WorkerPool.FrameOff ? " and so does every later one" : "", WorkerPool.LastError);
        }

        Tally();
    }

    // Deferred: the job updates each mirror itself (Update), rows are the list counts.
    private static int Collect(List<MeshDataPool> pools, int manager, bool deferred = false)
    {
        var (rows, n) = (0, Math.Min(pools.Count, MaxPools));
        if (!Index(manager, MaxStage) || !Assert(_jobCount + n <= MaxJobs)) return 0;
        if (_jobPools.Length < _jobCount + n)
        {
            var size = Capacity(_jobCount + n, _jobPools.Length, int.MaxValue);
            Array.Resize(ref _jobPools, size);
            Array.Resize(ref _jobMirrors, size);
            Array.Resize(ref _jobManager, size);
            _ = Assert(_jobPools.Length >= _jobCount + n) && Assert(_jobManager.Length == _jobMirrors.Length);
        }

        for (var i = 0; i < Math.Min(n, MaxPools); i++)
        {
            var pool = pools[i];
            if (pool is null || Dimension(pool) == 1) continue;
            var (slot, locations) = (SlotOf(pool), Locations(pool));
            if (slot == Foreign || locations is null || locations.Count > MaxLocations) continue;
            var m = deferred ? Owned(slot, locations) : MirrorOf(slot, locations);
            // a new mirror takes its list here, so a second list under one slot gets its own mirror
            if (deferred && m is { Owner: null }) m = Update(m, locations);
            if (m is null || (!deferred && !ReferenceEquals(m.Owner, locations))) continue;
            (_jobPools[_jobCount], _jobMirrors[_jobCount], _jobManager[_jobCount]) = (pool, m, manager);
            _jobCount++;
            rows += deferred ? locations.Count : m.Length;
        }

        return rows;
    }

    private static void CullJob(int j)
    {
        if (!Index(j, _jobCount)) return;
        var (pool, m, culler) = (_jobPools[j], _jobMirrors[j], _jobCuller);
        if (pool is null || culler is null) return;
        var groups = Swept(m, culler, _jobMode, pool.indicesStartsByte, pool.indicesSizes, out var rendered);
        if (groups < 0) return;
        (pool.indicesGroupsCount, pool.RenderedTriangles, pool.AllocatedTris) = (groups, rendered, m.AllocatedTris);
        (m.Groups, m.Pass) = (groups, _pass);
    }

    // Skipped rows count for the HUD where drawn: here after Alone's batch, in Stamp for stage batches
    private static void Tally(bool drawn = true)
    {
        for (var j = 0; j < Math.Min(_jobCount, MaxJobs); j++)
        {
            if (Counting.Hud && drawn) Skipped += _jobMirrors[j].SkippedRows;
            (_jobMirrors[j].SkippedRows, _jobPools[j]) = (0, null);
        }

        (_jobCount, _jobCuller) = (0, null);
    }

    // Results stamped in this call are never read by a later one
    internal static void Culled() => _pass++;
}
