using System.Diagnostics;
using System.Numerics;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// MeshDataPool.FrustumCull, bit-exact and in order, a vector at a time. A pool quiet for QuietCulls is sorted into an
// x/z grid whose cell boxes test first (AddModel is first fit, so only sorting lets a box reject: 2/3 camera, 6/7 far
// shadow; sorting on every change cost 8 %). Sphere floats widened in the kernel: 5-10 % faster than doubles.
internal static partial class FrustumSweep
{
    private const int MaxLocations = 65536, MaxPools = 4096, PlaneCount = 6, MaxLanes = 32;
    private const int MaxPlanes = PlaneCount + ShadowView.MaxCasters; // a shadow pass's own and the view's casters'
    private const int WordBits = 64, MaxWords = MaxLocations / WordBits;
    private const int CellShift = 5, MaxCells = 16384, MinBucketed = 256, PerCell = 8, MaxShift = 30;
    private const int QuietCulls = 8;
    private const double Slack = 1.0; // a cell box grows by a block, so rounding never shrinks it inside a member
    // Diff: search distance for a survivor, change absorbed before a relayout
    private const int Window = 8, MaxSearch = 32, ScanBudget = 8;
    private const int Churn = 64, RelayoutsPerFrame = 4, MaxRelayoutTries = 64;
    // Workers: ~0.3 ms of culling is the least worth waking one for
    private const int RowsPerHelper = 4096;

    // every cell can pad up to a vector; Diff appends behind the grid until a quarter of the rows are new
    private const int MaxSlots =
        MaxLocations + (MaxLocations / PerCell + 1) * MaxLanes + MaxLocations / 4 + 2 * MaxLanes;

    private const int Empty = -1, Gone = -1, Foreign = -1, Unseen = -1, Lost = -2;
    // radius / Sqrt3 in float is AABBisOutside's half edge; occlusion boxes divide alike, to the bit
    internal const float Sqrt3 = 1.7320508f;

    private static int _frameRebuilds, _frameRelayouts;
    private static long _rebuilds, _diffed, _relayouts, _settles; // _relayouts: FrustumSweepTests counts them
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;

    // Shadow passes skip casters whose shadow cannot reach the view (ShadowView)
    private static bool Casting => ShadowView.Casting;
    public static long Tested { get; private set; } // totals while Counting.Hud, main thread
    public static long Visible { get; private set; }
    public static long Skipped { get; private set; }
    // Interlocked: workers update them too
    public static long Rebuilds => Interlocked.Read(ref _rebuilds);
    public static long Diffed => Interlocked.Read(ref _diffed);
    public static long Settles => Interlocked.Read(ref _settles);
    public static long Ticks { get; private set; }
    public static int MaxRebuilds { get; private set; } // the most in one frame since ResetPeaks

