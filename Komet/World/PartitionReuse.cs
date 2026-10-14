using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Common.Entities;

namespace Komet.World;

// PartitionEntities rebuilds every chunk, list and array 31 times a second (7 MB/s on the bench server, a quarter to
// gen 1); pooled here. Nothing outside keeps one across calls (WalkEntities reads in place; entityListForPartitioning
// is replaced) and chunks recycle one call late. Pools are per dictionary.
internal static class PartitionReuse
{
    private const string Partitioning = "Vintagestory.GameContent.EntityPartitioning";
    private const string ChunkType = "Vintagestory.GameContent.EntityPartitionChunk";
    private const int MaxInstructions = 512, MaxChunks = 8192, MaxLists = 1 << 16, MaxArrays = 8192, MaxCells = 64;
    private const int PartitionBit = 1, AddBit = 2, FetchBit = 4, AllBits = 7;

    private static int _rewritten; // Harmony reruns a transpiler whenever another mod patches the method
    private static long _reused;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits;

    public static long Reused => Interlocked.Read(ref _reused);

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;
        var (partitioning, chunk) = (AccessTools.TypeByName(Partitioning), AccessTools.TypeByName(ChunkType));
        if (!NotNull(harmony) || !NotNull(partitioning) || !NotNull(chunk) || !Fields(chunk)) return;
        var partition = AccessTools.DeclaredMethod(partitioning, "PartitionEntities", [typeof(ICollection<Entity>)]);
        var add = AccessTools.DeclaredMethod(chunk, "Add", [typeof(Entity), typeof(int)]);
        var fetch = AccessTools.DeclaredMethod(chunk, "FetchOrCreateList", [typeof(List<Entity>).MakeByRefType()]);
        if (!NotNull(partition) || !NotNull(add) || !NotNull(fetch)) return;
        _ = NotNull(harmony.Patch(fetch, transpiler: new HarmonyMethod(RewriteFetch)));
        _ = NotNull(harmony.Patch(add, transpiler: new HarmonyMethod(RewriteAdd)));
        _ = NotNull(harmony.Patch(partition, transpiler: new HarmonyMethod(RewritePartition),
            finalizer: new HarmonyMethod(Close)));
    }

    private static bool Fields(Type chunk) =>
        Assert(AccessTools.DeclaredField(chunk, "Entities")?.FieldType == typeof(List<Entity>[])) &&
        Assert(AccessTools.DeclaredField(chunk, "InanimateEntities")?.FieldType == typeof(List<Entity>[])) &&
        Assert(chunk.IsClass && AccessTools.DeclaredConstructor(chunk, []) is not null);

    internal static List<CodeInstruction> RewritePartition(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~PartitionBit;
        var chunk = AccessTools.TypeByName(ChunkType);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(chunk)) return code;
        var dictionary = typeof(Dictionary<,>).MakeGenericType(typeof(long), chunk);
        var (clear, ctor) = (AccessTools.Method(dictionary, nameof(Dictionary<,>.Clear)),
            AccessTools.DeclaredConstructor(chunk, []));
        if (!NotNull(clear) || !NotNull(ctor)) return code;
        var (cleared, made) = (Il.Single(code, c => c.Calls(clear)), Il.Single(code, c => c.Is(OpCodes.Newobj, ctor)));
        if (!Assert(cleared >= 0) || !Assert(made > cleared)) return code;
        var retire = AccessTools.Method(typeof(PartitionReuse), nameof(Retire)).MakeGenericMethod(chunk);
        var take = AccessTools.Method(typeof(PartitionReuse), nameof(Take)).MakeGenericMethod(chunk);
        if (Il.Substitute(code, cleared, retire) && Il.Substitute(code, made, take)) _rewritten |= PartitionBit;
        return code;
    }

    internal static List<CodeInstruction> RewriteAdd(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~AddBit;
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        if (Assert(Il.SubstituteSingle(code, c => c.opcode == OpCodes.Newarr && Equals(c.operand, typeof(List<Entity>)),
                AccessTools.Method(typeof(PartitionReuse), nameof(TakeArray)))))
            _rewritten |= AddBit;
        return code;
    }

    internal static List<CodeInstruction> RewriteFetch(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~FetchBit;
        var ctor = AccessTools.DeclaredConstructor(typeof(List<Entity>), [typeof(int)]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor)) return code;
        if (Assert(Il.SubstituteSingle(code, c => c.Is(OpCodes.Newobj, ctor), AccessTools.Method(typeof(PartitionReuse), nameof(TakeList)))))
            _rewritten |= FetchBit;
        return code;
    }

    // Stands in for partitions.Clear() at the start of every PartitionEntities
    internal static void Retire<TChunk>(Dictionary<long, TChunk> partitions) where TChunk : class, new()
    {
        if (!NotNull(partitions)) return;
        Spares.Current = null;
        if (Enabled)
        {
            var pool = Pools<TChunk>.Of(partitions);
            pool.Recycle();
            pool.Remember(partitions);
            Spares.Current = pool;
        }
        else Pools<TChunk>.Forget(partitions);

        partitions.Clear();
        _ = Assert(partitions.Count == 0);
    }

    // Finalizer on PartitionEntities: nothing is pooled outside it; a void finalizer rethrows untouched
    internal static void Close() => Spares.Current = null;

    internal static TChunk Take<TChunk>() where TChunk : class, new()
    {
        if (!Enabled || Spares.Current is not Pool<TChunk> pool || !pool.Free.TryPop(out var chunk))
            return new TChunk();
        if (Counting.Hud) _ = Interlocked.Increment(ref _reused);
        return chunk;
    }

    internal static List<Entity>[] TakeArray(int length)
    {
        if (!Enabled || Spares.Current is not { } spares || !spares.Arrays.TryPeek(out var array) ||
            array.Length != length) return new List<Entity>[length];
        if (Counting.Hud) _ = Interlocked.Increment(ref _reused);
        return spares.Arrays.Pop();
    }

    internal static List<Entity> TakeList(int capacity)
    {
        if (!Enabled || Spares.Current is not { } spares || !spares.Lists.TryPop(out var list))
            return new List<Entity>(capacity);
        if (Counting.Hud) _ = Interlocked.Increment(ref _reused);
        return list;
    }

    private class Spares
    {
        [ThreadStatic] internal static Spares? Current;
        public readonly Stack<List<Entity>[]> Arrays = new();
        public readonly Stack<List<Entity>> Lists = new();

        protected void Empty(List<Entity>[] array)
        {
            if (!NotNull(array)) return;
            for (var i = 0; i < Math.Min(array.Length, MaxCells); i++)
            {
                if (array[i] is not { } list) continue;
                list.Clear();
                if (Lists.Count < MaxLists) Lists.Push(list);
            }

            Array.Clear(array);
            _ = Assert(array.Length > MaxCells || Array.TrueForAll(array, l => l is null));
        }

        protected void Keep(List<Entity>[] array)
        {
            Empty(array);
            if (Arrays.Count < MaxArrays) Arrays.Push(array);
        }
    }

    private sealed class Pool<TChunk> : Spares where TChunk : class
    {
        public readonly Stack<TChunk> Free = new();

        private readonly HashSet<TChunk> _last = new(ReferenceEqualityComparer.Instance);

        // The call before last's chunks are out of the dictionary and all hands; InanimateEntities goes to the pool
        public void Recycle()
        {
            using var chunks = _last.GetEnumerator();
            for (var i = 0; i < MaxChunks && chunks.MoveNext(); i++)
            {
                var chunk = chunks.Current;
                if (Access<TChunk>.Entities(chunk) is { } entities) Empty(entities);
                ref var inanimate = ref Access<TChunk>.Inanimate(chunk);
                if (inanimate is not null) Keep(inanimate);
                inanimate = null;
                if (Free.Count < MaxChunks) Free.Push(chunk);
            }

            _last.Clear();
            _ = Assert(Free.Count <= MaxChunks);
        }

        public void Remember(Dictionary<long, TChunk> partitions)
        {
            using var chunks = partitions.Values.GetEnumerator();
            for (var i = 0; i < MaxChunks && chunks.MoveNext(); i++)
                if (chunks.Current is { } chunk) _ = _last.Add(chunk);
            _ = Assert(_last.Count <= partitions.Count);
        }
    }

    private static class Pools<TChunk> where TChunk : class
    {
        private static readonly ConditionalWeakTable<Dictionary<long, TChunk>, Pool<TChunk>> Table = [];

        public static Pool<TChunk> Of(Dictionary<long, TChunk> partitions)
        {
            var pool = Table.GetValue(partitions, static _ => new Pool<TChunk>());
            return NotNull(pool) ? pool : new Pool<TChunk>();
        }

        public static void Forget(Dictionary<long, TChunk> partitions)
        {
            if (NotNull(partitions)) _ = Table.Remove(partitions);
        }
    }

    private static class Access<TChunk> where TChunk : class
    {
        public static readonly AccessTools.FieldRef<TChunk, List<Entity>[]> Entities =
            AccessTools.FieldRefAccess<TChunk, List<Entity>[]>("Entities");

        public static readonly AccessTools.FieldRef<TChunk, List<Entity>[]> Inanimate =
            AccessTools.FieldRefAccess<TChunk, List<Entity>[]>("InanimateEntities");
    }
}
