using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Komet.World;

// GenerateChunkImage takes packUnpackLock per UnpackAndReadBlock, making the map thread the most contended of a join
// (trace, 2026-10-08: ~2100 samples in Monitor.Enter). Here a column chunk's first read locks once and gathers every
// cell the piece can ask (rain height map); other reads go to the engine. Never one snapshot, as in the engine.
internal static class MapReads
{
    private const int Size = 32, Cells = Size * Size * Size, Columns = Size * Size, Layer = 3, MaxColumn = 64, MaxInstructions = 4096;
    private const string Map = "Vintagestory.GameContent.ChunkMapLayer";

    [ThreadStatic] private static Batch? _batch;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        if (!NotNull(harmony) || Target() is not { } target || !Fields()) return;
        _ = NotNull(harmony.Patch(target, new HarmonyMethod(Begin), transpiler: new HarmonyMethod(Rewrite),
            finalizer: new HarmonyMethod(End)));
    }

    internal static MethodInfo? Target()
    {
        var type = AccessTools.TypeByName(Map);
        var method = type is null
            ? null
            : AccessTools.DeclaredMethod(type, "GenerateChunkImage", [typeof(FastVec2i), typeof(IMapChunk), typeof(bool)]);
        return method is null || Assert(method.ReturnType == typeof(int[])) ? method : null;
    }

    private static bool Fields() =>
        AccessTools.DeclaredField(typeof(WorldChunk), "packUnpackLock")?.FieldType == typeof(object) &&
        AccessTools.DeclaredField(typeof(WorldChunk), "chunkdata")?.FieldType == typeof(ChunkData) &&
        AccessTools.DeclaredMethod(typeof(WorldChunk), "unpackNoLock", []) is not null &&
        Target()?.DeclaringType is { } map && AccessTools.DeclaredField(map, "chunksTmp")?.FieldType == typeof(IWorldChunk[]) &&
        Assert(Layer < 4);

    // Every IWorldChunk.UnpackAndReadBlock(int, int) in the method, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (read, mine) = (AccessTools.Method(typeof(IWorldChunk), nameof(IWorldChunk.UnpackAndReadBlock), [typeof(int), typeof(int)]),
            AccessTools.Method(typeof(MapReads), nameof(Read)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(read) || !NotNull(mine)) return code;
        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (!code[i].Calls(read)) continue;
            if (!Il.Substitute(code, i, mine)) return code;
            sites++;
        }

        Rewritten = sites > 0;
        return code;
    }

    // The piece's column (chunksTmp, filled by the method) and its height map
    private static void Begin(IWorldChunk[]? ___chunksTmp, IMapChunk? mc)
    {
        var batch = _batch ??= new Batch();
        batch.Start(Enabled && Rewritten ? ___chunksTmp : null, mc);
        _ = Assert(___chunksTmp is null || ___chunksTmp.Length <= MaxColumn); // MapSizeY / 32
    }

    private static Exception? End(Exception? __exception)
    {
        _batch?.Start(null, null);
        _ = Assert(_batch is not { Active: true });
        return __exception;
    }

    // In place of chunk.UnpackAndReadBlock(index, layer)
    internal static int Read(IWorldChunk chunk, int index, int layer)
    {
        var batch = _batch;
        if (batch is not { Active: true } || layer != Layer || chunk is not WorldChunk world || !Index(index, Cells) ||
            batch.Find(world) is not { } slot || !slot.Has(index))
            return chunk.UnpackAndReadBlock(index, layer);
        return slot.Ids[index];
    }

    private sealed class Batch
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packUnpackLock")]
        private static extern ref object Gate(WorldChunk chunk);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkdata")]
        private static extern ref ChunkData? Data(WorldChunk chunk);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "unpackNoLock")]
        private static extern void Unpack(WorldChunk chunk);

        private readonly Slot?[] _slots = new Slot?[MaxColumn];
        private IWorldChunk[]? _column;
        private int[]? _heights, _heightsBuffer;
        private int _generation;

        public bool Active => _column is not null;

        public void Start(IWorldChunk[]? column, IMapChunk? map)
        {
            _generation++;
            (_column, _heights) = (null, null);
            if (column is null || map?.RainHeightMap is not { Length: >= Columns } heights || !Assert(column.Length <= MaxColumn))
                return;
            var copy = _heightsBuffer ??= new int[Columns];
            for (var k = 0; k < Columns; k++) copy[k] = heights[k];
            (_column, _heights) = (column, copy);
        }

        // The chunk's gathered reads, gathering on a column chunk's first read
        public Slot? Find(WorldChunk chunk)
        {
            if (_column is not { } column || _heights is not { } heights || !NotNull(chunk)) return null;
            _ = Assert(_generation > 0); // Start ran
            for (var y = 0; y < Math.Min(column.Length, MaxColumn); y++)
            {
                if (!ReferenceEquals(column[y], chunk)) continue;
                var slot = _slots[y] ??= new Slot();
                if (slot.Generation == _generation) return slot.Gathered ? slot : null;
                slot.Generation = _generation;
                slot.Gathered = Gather(chunk, y, heights, slot);
                return slot.Gathered ? slot : null;
            }

            return null;
        }

        // Under the lock once: unpack as UnpackAndReadBlock does, then read every cell the piece can ask of chunk y
        private bool Gather(WorldChunk chunk, int y, int[] heights, Slot slot)
        {
            if (chunk.Disposed || Gate(chunk) is not { } gate || !Index(y, MaxColumn)) return false; // the engine answers 0 for a disposed one
            slot.Stamp = _generation;
            lock (gate)
            {
                if (chunk.Disposed) return false;
                Unpack(chunk);
                if (Data(chunk) is not { } data) return false;
                for (var k = 0; k < Columns; k++)
                {
                    var (h, x, z) = (heights[k], k % Size, k / Size);
                    if (h / Size == y)
                    {
                        var at = h % Size;
                        slot.Take(data, Cell(x, at, z));
                        if (x > 0) slot.Take(data, Cell(x - 1, at, z));
                        if (x < Size - 1) slot.Take(data, Cell(x + 1, at, z));
                        if (z > 0) slot.Take(data, Cell(x, at, z - 1));
                        if (z < Size - 1) slot.Take(data, Cell(x, at, z + 1));
                    }

                    if (h > 0 && (h - 1) / Size == y) slot.Take(data, Cell(x, (h - 1) % Size, z)); // under snow
                }
            }

            return true;
        }

        // MapUtil.Index3d(x, y, z, 32, 32)
        private static int Cell(int x, int y, int z)
        {
            _ = Assert(x is >= 0 and < Size) && Assert(y is >= 0 and < Size) && Assert(z is >= 0 and < Size);
            return (y * Size + z) * Size + x;
        }
    }

    // The block ids read from one chunk, valid where the stamp is the batch's
    private sealed class Slot
    {
        public readonly int[] Ids = new int[Cells];
        private readonly int[] _stamps = new int[Cells];
        public int Stamp, Generation;
        public bool Gathered;

        public void Take(ChunkData data, int index)
        {
            if (!Index(index, Cells) || !Assert(Stamp > 0) || _stamps[index] == Stamp) return;
            Ids[index] = data.GetBlockId(index, Layer);
            _stamps[index] = Stamp;
        }

        public bool Has(int index) => Index(index, Cells) && Assert(Stamp > 0) && _stamps[index] == Stamp;
    }
}
