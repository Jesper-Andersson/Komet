using Komet.Gpu;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// The far cache (Vulkan-only, VulkanRenderer.FarCaching): terrain past Radius is drawn into a layer of its own only now and
// then (Refresh) and laid into the frame in between. The camera's rows pass leaves it the far rows its view holds (rows.comp
// Split); a refreshing frame's far pass (mode 3) draws the far rows of this view, unculled by the pyramid, through Redirect.
internal static partial class OcclusionCulling
{
    // One frame's split: the radius squared; the five planes (normal, D) of the view the cache holds, filled by the refreshing
    // frame's first camera rows pass; Redirect moves the draws into the cache's layer (true) and back (false). One per
    // renderer, begun anew each frame.
    internal sealed class FarCut(double radiusSq, Action<bool> redirect)
    {
        public double RadiusSq { get; } = radiusSq;
        public bool Refresh { get; private set; }
        public Action<bool> Redirect { get; } = redirect;
        public double[] View { get; } = new double[20];
        public bool Planned { get; set; } // View holds this frame's view (a refresh) or the cached one's
        public int Into { get; set; } // far draws handed into the cache this frame

        public FarCut Begin(bool refresh)
        {
            (Refresh, Planned, Into) = (refresh, false, 0);
            Array.Clear(View); // as a new cut's: no planes until planned
            _ = Assert(View.Length == 20) && Assert(RadiusSq > 0);
            return this;
        }
    }

    internal static FarCut? FarCache { get; set; }

    // The planes of a refreshing frame's view, each moved in by Inset blocks: what lies at its very edge is drawn as ever
    private const double Inset = 2;

    // rows.comp's split in the Frustum block (mode 0 and 3); a refresh takes this frame's planes
    private static void FarSplit(Span<double> block, ReadOnlySpan<double> planes, int mode)
    {
        if (FarCache is not { } cut || mode is not (0 or 3) || !Assert(block.Length >= Casters + 20) || !Assert(planes.Length >= 20)) return;
        if (cut.Refresh && !cut.Planned)
        {
            for (var p = 0; p < 5; p++)
            {
                for (var i = 0; i < 3; i++) cut.View[4 * p + i] = planes[4 * p + i];
                cut.View[4 * p + 3] = planes[4 * p + 3] - Inset; // a plane's normal points inward, D in blocks
            }

            cut.Planned = true;
        }

        (block[30], block[31]) = (cut.RadiusSq, cut.Planned ? 1 : 0);
        if (mode == 0) cut.View.CopyTo(block[Casters..(Casters + 20)]);
    }

    // A refreshing frame, after a camera call's draws: its far rows into the cache, every one this view holds
    private static void FarPass(FarCut cut, (int First, int Count) draws, (FrustumCulling Culler, Vec3d Camera) rows, int counters)
    {
        if (!NotNull(cut) || !Assert(draws.Count is > 0 and <= MaxDraws) || !cut.Planned) return;
        Frustum(rows.Culler, rows.Camera, (3, _backFaces));
        Gpu.Zero(counters, FarCounts + draws.First, draws.Count);
        Gpu.Barrier(GpuBarrier.Storage);
        RowsPass(ViewProjection, (0, 0, 0), (1, Keep, FarCounts, FarBase), draws, _callMaxRows);
        cut.Redirect(true);
        try
        {
            Issue(draws, (FarBase, FarCounts), counters);
            cut.Into += draws.Count;
        }
        finally
        {
            cut.Redirect(false);
        }
    }
}
