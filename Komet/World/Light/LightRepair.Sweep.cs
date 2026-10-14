using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.World;

// Catches what the load misses (broken while loaded, loaded before install, left by a relight); every 200 ms,
// MaxRepairs per tick.
internal static partial class LightRepair
{
    private const int ColumnsPerTick = 256, MaxRepairs = 2;

    private static long[] _sweep = [];
    private static int _cursor;
    private static IWorldChunk[] _scratch = [];

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "RunPhase")]
    private static extern ref EnumServerRunPhase RunPhase(ServerMain server);

    internal static void Tick(ServerSystem __instance)
    {
        if (!Enabled || _failed || !NotNull(__instance)) return;
        try
        {
            var server = Server(__instance);
            if (server?.WorldMap is { } map && RunPhase(server) == EnumServerRunPhase.RunGame) Sweep(server, map);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(e);
        }
    }

    private static void Sweep(ServerMain server, ServerWorldMap map)
    {
        var illuminator = map.chunkIlluminatorMainThread;
        if (!NotNull(illuminator)) return;
        Action<int, int, int> changed = (x, y, z) => Changed(map, x, y, z);
        var repairs = RelightWaiting(illuminator, map, MaxRepairs, changed);
        if (_cursor >= _sweep.Length) (_sweep, _cursor) = (server.LoadedMapChunkIndices, 0);
        for (var i = 0; i < Math.Min(_sweep.Length - _cursor, ColumnsPerTick) && repairs < MaxRepairs; i++)
        {
            var index = _sweep[_cursor++];
            var (x, z) = ((int)(index % map.ChunkMapSizeX), (int)(index / map.ChunkMapSizeX));
            if (Column(map, x, z, 0, Scratch(illuminator)) < _scratch.Length || !Array.Exists(_scratch, Missing)) continue;
            if (Repair(illuminator, map, x, z, map.GetMapChunk(x, z)?.RainHeightMap, changed) > 0) repairs++;
        }

        Array.Clear(_scratch); // no chunk held past the tick
    }

    // A partly loaded column waits, an unloaded one is dropped; unpacked first, as a packed one has no Lighting
    internal static int RelightWaiting(ChunkIlluminator illuminator, IChunkProvider chunks, int max,
        Action<int, int, int> changed)
    {
        if (!NotNull(illuminator) || !NotNull(chunks) || !NotNull(changed)) return 0;
        var (done, count) = (0, Waiting.Count);
        for (var i = 0; i < Math.Min(count, ColumnsPerTick) && done < max; i++)
        {
            var (dimension, x, z) = Waiting.Dequeue();
            var column = Scratch(illuminator);
            var loaded = Column(chunks, x, z, dimension, column);
            if (loaded > 0 && loaded < column.Length) Waiting.Enqueue((dimension, x, z));
            if (loaded < column.Length) continue;
            foreach (var chunk in column.Bounded(MaxChunks))
            {
                chunk.Unpack();
                chunk.Lighting.ClearLight();
            }

            Relit(illuminator, chunks, x, z, dimension, column, [.. Enumerable.Range(0, column.Length)], changed);
            done++;
        }

        return done;
    }

    internal static int Repair(ChunkIlluminator illuminator, IChunkProvider chunks, int x, int z, ushort[]? rain,
        Action<int, int, int> changed)
    {
        if (!NotNull(illuminator) || !NotNull(chunks) || !NotNull(changed)) return 0;
        var column = new IWorldChunk[Scratch(illuminator).Length];
        if (Column(chunks, x, z, 0, column) < column.Length) return 0;
        var broken = Broken(column, rain);
        if (broken.Length == 0) return 0;
        Relit(illuminator, chunks, x, z, 0, column, broken, changed);
        return broken.Length;
    }

    private static IWorldChunk[] Scratch(ChunkIlluminator illuminator)
    {
        var height = Math.Min(MapY(illuminator) / Size, MaxChunks);
        if (_scratch.Length != height) _scratch = new IWorldChunk[height];
        return Assert(height > 0) ? _scratch : [];
    }

    private static int Column(IChunkProvider chunks, int x, int z, int dimension, IWorldChunk[] column)
    {
        if (!NotNull(column) || column.Length == 0) return -1;
        var loaded = 0;
        for (var y = 0; y < Math.Min(column.Length, MaxChunks); y++)
        {
            column[y] = chunks.GetChunk(x, y + dimension * 1024, z);
            if (column[y] is not null) loaded++;
        }

        return Assert(loaded <= column.Length) ? loaded : -1;
    }

    private static void Relit(ChunkIlluminator illuminator, IChunkProvider chunks, int x, int z, int dimension,
        IWorldChunk[] column, int[] lit, Action<int, int, int> changed)
    {
        if (!Assert(lit.Length > 0)) return;
        var flooded = Sunlight(illuminator, column, x, z, dimension);
        var touched = BlockLight(illuminator, chunks, x, z, lit, dimension);
        Report(chunks, x, z, dimension, column.Length, flooded, touched, changed);
        Count(lit.Length);
    }

    private static void Report(IChunkProvider chunks, int x, int z, int dimension, int height, byte flooded,
        HashSet<long> touched, Action<int, int, int> changed)
    {
        var offset = dimension * 1024;
        for (var y = 0; y < Math.Min(height, MaxChunks); y++) changed(x, y + offset, z);
        foreach (var face in BlockFacing.HORIZONTALS.Bounded(4))
        {
            if ((flooded & face.Flag) == 0) continue;
            for (var y = 0; y < Math.Min(height, MaxChunks); y++)
                changed(x + face.Normali.X, y + offset, z + face.Normali.Z);
        }

        foreach (var index in touched.Bounded(MaxSources))
        {
            if (!NotNull(chunks) || chunks is not WorldMap map) break;
            var pos = map.ChunkPosFromChunkIndex3D(index);
            changed(pos.X, pos.InternalY, pos.Z);
        }
    }
}
