using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Komet.Chunks;

// MeshDataRecycler sits at its caps (300k/900k/2240k quads, one freed per DoRecycling): 210 MB in a heap dump. After
// DoRecycling this drops entries past the level's cap share or untouched for its time (1: half, 8 s; 2: quarter, 4 s).
// Level 2 also drops far BlockEntityMicroBlock meshes after OnTesselation (174 MB over 58000); the next tessellation
// regenerates them (Mesh == null -> GenMesh) on the same thread, so no reader sees them vanish.
internal static class RecyclerTrim
{
    private const int MaxLevel = 2, MaxEntries = 1 << 16;
    private static readonly int[] Caps = [300_000, 900_000, 2_240_000];
    private static readonly long[] TtlMs = [15_000, 8_000, 4_000];

    public static int Level { get; set; } = 1;
    public static int Max => MaxLevel;
    public static bool Installed { get; private set; }
    public static long Dropped { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "smallSizes")]
    private static extern ref SortedList<float, MeshData>? Small(MeshDataRecycler recycler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mediumSizes")]
    private static extern ref SortedList<float, MeshData>? Medium(MeshDataRecycler recycler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "largeSizes")]
    private static extern ref SortedList<float, MeshData>? Large(MeshDataRecycler recycler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref IClientWorldAccessor? Game(MeshDataRecycler recycler);

    private const int FarChunks = 6;
    private static AccessTools.FieldRef<object, MeshData?>? _mesh;

    private static void InstallMicroBlocks(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntityMicroBlock");
        var tesselate = type is null ? null : AccessTools.DeclaredMethod(type, "OnTesselation");
        var mesh = type is null ? null : AccessTools.DeclaredField(type, "Mesh");
        if (tesselate is null || mesh?.FieldType != typeof(MeshData) || !NotNull(harmony)) return;
        _mesh = AccessTools.FieldRefAccess<object, MeshData?>(mesh);
        _ = NotNull(harmony.Patch(tesselate, postfix: new HarmonyMethod(typeof(RecyclerTrim), nameof(Tesselated))));
    }

    // On the tessellation thread, after the mesh went into the chunk's
    private static void Tesselated(BlockEntity __instance)
    {
        if (Level < MaxLevel || _mesh is not { } mesh || __instance?.Api?.World is not IClientWorldAccessor world ||
            world.Player?.Entity?.Pos is not { } at || __instance.Pos is not { } pos) return;
        var (dx, dz) = (pos.X / 32 - (int)at.X / 32, pos.Z / 32 - (int)at.Z / 32);
        if (Math.Abs(dx) <= FarChunks && Math.Abs(dz) <= FarChunks) return;
        if (mesh(__instance) is null) return;
        mesh(__instance) = null;
    }

    public static void Install(Harmony harmony)
    {
        Installed = false;
        InstallMicroBlocks(harmony);
        var recycling = AccessTools.DeclaredMethod(typeof(MeshDataRecycler), nameof(MeshDataRecycler.DoRecycling));
        if (!NotNull(harmony) || recycling is null ||
            Array.Exists(["smallSizes", "mediumSizes", "largeSizes", "game"], f => AccessTools.DeclaredField(typeof(MeshDataRecycler), f) is null))
            return;
        _ = NotNull(harmony.Patch(recycling, postfix: new HarmonyMethod(typeof(RecyclerTrim), nameof(Recycled))));
        Installed = true;
    }

    private static void Recycled(MeshDataRecycler __instance)
    {
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (level == 0 || !NotNull(__instance) || Game(__instance) is not { } game) return;
        // unsynchronised lists, several threads: only under TessSafety.RecycleGate (re-entered by DoRecycling)
        if (!Monitor.TryEnter(Komet.Tessellation.TessSafety.RecycleGate)) return;
        try
        {
            var before = game.ElapsedMilliseconds - TtlMs[level]; // each level halves the caps
            Trim(Small(__instance), Caps[0] >> level, before);
            Trim(Medium(__instance), Caps[1] >> level, before);
            Trim(Large(__instance), Caps[2] >> level, before);
        }
        finally
        {
            Monitor.Exit(Komet.Tessellation.TessSafety.RecycleGate);
        }
    }

    // Entries untouched since before `stale`, then the oldest until the quads kept fit the cap
    private static void Trim(SortedList<float, MeshData>? list, int cap, long stale)
    {
        if (list is not { Count: > 0 } || !Assert(cap > 0)) return;
        var kept = 0L;
        var count = Math.Min(list.Count, MaxEntries);
        for (var n = 0; n < MaxEntries; n++)
        {
            var i = count - 1 - n; // from the last: removing i moves only the entries already seen
            if (i < 0) break;
            if (list.GetValueAtIndex(i).RecyclingTime >= stale)
            {
                kept += (long)list.GetKeyAtIndex(i);
                continue;
            }

            Drop(list, i);
        }

        if (kept > cap && list.Count > 0) Oldest(list, kept - cap);
        _ = Assert(kept >= 0 || list.Count == 0);
    }

    // Oldest dropped until `over` quads are gone: sorted once, removed from the top index down (under RecycleGate)
    private static void Oldest(SortedList<float, MeshData> list, long over)
    {
        var count = Math.Min(list.Count, MaxEntries);
        var order = System.Buffers.ArrayPool<(long Time, int At)>.Shared.Rent(count);
        var dropped = System.Buffers.ArrayPool<int>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < Math.Min(count, MaxEntries); i++) order[i] = (list.GetValueAtIndex(i).RecyclingTime, i);
            Array.Sort(order, 0, count);
            var n = 0;
            for (var k = 0; k < Math.Min(count, MaxEntries) && over > 0; k++)
            {
                over -= (long)list.GetKeyAtIndex(order[k].At);
                dropped[n++] = order[k].At;
            }

            Array.Sort(dropped, 0, n);
            for (var k = 0; k < Math.Min(n, MaxEntries); k++) Drop(list, dropped[n - 1 - k]);
        }
        finally
        {
            System.Buffers.ArrayPool<(long Time, int At)>.Shared.Return(order);
            System.Buffers.ArrayPool<int>.Shared.Return(dropped);
        }

        _ = Assert(count <= MaxEntries);
    }

    private static void Drop(SortedList<float, MeshData> list, int at)
    {
        _ = Assert(Index(at, list.Count)) && Assert(Dropped >= 0);
        list.GetValueAtIndex(at).DisposeBasicData();
        list.RemoveAt(at);
        Dropped++;
    }
}
