using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Hot tessellation engine methods optimized from the first call. Tiering kept them quick-jitted ~12 s into a world
// (tier-up waits while new methods jit; a world start jits 10k+), helpers not inlined: passes 10-20x slower, a 22
// 000-chunk join backlog (2026-10-07). Harmony rebuilds a patched method optimized at once; this transpiler changes
// nothing. Large methods only; skipped if another patch rebuilt one (an old world's ownerless record doesn't count).
internal static class JitWarm
{
    private const int MaxMethods = 32;

    private static readonly (Type Type, string Name)[] Hot =
    [
        (typeof(CubeTesselator), nameof(CubeTesselator.Tesselate)),
        (typeof(CubeTesselator), nameof(CubeTesselator.DrawBlockFace)),
        (typeof(BleedingCubeTesselator), nameof(BleedingCubeTesselator.Tesselate)),
        (typeof(TopsoilTesselator), nameof(TopsoilTesselator.Tesselate)),
        (typeof(TopsoilTesselator), "DrawBlockFaceTopSoil"),
        (typeof(SurfaceLayerTesselator), nameof(SurfaceLayerTesselator.Tesselate)),
        (typeof(SurfaceLayerTesselator), nameof(SurfaceLayerTesselator.DrawBlockFace)),
        (typeof(LiquidTesselator), nameof(LiquidTesselator.Tesselate)),
        (typeof(ChunkTesselator), nameof(ChunkTesselator.BuildBlockPolygons)),
        (typeof(ChunkTesselator), nameof(ChunkTesselator.CalculateVisibleFaces_Fluids)),
        (typeof(ShapeTesselator), "TesselateShapeElements")
    ];

    public static bool Enabled { get; set; } = true;

    public static void Install(Harmony harmony, ILogger? logger)
    {
        var warmed = 0;
        var same = AccessTools.Method(typeof(JitWarm), nameof(Same));
        if (!Enabled || !NotNull(harmony) || !NotNull(same)) return;
        foreach (var method in Methods().Bounded(MaxMethods))
            if (!EngineShape.Patched(method) && NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(same))))
                warmed++;
        if (warmed == 0) logger?.Warning("Komet JitWarm: none of the engine's tessellation methods was found");
        _ = Assert(warmed <= MaxMethods);
    }

    // The 7-argument AddJsonModelDataToMesh, not its small 5-argument wrapper, which stays inlinable
    internal static List<MethodInfo> Methods()
    {
        var methods = new List<MethodInfo>();
        foreach (var (type, name) in Hot.Bounded(MaxMethods))
            foreach (var method in AccessTools.GetDeclaredMethods(type).Bounded(256))
                if (method.Name == name && !method.IsAbstract && method.GetMethodBody() is not null)
                    methods.Add(method);
        var json = AccessTools.GetDeclaredMethods(typeof(JsonTesselator))
            .FirstOrDefault(static m => m.Name == nameof(JsonTesselator.AddJsonModelDataToMesh) && m.GetParameters().Length == 7);
        if (json is not null) methods.Add(json);
        return Assert(methods.Count <= MaxMethods) ? methods : [];
    }

    private static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions) =>
        NotNull(instructions) ? instructions : [];
}
