using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// The engine never frees a chunk pool: flying across the map left 5.4 GB of pools for 13.6 M triangles (~0.5 GB data).
// A pool empty for EmptySeconds leaves its pass's list and its mesh is deleted (GL buffers, Vulkan copies via
// VAO.Dispose); its reference is cleared so DisposeAllPools skips it, its master list slot stays (ids are indices). A
// pass keeps one pool. Levels: 1 empty pools after 10 s; 2 after 2 s, and drains pools below DrainShare (25 %) of
// indices, older than OldMs (1 min), not the pass's newest; 3: 1 s, 50 %, 30 s, 2x speed; 4: 75 %, 15 s, 4x; 5: 90 %, 5
// s, 8x. Draining only while the pass is below PassShare: TryAdd returns null (as full) and its chunks are marked dirty
// (DrainChunks/s) to retessellate elsewhere; one pool per pass, given up after DrainMs. Checked every IntervalMs in
// TerrainRebuild's Before stage (no frame open, writes done).
internal static class PoolTrim
{
    private const long IntervalMs = 1_000, DrainMs = 60_000;
    private const int MaxManagers = 64, MaxPools = 4096, MaxLevel = 5;

    private static readonly ConditionalWeakTable<MeshDataPool, StrongBox<long>> EmptySince = [], Seen = [];
    private static readonly ConditionalWeakTable<MeshDataPool, StrongBox<long>> Draining = []; // since when
    private static long _next;
    private static int _drains;

    // 0 off, 1 empty pools, 2 and 3 also draining the sparse ones
    public static int Level { get; set; } = 2;
    public static bool Enabled => Level > 0;
    public static int Max => MaxLevel;
    public static long Drained { get; private set; }

    private static long EmptyMs => Level switch { >= 3 => 1_000, 2 => 2_000, _ => 10_000 };
    private static double DrainShare => Level switch { >= 5 => 0.9, 4 => 0.75, 3 => 0.5, _ => 0.25 };
    private static long OldMs => Level switch { >= 5 => 5_000, 4 => 15_000, 3 => 30_000, _ => 60_000 };
    private static int DrainChunks => Level switch { >= 5 => 768, 4 => 384, 3 => 192, _ => 96 };

    // Stops once waste is gone: every retessellated chunk unpacks its and its neighbours' data in RAM
    private static double PassShare => Level switch { >= 5 => 0.8, 4 => 0.7, 3 => 0.6, _ => 0.5 };

    // TryAdd prefix: a draining pool takes nothing (null, as full)
    internal static bool TryAdding(MeshDataPool __instance, ref ModelDataPoolLocation? __result)
    {
        if (Level < 2 || !Draining.TryGetValue(__instance, out _)) return true;
        __result = null;
        return !Assert(Level >= 2);
    }

