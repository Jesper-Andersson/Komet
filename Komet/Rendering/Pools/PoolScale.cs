using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Each pool (ModelDataPoolMaxVertexSize, 500 000 vertices) costs a draw, a culling job and an origin uniform; bench
// passes hold ~110 pools. A pass's second pool is twice the size, later ones Scale times, so the same meshes fill a
// quarter of the pools (134 -> 144 FPS). Placement is order-independent. Read at pool creation; Scale 1 is the engine.
internal static class PoolScale
{
    public const int Engine = 1, MaxScale = 8, DefaultScale = 4;
    private const int MaxInstructions = 512, MaxSites = 16;

    private static readonly ConditionalWeakTable<MeshDataPoolManager, object> Chunks = [];
    private static readonly object Marker = new();
    private static int _sites;

    public static int Scale { get; set; } = DefaultScale;
    public static bool Rewritten => _sites > 0;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "defaultVertexPoolSize")]
    private static extern ref int VertexSize(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "defaultIndexPoolSize")]
    private static extern ref int IndexSize(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "maxPartsPerPool")]
    private static extern ref int PartCount(MeshDataPoolManager manager);

    public static void Install(Harmony harmony)
    {
        _sites = 0;
        var add = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.AddModel),
            [typeof(MeshData), typeof(Vec3i), typeof(int), typeof(Sphere)]);
        var made = AccessTools.DeclaredConstructor(typeof(ChunkRenderer), [typeof(int[]), typeof(ClientMain)]);
        var atlas = AccessTools.DeclaredMethod(typeof(ChunkRenderer), "RuntimeAddBlockTextureAtlas", [typeof(int[])]);
        if (!NotNull(harmony) || !NotNull(add) || !NotNull(made) || !NotNull(atlas)) return;
        _ = NotNull(harmony.Patch(made, postfix: new HarmonyMethod(Registered)));
        _ = NotNull(harmony.Patch(atlas, postfix: new HarmonyMethod(Registered)));
        _ = NotNull(harmony.Patch(add, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static void Registered(ChunkRenderer __instance)
    {
        if (!NotNull(__instance) || __instance.poolsByRenderPass is not { } passes) return;
        foreach (var pass in passes.Bounded(MaxSites * MaxSites))
            foreach (var manager in (pass ?? []).Bounded(MaxSites * MaxSites))
                if (manager is not null) Register(manager);
    }

    internal static void Register(MeshDataPoolManager manager)
    {
        if (NotNull(manager)) Chunks.AddOrUpdate(manager, Marker);
    }

    // AddModel's pool-size reads go through Vertices, Indices or Parts
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        _sites = 0;
        var type = typeof(MeshDataPoolManager);
        (string Field, string Method)[] reads = [("defaultVertexPoolSize", nameof(Vertices)),
            ("defaultIndexPoolSize", nameof(Indices)), ("maxPartsPerPool", nameof(Parts))];
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var found = 0;
        foreach (var (field, method) in reads.Bounded(MaxSites))
        {
            var (read, scaled) = (AccessTools.DeclaredField(type, field),
                AccessTools.Method(typeof(PoolScale), method));
            if (!NotNull(read) || !NotNull(scaled)) return code;
            var before = found;
            for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
                if (code[i].LoadsField(read) && Il.Substitute(code, i, scaled)) found++;
            _ = Assert(found > before);
        }

        _sites = found;
        return code;
    }

    internal static int Vertices(MeshDataPoolManager manager) => VertexSize(manager) * Factor(manager);
    internal static int Indices(MeshDataPoolManager manager) => IndexSize(manager) * Factor(manager);
    internal static int Parts(MeshDataPoolManager manager) => PartCount(manager) * Factor(manager);

    internal static int Factor(MeshDataPoolManager manager)
    {
        var scale = Math.Clamp(Scale, Engine, MaxScale);
        if (scale == Engine || !Chunks.TryGetValue(manager, out _) || Pools(manager) is not { } pools) return Engine;
        return pools.Count switch
        {
            0 => Engine,
            1 => Math.Min(2, scale),
            _ => Assert(pools.Count > 1) ? scale : Engine
        };
    }
}
