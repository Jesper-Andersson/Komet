using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// OnRenderFrameShadows draws every entity whose 3-block sphere touches the shadow box into both maps: at VD 256 ~0.3 ms
// GPU a frame for the far map alone, mostly shadow nothing in view receives. The transpiler gives the sphere test
// ShadowView's planes, as for terrain; the terrain pass (priority 0.37, before 0.4) made the view (Take).
internal static class EntityCasters
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x6E29B039DF02BB75UL;

    private static readonly Plane[] Casters = new Plane[ShadowView.MaxCasters];
    private static readonly SeamGuard Guard = new(nameof(EntityCasters), PatchKinds.Body, [typeof(EntityCasters)]);
    private static bool _rewritten;
    private static int _count;

    public static bool Enabled { get; set; } = true;

    // Unverified body or another mod's patch: every entity passing the engine test drawn
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched && _rewritten;

    internal static Action Recheck { get; } = static () => Guard.Recheck(_rewritten);

    // Totals (main thread): entities tested against the view's planes, and those left out
    public static long Tested { get; private set; }
    public static long Culled { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_rewritten, _count) = (false, 0);
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 1) || seams[0] is null || !Guard.Match(seams, fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Opening), new HarmonyMethod(Closing),
            new HarmonyMethod(Rewrite)));
        if (!_rewritten) logger?.Warning("Komet EntityCasters: the entities' shadow test was not found; every entity casts");
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams = [AccessTools.DeclaredMethod(typeof(SystemRenderEntities), "OnRenderFrameShadows", [typeof(float)])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;
        // Harmony reruns it on every repatch, always on the engine's own body
        var test = AccessTools.DeclaredMethod(typeof(FrustumCulling), nameof(FrustumCulling.SphereInFrustum),
            [typeof(double), typeof(double), typeof(double), typeof(double)]);
        _rewritten = NotNull(test) &&
                     Il.SubstituteSingle(code, c => c.Calls(test), AccessTools.DeclaredMethod(typeof(EntityCasters), nameof(InView)));
        return code;
    }

    internal static void Opening(ClientMain ___game)
    {
        var passed = ShadowView.Take();
        _count = Enabled && !Blocked && passed && ___game?.frustumCuller is { } culler && Planes(culler) is { Length: 6 } planes
            ? ShadowView.Casters(planes[0], Casters)
            : 0;
        _ = Assert(_count is >= 0 and <= ShadowView.MaxCasters);
    }

    internal static void Closing()
    {
        _ = Assert(!ShadowView.Casting);
        _count = 0;
    }

    internal static bool InView(FrustumCulling culler, double x, double y, double z, double radius)
    {
        if (!culler.SphereInFrustum(x, y, z, radius) || !Finite(radius)) return false;
        if (_count > 0) Tested++;
        for (var p = 0; p < Math.Min(_count, Casters.Length); p++)
        {
            if (Casters[p].distanceOfPoint(x, y, z) > -radius) continue;
            Culled++;
            return false;
        }

        return Assert(_count <= ShadowView.MaxCasters);
    }
}
