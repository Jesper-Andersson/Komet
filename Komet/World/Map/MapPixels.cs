using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.World;

// Each GenerateChunkImage piece is a new int[1024] awaiting map DB save and tile upload, so it survives a GC (130 MB a
// minute exploring). Rented instead, returned when both consumers released it; a piece replaced before use is released
// by that consumer.
internal static class MapPixels
{
    // 900 a second flying at 20, in bursts of hundreds
    private const int MaxInstructions = 4096, MaxPooled = 4096, Consumers = 2;
    private static readonly Core.BoundedPool<int[]> Pool = new(MaxPooled);
    private static readonly ConditionalWeakTable<int[], StrongBox<int>> Owed = [];
    private static MethodInfo? _store;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        var (layer, tile) = (AccessTools.TypeByName("Vintagestory.GameContent.ChunkMapLayer"),
            AccessTools.TypeByName("Vintagestory.GameContent.MultiChunkMapComponent"));
        var piece = AccessTools.TypeByName("Vintagestory.GameContent.MapPieceDB");
        var (generate, tick, set) = (layer is null ? null : AccessTools.DeclaredMethod(layer, "GenerateChunkImage"),
            layer is null ? null : AccessTools.DeclaredMethod(layer, "OnOffThreadTick"),
            tile is null ? null : AccessTools.DeclaredMethod(tile, "setChunk"));
        if (!NotNull(harmony) || generate is null || tick is null || set is null || piece is null) return;
        _store = AccessTools.Method(typeof(MapPixels), nameof(Store)).MakeGenericMethod(piece);
        _ = NotNull(harmony.Patch(generate, transpiler: new HarmonyMethod(Rewrite)));
        _ = NotNull(harmony.Patch(tick, transpiler: new HarmonyMethod(Saving)));
        _ = NotNull(harmony.Patch(set, new HarmonyMethod(Waiting)));
    }

    // Exactly one newarr int32, the image's own, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var rent = AccessTools.Method(typeof(MapPixels), nameof(Rent));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(rent)) return code;
        Rewritten = Assert(Il.SubstituteSingle(code, c => c.opcode == OpCodes.Newarr && c.operand is Type t && t == typeof(int),
            rent));
        return code;
    }

    // The save list's indexer set in OnOffThreadTick, exactly once, else the engine's IL
    internal static List<CodeInstruction> Saving(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(_store)) return code;
        var dictionary = _store.GetParameters()[0].ParameterType;
        var at = Il.Single(code,
            c => c.operand is MethodInfo { Name: "set_Item" } m && m.DeclaringType == dictionary);
        if (!Assert(at >= 0) || !Il.Substitute(code, at, _store)) Rewritten = false;
        return code;
    }

    // toSaveList[key] = value: the piece it replaces will not be saved
    internal static void Store<T>(Dictionary<FastVec2i, T> list, FastVec2i key, T value) where T : class
    {
        if (Enabled && NotNull(list) && list.TryGetValue(key, out var old) && !ReferenceEquals(old, value))
            Release(Pixels<T>.Of(old));
        list[key] = value;
    }

    // setChunk(dx, dz, pixels): the piece waiting in that cell is not uploaded
    private static void Waiting(int dx, int dz, int[] pixels, int[]?[]? ___pixelsToSet)
    {
        if (Enabled && ___pixelsToSet is { Length: 9 } waiting && dx is >= 0 and < 3 && dz is >= 0 and < 3 &&
            waiting[dz * 3 + dx] is { } old && !ReferenceEquals(old, pixels)) Release(old);
    }

    internal static class Pixels<T> where T : class
    {
        public static readonly AccessTools.FieldRef<T, int[]> Of = AccessTools.FieldRefAccess<T, int[]>("Pixels");
    }

    internal static int[] Rent(int length)
    {
        if (!Enabled) return new int[length];
        if (!Assert(length > 0)) return new int[length];
        var pixels = Pool.Take();
        if (pixels is null || pixels.Length != length) pixels = new int[length];
        else Array.Clear(pixels);
        _ = Assert(pixels.Length == length);
        Owed.AddOrUpdate(pixels, new StrongBox<int>(Consumers));
        return pixels;
    }

    // The last consumer done returns it
    internal static void Release(int[]? pixels)
    {
        if (pixels is null || !Owed.TryGetValue(pixels, out var owed)) return;
        var left = Interlocked.Decrement(ref owed.Value);
        if (!Assert(left >= 0) || left != 0) return;
        _ = Owed.Remove(pixels);
        Pool.Give(pixels);
    }
}
