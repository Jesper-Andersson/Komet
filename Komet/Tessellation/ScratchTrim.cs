using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Each ChunkTesselator (each thread's) keeps scratch MeshData per LOD, pass and atlas for chunk centre and edge, grown
// to the largest chunk seen, never shrunk: 324 MB over nine tesselators in a heap dump. After IdleMs with nothing
// awaiting tessellation, one holding over GrownVertices gets fresh start-size scratch via the engine's
// UpdateForAtlasses (as when an atlas is added) under its ReloadLock; one per check, each once per CooldownMs.
internal static class ScratchTrim
{
    private const long IdleMs = 10_000, CooldownMs = 60_000;
    private const int GrownVertices = 4 * 1024 * 1024; // all its scratch meshes together (the engine starts them at 1024 each)

    private static readonly ConditionalWeakTable<ChunkTesselator, StrongBox<long>> Last = [];
    private static MethodInfo? _update;
    private static long _busySince = Environment.TickCount64, _next;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModeldataByRenderPassByLodLevel")]
    private static extern ref MeshData[][][]? Center(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModeldataByRenderPassByLodLevel")]
    private static extern ref MeshData[][][]? Edge(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "TextureIdToReturnNum")]
    private static extern ref int[]? Atlases(ChunkTesselator tesselator);

    // On the main thread, from TerrainRebuild's hook
    internal static void Run(ClientMain game)
    {
        var now = Environment.TickCount64;
        if (RuntimeStats.chunksAwaitingTesselation > 0 || RuntimeStats.chunksAwaitingPooling > 0) _busySince = now;
        if (now - _busySince < IdleMs || now < _next || !NotNull(game)) return;
        _next = now + 1000; // a check a second: no list of the workers every idle frame
        _update ??= AccessTools.DeclaredMethod(typeof(ChunkTesselator), "UpdateForAtlasses", [typeof(int[])]);
        if (_update is null) return;
        var all = TessWorkers.Instances(game);
        if (game.TerrainChunkTesselator is { } own) all.Add(own);
        foreach (var tesselator in all.Bounded(WorkerPool.MaxThreads + 1))
        {
            var last = Last.GetValue(tesselator, _ => new StrongBox<long>(0));
            if (now - last.Value < CooldownMs || Vertices(tesselator) < GrownVertices || Atlases(tesselator) is not { } atlases) continue;
            last.Value = now;
            _ = _update.Invoke(tesselator, [atlases]); // takes the tesselator's ReloadLock
            return; // one a check: a worker in a pass holds its lock for a few milliseconds
        }
    }

    private static long Vertices(ChunkTesselator tesselator)
    {
        var sum = 0L;
        foreach (var lods in (ReadOnlySpan<MeshData[][][]?>)[Center(tesselator), Edge(tesselator)])
            foreach (var passes in (lods ?? []).Bounded(8))
                foreach (var atlases in (passes ?? []).Bounded(64))
                    foreach (var mesh in (atlases ?? []).Bounded(64))
                        sum += mesh?.VerticesMax ?? 0;
        return Assert(sum >= 0) ? sum : 0;
    }
}
