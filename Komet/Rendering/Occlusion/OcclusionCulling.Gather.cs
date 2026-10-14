using Komet.Core;

namespace Komet.Rendering;

// A held call's ranges go into backend staging (Vulkan) or Ranges for upload (GL). Pool places are known ahead, so big
// calls gather per pool on free workers. A pool with fewer ranges than its list leaves empty ones (draw nothing).
internal static unsafe partial class OcclusionCulling
{
    private const int ParallelRanges = 4096, RangesPerHelper = 8192;

    private static readonly Planned[] Plan = new Planned[MaxDraws];
    private static readonly Action<int> GatherJob = GatherOne;
    private static int _planned, _stagedFirst;
    private static Occlusion.Range* _staged;
    private static (double X, double Y, double Z) _eye;

    private readonly record struct Planned(MeshDataPool Pool, int At, int Count);

    // true when they went into backend staging
    private static bool Fill((int First, int Count) ranges, (double X, double Y, double Z) camera)
    {
        if (ranges.Count <= 0 || !Assert(_planned <= MaxDraws) || !Assert(ranges.First + ranges.Count <= MaxRanges))
            return false;
        _staged = (Occlusion.Range*)Gpu.Staged(_ranges, ranges.First * RangeBytes, ranges.Count * RangeBytes);
        (_stagedFirst, _eye) = (ranges.First, camera);
        var parallel = ranges.Count >= ParallelRanges && _planned > 1 && WorkerPool.Free &&
                       WorkerPool.RunFrame(GatherJob, _planned, ranges.Count / RangesPerHelper) ==
                       WorkerPool.FrameResult.Done;
        if (!parallel)
            for (var j = 0; j < Math.Min(_planned, MaxDraws); j++) GatherOne(j); // idempotent: a failed batch runs again
        var staged = _staged != null;
        _staged = null;
        return staged;
    }

    // Into backend staging at first, as Fill does (GPU tests); false when it has none
    internal static bool Stage(ReadOnlySpan<Occlusion.Range> ranges, int first)
    {
        if (ranges.IsEmpty || !Assert(first >= 0 && first + ranges.Length <= MaxRanges)) return false;
        var staged = (Occlusion.Range*)Gpu.Staged(_ranges, first * RangeBytes, ranges.Length * RangeBytes);
        if (staged == null) return false;
        ranges.CopyTo(new Span<Occlusion.Range>(staged, ranges.Length));
        return Assert(ranges.Length > 0);
    }

    private static void GatherOne(int j)
    {
        if (!Index(j, _planned)) return;
        var (pool, at, count) = Plan[j];
        if (!Assert(count >= 0) || !Assert(at + count <= MaxRanges)) return;
        var into = _staged != null
            ? new Span<Occlusion.Range>(_staged + (at - _stagedFirst), count)
            : Ranges.AsSpan(at, count);
        var written = Gather(pool, into, _eye);
        if (written < count) into[Math.Max(written, 0)..].Clear();
        _ = Assert(written <= count);
    }
}
