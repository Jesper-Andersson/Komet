using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Deep chunks hold cave walls nobody sees: at view distance 1536, 31 % of quads lay below block 96, 19 % two chunks
// below their 3x3 columns' lowest top chunk. A chunk Margin below all nine columns' surface (min of rain and worldgen
// height) and farther than Near (FarUnder with the player underground) tessellates empty like OccludedChunks. The main
// thread tracks Surfaces and the player and re-marks chunks changing side, with hysteresis.
internal static class DeepCulling
{
    private const int Size = 32, Margin = 16, Near = 96, FarUnder = 256, Band = 48, Dimension = 1024, MaxMarks = 192;
    private const int ColumnsPerScan = 512, MaxTracked = 1 << 22;
    private const int Full = 1, Culled = 2;

    private static readonly ConcurrentDictionary<long, short> Surfaces = new(); // column -> lowest surface block
    private static readonly ConcurrentDictionary<long, byte> Tracked = new(); // deep chunk -> how tessellated
    private static readonly ConcurrentDictionary<long, long> Pending = new();
    private static ICoreClientAPI? _api;
    private static long[] _columns = [], _chunks = [];
    private static int _chunkCursor, _chunkCount, _columnCount;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunks")]
    private static extern ref Dictionary<long, ClientChunk>? Chunks(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunksLock")]
    private static extern ref object? ChunksLock(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "MapChunks")]
    private static extern ref Dictionary<long, Vintagestory.Client.ClientMapChunk>? MapChunks(ClientWorldMap map);
    private static long _tick, _columnCursor;
    private static double _x, _z, _near = double.MaxValue;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static int CulledChunks { get; private set; }

