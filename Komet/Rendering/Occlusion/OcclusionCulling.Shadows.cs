using Komet.Gpu;
using Vintagestory.API.MathTools;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Shadow passes cull from the same rows, without occlusion, while Vulkan draws them. Draws (MaxDraws on), commands (ShadowBase on), counts (after pass 1) have
// their own place so camera passes never overwrite them. Casters culled by the camera view (ShadowView) when made.
internal static partial class OcclusionCulling
{
    private static bool _shadowing, _shadowCall, _casting;
    private static int _shadowMode, _shadowRanges, _shadowDraws = MaxDraws, _shadowFrame = -1;
    private static Vec3d? _shadowCamera;

    // Under Vulkan: whether it takes GL's draws now; counted draws have no GL fallback, so only then is a call held
    internal static Func<bool>? Takes { get; set; }

    private static void ShadowOpening()
    {
        _ = Assert(!_holding);
        _shadowing = Active && RowsEnabled && VulkanDraws && Ready() && _rowsReady;
        _casting = _shadowing && ShadowView.Casting;
        _ = Assert(!_casting || _shadowing);
    }

    private static void ShadowClosing()
    {
        _ = Assert(!_holding);
        (_shadowing, _shadowCall, _casting) = (false, false, false);
    }

    private static void HoldingShadow(MeshDataPoolManager manager, Vec3d playerpos, string origin, EnumFrustumCullMode mode)
    {
        var pass = mode switch
        {
            EnumFrustumCullMode.CullInstantShadowPassNear => 1,
            EnumFrustumCullMode.CullInstantShadowPassFar => 2,
            _ => 0
        };
        if (pass == 0 || !NotNull(manager) || !NotNull(playerpos) || Pools(manager) is not { } pools) return;
        if (VulkanDraws && Takes?.Invoke() != true) return; // e.g. DistantShadows' own map: OpenGL's, culled by the CPU
        if (_shadowFrame != _frame) (_shadowRanges, _shadowDraws, _shadowFrame) = (0, MaxDraws, _frame);
        var locations = Scan(pools);
        if (_shadowRanges + locations > MaxRanges || _shadowDraws + pools.Count > 2 * MaxDraws ||
            !Rowed(Culler(manager), pools, true)) return;
        (_holding, _rowsCall, _shadowCall, _shadowCamera, _shadowMode, _origin) =
            (true, true, true, playerpos, pass, origin);
        Held.Clear();
        _ = Assert(_shadowDraws >= MaxDraws);
    }

    private static void IssuingShadow(FrustumCulling? culler, List<MeshDataPool> pools)
    {
        if (culler is null || _shadowCamera is not { } camera || !NotNull(pools)) return;
        var (first, cursor) = (_shadowRanges, _shadowRanges);
        var draws = Lay(pools, camera, ref cursor, (_shadowDraws, 2 * MaxDraws), 1);
        if (draws > _shadowDraws)
            CullShadow((first, cursor - first), (_shadowDraws, draws - _shadowDraws), culler, camera);
        _ = Assert(_shadowRanges <= MaxRanges);
    }

    private static void CullShadow((int First, int Count) ranges, (int First, int Count) draws, FrustumCulling culler,
        Vec3d camera)
    {
        if (!Assert(draws.First >= MaxDraws && draws.First + draws.Count <= 2 * MaxDraws) ||
            !Assert(ranges.First + ranges.Count <= MaxRanges)) return;
        var counters = CounterBuffers[_frame % Ring];
        UploadRows(draws, culler, camera, (_shadowMode, false));
        Prime(draws, counters, both: false);
        BindRows(counters);
        RowsPass(ViewProjection, (0, 0, 0), (1, Keep, StatUints + MaxDraws, ShadowBase), draws, _callMaxRows);
        Issue(draws, (ShadowBase, StatUints + MaxDraws), counters);
        (_shadowRanges, _shadowDraws) = (ranges.First + ranges.Count, draws.First + draws.Count);
        Gpu.Texture(DepthPyramid.Unit, 0);
    }

    // One shadow call as its own frame (GPU tests), casters culled by the view when given
    internal static bool RunShadow(List<MeshDataPool> pools, FrustumCulling culler, Vec3d camera, EnumFrustumCullMode mode,
        FrustumCulling? view = null)
    {
        if (!NotNull(pools) || !NotNull(culler) || !Ready() || !_rowsReady) return false;
        BeginFrame(true);
        _shadowing = RowsEnabled;
        ShadowView.Use(view);
        _casting = view is not null;
        var manager = (MeshDataPoolManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(MeshDataPoolManager));
        (Pools(manager), Culler(manager)) = (pools, culler);
        HoldingShadow(manager, camera, "origin", mode);
        var held = _holding;
        foreach (var pool in pools.Bounded(MaxPools))
            if (held && ModelRef(pool) is { } model)
                Held.Add(model);
        if (held) Issuing(manager);
        ShadowClosing();
        ShadowView.Use(null);
        EndFrame();
        return held;
    }
}