    public static void ResetPeaks()
    {
        _ = Assert(MaxRebuilds >= 0) && Assert(Rebuilds >= Diffed);
        MaxRebuilds = 0;
    }

    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        Clear(); // pool ids restart; mirrors hold the previous world's locations
        _logger = logger;
        var cull = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull));
        var frame = AccessTools.Method(typeof(MeshDataPoolMasterManager), nameof(MeshDataPoolMasterManager.OnFrame));
        var prefix = new HarmonyMethod(FrustumCull);
        if (!NotNull(harmony) || !NotNull(cull) || !MeshPool.Seams() ||
            !Assert(AccessTools.Field(typeof(FrustumCulling), "frustum") != null) ||
            !Assert(AccessTools.Field(typeof(FrustumCulling), "playerPos") != null) ||
            !Assert(Vector<double>.Count is >= 2 and <= MaxLanes) || !Assert(Il.Binds(cull, prefix.method))) return;
        _ = NotNull(harmony.Patch(cull, prefix));
        // without it the relayout budget never refills
        if (NotNull(frame)) _ = NotNull(harmony.Patch(frame, postfix: new HarmonyMethod(NextFrame)));
        var render = AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var (pre, post) = (new HarmonyMethod(Precull), new HarmonyMethod(Culled));
        _parallel = NotNull(render) && Assert(AccessTools.Field(typeof(MeshDataPoolManager), "pools")?.FieldType ==
                                              typeof(List<MeshDataPool>)) &&
                    Assert(AccessTools.Field(typeof(MeshDataPoolManager), "frustumCuller")?.FieldType ==
                           typeof(FrustumCulling)) &&
                    Assert(AccessTools.Field(typeof(MeshDataPool), "dimensionId")?.FieldType == typeof(int)) &&
                    Assert(Il.Binds(render, pre.method)) && NotNull(harmony.Patch(render, pre, post));
        // a stage batch opens once a frame; every change to a pool's list ends it first
        var loop = AccessTools.Method(typeof(ClientMain), nameof(ClientMain.MainRenderLoop));
        var (add, remove) = (AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd)),
            AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation)));
        _staging = _parallel && NotNull(loop) && NotNull(add) && NotNull(remove) &&
                   NotNull(harmony.Patch(loop, new HarmonyMethod(EndFrame), new HarmonyMethod(EndFrame))) &&
                   NotNull(harmony.Patch(add, new HarmonyMethod(Unbatch) { priority = Priority.First })) &&
                   NotNull(harmony.Patch(remove, new HarmonyMethod(Unbatch) { priority = Priority.First }));
    }

    // TerrainRebuild: new chunk renderer (pool ids restart at 0); staging and settings stay
    internal static void Renewed()
    {
        EndFrame();
        Array.Clear(Plans);
        Array.Clear(PlanLengths);
        Array.Clear(_mirrors);
        Unnumbered.Clear();
        _ = Assert(!_staged) && Assert(_called == 0);
    }

    internal static void Clear()
    {
        EndFrame(); // no batch outlives its world
        Forget();
        Array.Clear(Plans); // no old manager stays reachable through a plan
        Array.Clear(PlanLengths);
        _ = Assert(!_staged) && Assert(_called == 0);
        Array.Clear(_mirrors);
        Unnumbered.Clear();
        (_frameRebuilds, _frameRelayouts, _jobCount, _jobCuller) = (0, 0, 0, null);
        (_jobPools, _jobMirrors, _jobManager, _parallel, _staging) = ([], [], [], true, false);
        _ = Assert(_mirrors.Length <= MaxPools);
    }

    // Once a frame (ChunkRenderer.OnBeforeRenderOpaque)
    internal static void NextFrame()
    {
        var rebuilt = Interlocked.Exchange(ref _frameRebuilds, 0);
        if (Counting.Hud && Assert(rebuilt >= 0)) MaxRebuilds = Math.Max(MaxRebuilds, rebuilt);
        _ = Assert(Interlocked.Exchange(ref _frameRelayouts, 0) <= RelayoutsPerFrame);
    }

    private static bool FrustumCull(MeshDataPool __instance, FrustumCulling frustumCuller,
        EnumFrustumCullMode frustumCullMode)
    {
        // OcclusionCulling's prefix ran first and left the pool to the GPU
        if (OcclusionCulling.Bypassing && Dimension(__instance) == 0) return false;
        if (!Enabled || frustumCullMode == EnumFrustumCullMode.NoCull) return true;
        var locations = Locations(__instance);
        if (!NotNull(locations) || !NotNull(frustumCuller) || locations.Count > MaxLocations) return true;
        var slot = SlotOf(__instance);
        if (slot != Foreign && slot < _mirrors.Length && _mirrors[slot] is { } done && done.Pass == _pass &&
            ReferenceEquals(done.Owner, locations))
        {
            if (Counting.Hud) (Tested, Visible) = (Tested + locations.Count, Visible + done.Groups);
            return false; // Precull wrote the pool's ranges and counts
        }

        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        var groups = Cull(frustumCuller, frustumCullMode, locations, slot, __instance.indicesStartsByte,
            __instance.indicesSizes, out var rendered, out var allocated);
        if (groups < 0) return true;
        (__instance.indicesGroupsCount, __instance.RenderedTriangles, __instance.AllocatedTris) =
            (groups, rendered, allocated);
        if (start != 0)
            (Tested, Visible, Ticks) = (Tested + locations.Count, Visible + groups,
                Ticks + Stopwatch.GetTimestamp() - start);
        return false;
    }

    // AddModel gives ids with an origin; SystemRenderDecals' pool shares id 0, so a pool without origin is Foreign (-1)
    internal static int SlotOf(MeshDataPool pool)
    {
        if (!NotNull(pool) || PoolOrigin(pool) is null) return Foreign;
        var id = PoolId(pool);
        return Index(id, MaxPools) ? id : Foreign;
    }

    // Visible ranges written, -1 when the engine has to cull
    internal static int Cull(FrustumCulling culler, EnumFrustumCullMode mode, List<ModelDataPoolLocation> locations,
        int slot, int[] starts, int[] sizes, out int rendered, out int allocated)
    {
        (rendered, allocated) = (0, 0);
        if (!Usable(culler, mode)) return -1;
        var m = MirrorOf(slot, locations);
        if (m is null) return -1;
        allocated = m.AllocatedTris;
        var groups = Swept(m, culler, mode, starts, sizes, out rendered);
        if (Counting.Hud) (Skipped, m.SkippedRows) = (Skipped + m.SkippedRows, 0);
        return groups;
    }

    // NoCull and out-of-enum values take IsVisible's default (!Hide, no frustum)
    private static bool Usable(FrustumCulling culler, EnumFrustumCullMode mode)
    {
        var planes = Planes(culler);
        var known = mode is EnumFrustumCullMode.CullNormal or EnumFrustumCullMode.CullInstant
            or EnumFrustumCullMode.CullInstantShadowPassNear or EnumFrustumCullMode.CullInstantShadowPassFar;
        return NotNull(planes) && Assert(planes.Length == PlaneCount) && known &&
               (mode == EnumFrustumCullMode.CullInstant || NotNull(PlayerPos(culler)));
    }

    // Touches only mirror and pool arrays: a call's pools can run in parallel
    private static int Swept(Mirror m, FrustumCulling culler, EnumFrustumCullMode mode, int[] starts, int[] sizes,
        out int rendered)
    {
        rendered = 0;
        if (!Assert(starts.Length >= 2 * m.Length) || !Assert(sizes.Length >= m.Length)) return -1;
        if (m.Length == 0) return 0;
        var shadow = mode is EnumFrustumCullMode.CullInstantShadowPassNear
            or EnumFrustumCullMode.CullInstantShadowPassFar;
        var planes = Planes(culler);
        if (!shadow || !Casting)
        {
            // InFrustumAndRange leaves the far plane to the range test
            Sweep(m, planes.AsSpan(0, mode == EnumFrustumCullMode.CullNormal ? 5 : PlaneCount), shadow, culler);
            return Emit(m, culler, mode, starts, sizes, out rendered);
        }

        Span<Plane> all = stackalloc Plane[MaxPlanes];
        planes.AsSpan(0, PlaneCount).CopyTo(all);
        Sweep(m, all[..(PlaneCount + ShadowView.Casters(planes[0], all[PlaneCount..]))], shadow, culler);
        return Emit(m, culler, mode, starts, sizes, out rendered);
    }
}