    public static void Install(Harmony harmony, ICoreClientAPI api)
    {
        Installed = false;
        var pass = AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.NowProcessChunk),
            [typeof(int), typeof(int), typeof(int), typeof(TesselatedChunk), typeof(bool)]);
        if (!NotNull(harmony) || !NotNull(api) || pass is null) return;
        _ = NotNull(harmony.Patch(pass, new HarmonyMethod(typeof(DeepCulling), nameof(Prefix)) { priority = Priority.High }));
        _api = api;
        _tick = api.Event.RegisterGameTickListener(Scan, 500);
        Installed = true;
    }

    public static void Stop()
    {
        if (_api is { } api && _tick != 0) api.Event.UnregisterGameTickListener(_tick);
        (_api, _tick, Installed, _near) = (null, 0, false, double.MaxValue);
        Surfaces.Clear();
        Tracked.Clear();
        Pending.Clear();
        _ = Assert(Tracked.IsEmpty);
    }

    private static long Column(int cx, int cz) => Assert(cx >= -1 && cz >= -1) ? ((long)cz << 32) | (uint)cx : 0;

    private static long Key(int x, int y, int z) =>
        !Assert(y < 1 << 20) || x < 0 || y < 0 || z < 0 ? -1 : ((long)y << 42) | ((long)(z & 0x1FFFFF) << 21) | (long)(x & 0x1FFFFF);

    // The chunk's top lies Margin below the surface of the nine columns around it (false where one is not known yet)
    internal static bool Deep(int cx, int cy, int cz)
    {
        if (cy >= Dimension || !Assert(cx >= 0 && cz >= 0)) return false;
        var lowest = int.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!Surfaces.TryGetValue(Column(cx + dx, cz + dz), out var surface)) return false;
                lowest = Math.Min(lowest, surface);
            }

        return cy * Size + Size - 1 < lowest - Margin;
    }

    private static double Distance(int cx, int cz, double x, double z)
    {
        double dx = cx * Size + Size / 2 - x, dz = cz * Size + Size / 2 - z;
        return Assert(double.IsFinite(dx)) ? Math.Sqrt(dx * dx + dz * dz) : 0;
    }

    // Culled when deep and the player far; a culled chunk stays so until Band nearer (and the reverse)
    internal static bool Cull(int state, double distance, double near)
    {
        if (!double.IsFinite(near) || !Assert(state is >= 0 and <= Culled)) return false;
        return state == Culled ? distance > near : distance > near + Band;
    }

    // NowProcessChunk: a deep chunk far from the player is left without geometry, as an enclosed one
    private static bool Prefix(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, TesselatedChunk tessChunk,
        bool skipChunkCenter, ref int __result)
    {
        if (!Enabled || !Installed || !NotNull(tessChunk) || !OccludedChunks.Installed || OccludedChunks.StoodDown) return true;
        var key = Key(chunkX, chunkY, chunkZ);
        if (key < 0 || !Deep(chunkX, chunkY, chunkZ))
        {
            if (key >= 0) _ = Tracked.TryRemove(key, out _);
            return true;
        }

        var state = Tracked.TryGetValue(key, out var was) ? was : 0;
        var cull = Cull(state, Distance(chunkX, chunkZ, Volatile.Read(ref _x), Volatile.Read(ref _z)), Volatile.Read(ref _near));
        Tracked[key] = cull ? (byte)Culled : (byte)Full;
        _ = Pending.TryRemove(key, out _);
        if (!cull) return true;
        __result = OccludedChunks.Apply(__instance, chunkX, chunkY, chunkZ, tessChunk, skipChunkCenter);
        return !Assert(__result == 0);
    }

    // The main thread: the player, a share of the columns' surfaces, and the chunks to tessellate again
    private static void Scan(float dt)
    {
        if (_api is not { World.Player.Entity: { } entity } api || api.World is not ClientMain game || !Assert(dt >= 0)) return;
        var (px, py, pz) = (entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        var under = py < SurfaceAt(game, (int)px, (int)pz) - 2;
        Volatile.Write(ref _x, px);
        Volatile.Write(ref _z, pz);
        var near = under ? FarUnder : Near;
        Volatile.Write(ref _near, Enabled ? near : double.MaxValue);
        _ = Assert(near > 0) && Assert(Margin > 0);
        Refresh(game);
        var (marks, now, culled) = (0, Environment.TickCount64, 0);
        var busy = TessSeams.Backlogged;
        using var entries = Tracked.GetEnumerator();
        for (var n = 0; n < MaxTracked && entries.MoveNext(); n++)
        {
            var (key, state) = entries.Current;
            culled += state == Culled ? 1 : 0;
            var (x, y, z) = ((int)(key & 0x1FFFFF), (int)(key >> 42), (int)((key >> 21) & 0x1FFFFF));
            var want = Enabled && Cull(state, Distance(x, z, px, pz), _near) && Deep(x, y, z); // distance first: cheap
            if (want == (state == Culled) || marks >= MaxMarks || (want && busy)) continue; // waits for the backlog
            if (Pending.TryGetValue(key, out var since) && now - since < 20_000) continue;
            if (api.World.BlockAccessor.GetChunk(x, y, z) is null)
            {
                _ = Tracked.TryRemove(key, out _);
                continue;
            }

            Pending[key] = now;
            game.WorldMap.MarkChunkDirty(x, y, z, fireEvent: false);
            marks++;
        }

        CulledChunks = culled;
        if (Enabled && !busy) marks = Sweep(game, px, pz, marks, now);
        _ = Assert(culled >= 0);
        _ = Assert(marks <= MaxMarks);
    }

    // Chunks tessellated before their surfaces were known (a world's first) are untracked: a share per scan is redone
    private static int Sweep(ClientMain game, double px, double pz, int marks, long now)
    {
        var map = game.WorldMap;
        if (!NotNull(map) || Chunks(map) is not { } chunks || ChunksLock(map) is not { } gate) return marks;
        if (_chunkCursor >= _chunkCount)
        {
            // the chunk loader holds the lock for whole batches at a join: the main thread does not wait, the next scan asks again
            if (!Monitor.TryEnter(gate)) return marks;
            try
            {
                _chunkCount = Snapshot(chunks.Keys, ref _chunks);
            }
            finally
            {
                Monitor.Exit(gate);
            }

            _chunkCursor = 0;
        }

        for (var n = 0; n < 4096 && _chunkCursor < _chunkCount && marks < MaxMarks; n++, _chunkCursor++)
        {
            var pos = map.ChunkPosFromChunkIndex3D(_chunks[_chunkCursor]);
            var key = Key(pos.X, pos.Y + pos.Dimension * Dimension, pos.Z);
            if (pos.Dimension != 0 || key < 0 || Tracked.ContainsKey(key) || Pending.ContainsKey(key) || !Deep(pos.X, pos.Y, pos.Z) ||
                !Cull(0, Distance(pos.X, pos.Z, px, pz), _near)) continue;
            Pending[key] = now;
            map.MarkChunkDirty(pos.X, pos.Y, pos.Z, fireEvent: false);
            marks++;
        }

        return Assert(marks <= MaxMarks + 1) ? marks : MaxMarks;
    }

    // The keys copied into a buffer kept for the next snapshot: no new array (on the large object heap) every round.
    // The game's network thread adds map chunks without a lock: a copy that meets one growing is skipped (0), the next
    // scan copies again.
    private static int Snapshot<T>(Dictionary<long, T>.KeyCollection keys, ref long[] buffer)
    {
        _ = NotNull(keys) && Assert(buffer.Length <= 1 << 26);
        var count = keys.Count;
        if (buffer.Length < count + 256) buffer = new long[Math.Max(count + 256, buffer.Length * 2)];
        try
        {
            keys.CopyTo(buffer, 0);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return 0;
        }

        return Math.Min(count, buffer.Length);
    }

    // The surface (lower of rain and worldgen terrain height) at a block column, NaN-free: int.MaxValue where unknown
    private static int SurfaceAt(ClientMain game, int x, int z)
    {
        if (game.WorldMap.GetMapChunk(x / Size, z / Size) is not { } map || !Assert(x >= 0 || z >= 0)) return int.MaxValue;
        var i = (z % Size) * Size + x % Size;
        _ = Assert(i >= 0) && Assert(i < Size * Size);
        var rain = map.RainHeightMap is { Length: Size * Size } r ? r[i] : int.MaxValue;
        var terrain = map.WorldGenTerrainHeightMap is { Length: Size * Size } t ? t[i] : int.MaxValue;
        return Math.Min(rain, terrain);
    }

    // A share of the loaded columns' lowest surface, round robin over a snapshot (MapChunks is main-thread only)
    private static void Refresh(ClientMain game)
    {
        var map = game.WorldMap;
        if (!NotNull(map) || MapChunks(map) is not { } columns) return;
        if (_columnCursor >= _columnCount) (_columnCount, _columnCursor) = (Snapshot(columns.Keys, ref _columns), 0);
        var sizeX = Math.Max(1, map.ChunkMapSizeX);
        for (var n = 0; n < ColumnsPerScan && _columnCursor < _columnCount; n++, _columnCursor++)
        {
            var key = _columns[_columnCursor];
            if (!columns.TryGetValue(key, out var found) || found is not IMapChunk column) continue;
            var (rain, terrain) = (column.RainHeightMap, column.WorldGenTerrainHeightMap);
            var lowest = int.MaxValue;
            for (var i = 0; i < Size * Size; i++)
            {
                if (rain is { Length: Size * Size }) lowest = Math.Min(lowest, rain[i]);
                if (terrain is { Length: Size * Size }) lowest = Math.Min(lowest, terrain[i]);
            }

            if (lowest < int.MaxValue) Surfaces[Column((int)(key % sizeX), (int)(key / sizeX))] = (short)Math.Min(lowest, short.MaxValue);
        }

        _ = Assert(_columnCursor <= _columnCount);
    }
}
