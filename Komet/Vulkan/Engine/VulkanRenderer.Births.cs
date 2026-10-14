using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// MeshDataPoolManager.AddModel makes a chunk pool when none has room (AllocateNewPool) and writes into it at once.
// Adopting it later (Preparing) means copying the written data and waiting for the GPU (a frame in flight), a
// Before-stage stall per new pool while chunks stream. Adopted in AllocateNewPool's postfix the pool is still empty: no
// copy, no wait (TerrainPools.Adopt fresh). Pools refused still go to Preparing. Always on: an exact saving.
// Pool-making bodies are pinned (EngineShape): a write into the new pool there would be lost to fresh adoption.
internal static partial class VulkanRenderer
{
    // EngineShape of BirthSeams() in Vintage Story 1.22.7
    internal const ulong BirthFingerprint = 0x545C1A54CE3C990BUL;

    private static MeshDataPoolManager? _adding; // the manager whose AddModel runs, main thread
    private static bool _births;

    public static long Born { get; private set; } // pools adopted as they were made, and the time it took
    public static long BirthTicks { get; private set; }

    internal static MethodBase?[] BirthSeams()
    {
        var platform = typeof(ClientPlatformWindows);
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.AllocateNewPool)),
            AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.AllocateEmptyMesh)),
            AccessTools.DeclaredMethod(platform, nameof(ClientPlatformWindows.AllocateEmptySSBOMesh))
        ];
        return Assert(seams.Length == 3) ? seams : [];
    }

    private static void PatchBirths(Harmony harmony, ulong fingerprint = BirthFingerprint)
    {
        (_births, _adding, Born, BirthTicks) = (false, null, 0, 0);
        var seams = BirthSeams();
        var add = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.AddModel),
            [typeof(MeshData), typeof(Vec3i), typeof(int), typeof(Sphere)]);
        if (!NotNull(harmony) || !NotNull(add) || !Assert(seams.Length == 3) || !Assert(!Array.Exists(seams, s => s is null)) ||
            !EngineShape.Matches(seams, fingerprint, "Vulkan pool births", _logger)) return;
        _ = NotNull(harmony.Patch(add, Of(nameof(Adding)), finalizer: Of(nameof(Added))));
        _ = NotNull(harmony.Patch(seams[0], Of(nameof(Birthing)), Of(nameof(Birth)), finalizer: Of(nameof(Birthed))));
        _births = true;
    }

    // Harmony injects __instance and __result by name; the finalizer clears the manager whatever AddModel did
    private static void Adding(MeshDataPoolManager __instance) => _adding = NotNull(__instance) ? __instance : null;

    private static void Added() => _adding = null;

    // A chunk pass's pool is being made: its buffers lean from a smaller size (GlTap.PoolBirth)
    private static void Birthing() => GlTap.PoolBirth = _births && _adding is not null && Assert(_births);

    private static void Birthed() => GlTap.PoolBirth = false;

    private static void Birth(MeshDataPool __result)
    {
        GlTap.PoolBirth = false;
        if (!_births || _adding is not { } manager || (State != Phase.Running && !(OnlyOn && GlTap.Recording)) ||
            _renderer is not { } renderer ||
            __result is null || ModelRef(__result) is not VAO vao || _api?.World is not ClientMain game ||
            TerrainOf(game) is not { } chunks) return;
        var pass = PassOf(PassPools(chunks), manager);
        if (pass < 0 || !Assert(pass < ChunkPasses) || !Takes(pass) || !Assert(vao.VaoId > 0)) return;
        var (classic, start) = (Classic(pass), Stopwatch.GetTimestamp());
        Guarded(() =>
        {
            if (!renderer.Born(vao, classic)) return;
            (Born, BirthTicks) = (Born + 1, BirthTicks + Stopwatch.GetTimestamp() - start);
        });
    }

    // The chunk renderer's pass the manager draws, -1 for a manager of someone else's
    internal static int PassOf(MeshDataPoolManager[][]? passes, MeshDataPoolManager manager)
    {
        if (passes is null || !NotNull(manager) || !Assert(passes.Length < 32)) return -1;
        for (var pass = 0; pass < Math.Min(passes.Length, ChunkPasses); pass++)
        {
            var managers = passes[pass];
            for (var m = 0; m < Math.Min(managers?.Length ?? 0, MaxManagers); m++)
                if (ReferenceEquals(managers![m], manager))
                    return pass;
        }

        return -1;
    }

    private const int ChunkPasses = 9;

    private static bool Takes(int pass)
    {
        _ = Assert(Taking.Length == 5) && Assert(pass >= 0);
        return (EnumChunkRenderPass)pass switch
        {
            EnumChunkRenderPass.Liquid => Taking[(int)Pass.LiquidDepth] || Taking[(int)Pass.Transparent],
            EnumChunkRenderPass.Transparent or EnumChunkRenderPass.Meta => Taking[(int)Pass.Transparent],
            EnumChunkRenderPass.OpaqueWaterPlant => Taking[(int)Pass.WaterPlants],
            _ => Taking[(int)Pass.Opaque] || Taking[(int)Pass.Shadows]
        };
    }

    // A pass's pools drawn classic, with their own indices: the liquids always, the others without SSBOs
    private static bool Classic(int pass) =>
        Assert(pass >= 0) && Index(pass, ChunkPasses) &&
        (pass == (int)EnumChunkRenderPass.Liquid || !(ScreenManager.Platform?.UseSSBOs ?? true));
}