    public static void Install(Harmony harmony)
    {
        var add = AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd));
        if (NotNull(harmony) && add is not null)
            _ = harmony.Patch(add, new HarmonyMethod(typeof(PoolTrim), nameof(TryAdding)) { priority = Priority.Last });
        _ = Assert(_drains >= 0);
    }
    public static long Trimmed { get; private set; }
    public static long TrimmedBytes { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    private static extern ref List<ModelDataPoolLocation>? Locations(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelRef")]
    private static extern ref MeshRef? ModelRef(MeshDataPool pool);

    internal static void Run(ICoreClientAPI api, ClientMain game)
    {
        var now = Environment.TickCount64;
        if (!Enabled || now < _next || !NotNull(api) || TerrainOf(game) is not { } renderer) return;
        (_next, _drains) = (now + IntervalMs, 0);
        var passes = PassPools(renderer);
        if (!NotNull(passes) || !Assert(passes.Length < 32)) return;
        for (var pass = 0; pass < Math.Min(passes.Length, MaxManagers); pass++)
        {
            var managers = passes[pass];
            for (var m = 0; m < Math.Min(managers?.Length ?? 0, MaxManagers); m++)
                if (managers![m] is { } manager)
                {
                    Trim(api, manager, now);
                    if (Level >= 2) Drain(game, manager, now);
                }
        }
    }

    private static void Trim(ICoreClientAPI api, MeshDataPoolManager manager, long now)
    {
        if (Pools(manager) is not { Count: > 0 } pools || !Assert(pools.Count <= MaxPools)) return; // Fields.Pools; a pass may have none
        for (var n = 0; n < MaxPools; n++)
        {
            var i = pools.Count - 1 - n; // from the last: removal keeps the rest where they are
            if (i < 0 || pools.Count <= 1) break;
            var pool = pools[i];
            if (Locations(pool) is not { Count: 0 } || ModelRef(pool) is null)
            {
                _ = EmptySince.Remove(pool);
                continue;
            }

            var since = Box(EmptySince, pool, now);
            if (now - since.Value < EmptyMs) continue;
            pools.RemoveAt(i);
            _ = EmptySince.Remove(pool);
            if (Draining.Remove(pool)) Drained++;
            var bytes = (long)pool.VerticesPoolSize * 20 + (long)pool.IndicesPoolSize * 4; // about: faces and colours, indices
            api.Render.DeleteMesh(ModelRef(pool)); // OpenGL's buffers, and Vulkan's copies (VulkanRenderer.Disposing)
            ModelRef(pool) = null;
            (pool.VerticesPoolSize, pool.IndicesPoolSize) = (0, 0); // the master's statistics count it no more
            (Trimmed, TrimmedBytes) = (Trimmed + 1, TrimmedBytes + bytes);
        }

        _ = Assert(pools.Count >= 1) && Assert(Trimmed >= 0);
    }

    // Sparsest old pool (never the newest) stops taking chunks; its chunks retessellate elsewhere
    private static void Drain(ClientMain game, MeshDataPoolManager manager, long now)
    {
        if (Pools(manager) is not { Count: > 1 } pools || !Assert(pools.Count <= MaxPools)) return;
        MeshDataPool? draining = null, sparsest = null;
        var least = DrainShare;
        var (used, room, count) = (0L, 0L, Math.Min(pools.Count, MaxPools));
        Span<long> uses = count <= 256 ? stackalloc long[count] : new long[count]; // each pool's walked once
        for (var i = 0; i < Math.Min(count, MaxPools); i++)
        {
            uses[i] = Used(pools[i]);
            (used, room) = (used + uses[i], room + pools[i].IndicesPoolSize);
        }
        var sparsestUsed = 0L;
        var wasteful = room > 0 && used < PassShare * room;
        for (var i = 0; i < Math.Min(pools.Count - 1, MaxPools); i++) // the newest takes what the drained ones give up
        {
            var pool = pools[i];
            if (Draining.TryGetValue(pool, out var since))
            {
                if (now - since.Value > DrainMs) _ = Draining.Remove(pool); // its chunks did not come back: it takes chunks again
                else draining = pool;
                continue;
            }

            if (now - Box(Seen, pool, now).Value < OldMs || pool.IndicesPoolSize <= 0) continue;
            var share = uses[i] / (double)pool.IndicesPoolSize;
            if (share > 0 && share < least) (least, sparsest, sparsestUsed) = (share, pool, uses[i]);
        }

        // only if its chunks fit the others' free room, else draining makes a new pool forever
        var fits = sparsest is not null && room - used - (sparsest.IndicesPoolSize - sparsestUsed) >= 1.5 * sparsestUsed;
        if (draining is null && sparsest is not null && wasteful && fits) Draining.Add(sparsest, new StrongBox<long>(now));
        draining ??= sparsest;
        if (draining is not null && !Komet.Tessellation.TessSeams.Backlogged) Dirty(game, draining); // loading comes first
    }

    // First seen now (avoids the closure GetValue's factory would take)
    private static StrongBox<long> Box(ConditionalWeakTable<MeshDataPool, StrongBox<long>> table, MeshDataPool pool, long now)
    {
        _ = NotNull(pool) && Assert(now >= 0);
        if (table.TryGetValue(pool, out var box)) return box;
        box = new StrongBox<long>(now);
        table.Add(pool, box);
        return box;
    }

    private static long Used(MeshDataPool pool)
    {
        var used = 0L;
        if (Locations(pool) is not { } locations) return 0;
        foreach (var location in locations.Bounded(1 << 16)) used += location.IndicesEnd - location.IndicesStart;
        return Assert(used >= 0) ? used : 0;
    }

    // Marked dirty without event (no mod told), within the per-second budget
    private static void Dirty(ClientMain game, MeshDataPool pool)
    {
        if (Locations(pool) is not { } locations || game.WorldMap is not { } map) return;
        for (var i = 0; i < Math.Min(locations.Count, 1 << 16) && _drains < DrainChunks; i++) // no copy: marking leaves the list be
        {
            var sphere = locations[i].FrustumCullSphere;
            int x = (int)Math.Floor(sphere.x / 32), y = (int)Math.Floor(sphere.y / 32), z = (int)Math.Floor(sphere.z / 32);
            map.MarkChunkDirty(x, y, z, fireEvent: false);
            _drains++;
        }

        _ = Assert(_drains <= DrainChunks + 1);
    }
}
