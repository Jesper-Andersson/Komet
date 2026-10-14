using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Gpu;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static System.BitConverter;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Opaque terrain culled from GPU-kept rows (rows.comp), one per location, instead of CPU frustum culling. Lists change
// only via MeshDataPool.TryAdd/RemoveLocation (one Insert, Add or RemoveAt, MeshPool's too). An added location waits
// until its pool draws (AddModelAndStoreLocation sets LOD and chunk after TryAdd); a removed one gives its row to the
// region's last. Other version moves rewrite the list. Hide arrives via its three engine writers; chunks' CullVisible
// bits (culler thread) are read at the frame's first call and when the visible buffer swaps.
internal static partial class OcclusionCulling
{
    public const int MaxRows = 1 << 19;
    private const int MaxChunks = 1 << 16, RowUints = 12 + 6 * FaceSorting.Slabs, RowBytes = 4 * RowUints, MaxRuns = 4096;
    private const int MaxPoolRows = 65536, MaxChunkLocations = 4096, AboveLod = 4, BelowLod = 5, MinCapacity = 256,
        MaxBlocks = 8192;
    private const int RowsBinding = 9, ChunksBinding = 10, PoolsBinding = 11, FrustumBinding = 15, FrustumDoubles = 68;
    private const int KeysBinding = 12, SortedBinding = 13, Casters = 44; // the camera's planes from this double of the block on
    private const float Sqrt3 = FrustumSweep.Sqrt3;
    private const uint SortedBit = 1 << 4; // the row's faces are sorted by direction (FaceSorting)
    private const int SlabSteps = 4; // a slab's planes in quarter blocks

    private static readonly uint[] RowData = new uint[MaxRows * RowUints];
    private static readonly ModelDataPoolLocation?[] RowLocations = new ModelDataPoolLocation?[MaxRows];
    private static readonly Dictionary<ModelDataPoolLocation, int> RowOf = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<MeshDataPool, Region> Regions = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Bools, int> ChunkOf = new(ReferenceEqualityComparer.Instance);
    private static readonly Bools?[] ChunkBools = new Bools?[MaxChunks];
    private static readonly int[] ChunkRefs = new int[MaxChunks];
    private static readonly uint[] ChunkBits = new uint[MaxChunks];
    private static readonly Stack<int> FreeChunks = [];
    private static readonly List<(int Start, int Count)> Dirty = [];
    private static readonly uint[] PoolRows = new uint[4 * MaxDraws]; // per pool draw: first row, row count
    private static readonly List<(int Base, int Capacity)> FreeBlocks = []; // rows let go, for the next region
    private static int _rowsProgram, _rowBuffer, _chunkBuffer, _poolBuffer, _frustumBuffer, _sortProgram, _keys, _sorted;
    private static int _rowsUsed, _chunksUsed = 1, _chunksFrom = int.MaxValue, _chunksBefore, _chunkFrame = -1, _chunkIndex;
    private static int _callMaxRows, _surfaceMaxRows, _mainThread;
    private static bool _rowsReady, _rowsFrame, _rowsCall, _rescanHide, _surfaceRows; // the surfaces were culled from rows
    private static bool _relayout; // a region found no room: the next frame lays every region out anew
    private static FrustumCulling? _rowsCuller; // the last call's, for the surfaces' second pass

    // Call culls back faces: sorted rows skip away-facing directions (up to Runs commands each)
    private static bool _backFaces, _surfaceBackFaces = true;
    private const int Runs = 4;

    private sealed class Region
    {
        public int Base, Capacity, Count, Version, Triangles, Vao; // Vao: the pool's, for FaceSorting's groups
        public Vec3i? Placed { get; set; } // the pool's origin (its vertices are relative to it)
        public List<ModelDataPoolLocation>? Owner;
        public readonly List<ModelDataPoolLocation> Waiting = []; // added since the pool was last drawn
    }

    public static bool RowsEnabled { get; set; } = true;

    // Only while Vulkan draws the terrain: under OpenGL (radeonsi, VD 1024) rows were 2.1 ms slower than no occlusion
    // culling, the pyramid path at parity (glrows-*), so OpenGL keeps the engine's culling
    private static bool VulkanDraws => Drawer is not null;

    private static bool RowsWanted => RowsEnabled && VulkanDraws;

    // Switched on and programs built (needs compute doubles)
    internal static bool RowsActive => Active && RowsWanted && _rowsReady;

    // Rows the regions take, let go ones included
    public static int RowsUsed => _rowsUsed;

    // Regions written whole (first draw, or a change past TryAdd/RemoveLocation)
    public static long Rewrites { get; private set; }

    public static long Resyncs { get; private set; }

    private static readonly int[] Marks = new int[MaxRows];
    private static int _mark;

    // Current Render call is culled from rows: FrustumCull is the GPU's
    internal static bool Bypassing => _rowsCall;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "data")]
    private static extern ref int Bits(Bools bools);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModelPoolLocations")]
    private static extern ref ModelDataPoolLocation[]? Center(ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModelPoolLocations")]
    private static extern ref ModelDataPoolLocation[]? Edge(ClientChunk chunk);

    private static void InstallRows(Harmony harmony)
    {
        _mainThread = Environment.CurrentManagedThreadId;
        var self = typeof(OcclusionCulling);
        var cull = AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull));
        var shown = AccessTools.DeclaredMethod(typeof(ClientChunk), nameof(ClientChunk.SetVisibility));
        var set = AccessTools.DeclaredMethod(typeof(ClientChunk), nameof(ClientChunk.SetPoolLocations));
        var removed = AccessTools.DeclaredMethod(typeof(MeshDataPoolMasterManager),
            nameof(MeshDataPoolMasterManager.RemoveDataPoolLocations));
        if (!NotNull(harmony) || !NotNull(cull) || !NotNull(shown) || !NotNull(set) || !NotNull(removed) ||
            !Assert(AccessTools.Field(typeof(ClientChunk), "centerModelPoolLocations") != null) ||
            !Assert(AccessTools.Field(typeof(Bools), "data")?.FieldType == typeof(int))) return;
        _ = NotNull(harmony.Patch(cull,
            new HarmonyMethod(AccessTools.Method(self, nameof(Bypassed))) { priority = Priority.First }));
        _ = NotNull(harmony.Patch(shown, postfix: new HarmonyMethod(AccessTools.Method(self, nameof(ChunkShown)))));
        var located = new HarmonyMethod(AccessTools.Method(self, nameof(Located)));
        _ = NotNull(harmony.Patch(set, postfix: located));
        _ = NotNull(harmony.Patch(removed, postfix: located));
        var (add, remove) = (AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd)),
            AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation)));
        if (!NotNull(add) || !NotNull(remove)) return;
        var before = new HarmonyMethod(AccessTools.Method(self, nameof(Changing))) { priority = Priority.First };
        _ = NotNull(harmony.Patch(add, before, new HarmonyMethod(AccessTools.Method(self, nameof(Added)))));
        _ = NotNull(harmony.Patch(remove, before, new HarmonyMethod(AccessTools.Method(self, nameof(Removed)))));
    }

    // Before every other prefix: version before the change
    internal static void Changing(MeshDataPool __instance, out int __state)
    {
        __state = -1;
        if (NotNull(__instance) && Locations(__instance) is { } list) __state = Version(list);
    }

    internal static void Added(MeshDataPool __instance, ModelDataPoolLocation? __result, int __state)
    {
        if (__result is null || !Regions.TryGetValue(__instance, out var region) || !Current(region, __instance, __state))
            return;
        region.Waiting.Add(__result);
        _ = Assert(region.Waiting.Count <= MaxPoolRows);
    }

    internal static void Removed(MeshDataPool __instance, ModelDataPoolLocation location, int __state)
    {
        if (location is null || !Regions.TryGetValue(__instance, out var region) || !Current(region, __instance, __state))
            return;
        if (RowOf.TryGetValue(location, out var row) && row >= region.Base && row < region.Base + region.Count)
            Unrow(region, row);
        else _ = region.Waiting.Remove(location);
    }

    // Region was current before the change (then after too, at the new version); main thread only
    private static bool Current(Region region, MeshDataPool pool, int before)
    {
        if (!ReferenceEquals(region.Owner, Locations(pool)) || region.Version != before || region.Owner is not { } list)
            return false;
        if (Environment.CurrentManagedThreadId != _mainThread) return false; // stale: written anew when drawn
        region.Version = Version(list);
        return Assert(list.Count <= MaxPoolRows);
    }

    private static void ReadyRows()
    {
        ForgetRows();
        _rowsReady = false;
        if (_logger is null || !Gpu.Doubles() || !Assert(_rowsProgram == 0)) return;
        (_rowsProgram, _sortProgram) = (Gpu.Program(GpuShaders.Rows, _logger), Gpu.Program(GpuShaders.Sort, _logger));
        (_rowBuffer, _chunkBuffer, _poolBuffer, _frustumBuffer) = (Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer());
        (_keys, _sorted) = (Gpu.Buffer(), Gpu.Buffer());
        Gpu.Upload<uint>(_rowBuffer, [], MaxRows * RowBytes);
        Gpu.Upload<uint>(_chunkBuffer, [], MaxChunks * 4);
        Gpu.Upload<uint>(_poolBuffer, [], 2 * MaxDraws * 8);
        Gpu.Upload<uint>(_keys, [], MaxRanges * 4); // the camera's first pass's commands only
        Gpu.Upload<uint>(_sorted, [], MaxRanges * CommandBytes);
        _rowsReady = _rowsProgram != 0 && Assert(6 * FaceSorting.Slabs < 31); // a verdict's slab bits below rows.comp's Whole
        _ = Assert(_rowBuffer != 0) && Assert(_chunkBuffer != 0);
    }

    private static void ReleaseRows()
    {
        Gpu.DeleteProgram(ref _rowsProgram);
        Gpu.DeleteProgram(ref _sortProgram);
        Gpu.DeleteBuffer(ref _keys);
        Gpu.DeleteBuffer(ref _sorted);
        Gpu.DeleteBuffer(ref _rowBuffer);
        Gpu.DeleteBuffer(ref _chunkBuffer);
        Gpu.DeleteBuffer(ref _poolBuffer);
        Gpu.DeleteBuffer(ref _frustumBuffer);
        _rowsReady = false;
        ForgetRows();
        _ = Assert(_rowsProgram == 0) && Assert(_rowBuffer == 0);
    }

    // TerrainRebuild: old pools went with their renderer; new ones get rows from the start
    internal static void Renewed()
    {
        ForgetRows();
        _ = Assert(Regions.Count == 0) && Assert(RowOf.Count == 0);
    }

    private static void ForgetRows()
    {
        Array.Clear(RowLocations, 0, Math.Min(_rowsUsed, MaxRows));
        Array.Clear(ChunkBools, 0, Math.Min(_chunksUsed, MaxChunks));
        Array.Clear(ChunkRefs, 0, Math.Min(_chunksUsed, MaxChunks));
        RowOf.Clear();
        Regions.Clear();
        FreeBlocks.Clear();
        ChunkOf.Clear();
        FreeChunks.Clear();
        Dirty.Clear();
        (_rowsUsed, _chunksUsed, _chunkFrame, _chunksFrom, _chunksBefore) = (0, 1, -1, int.MaxValue, 0);
        (_rowsCall, _rescanHide, _surfaceMaxRows, _shadowCall, _shadowFrame) = (false, false, 0, false, -1);
        _ = Assert(RowOf.Count == 0) && Assert(Regions.Count == 0);
    }

    // engineless: GPU tests, rows whatever the backend
    private static void BeginRows(bool engineless)
    {
        _ = Assert(!_rowsCall);
        _rowsFrame = (RowsWanted || engineless && RowsEnabled) && _rowsReady;
        (_surfaceMaxRows, _surfaceRows, _surfaceBackFaces) = (0, false, true);
        if (!_rowsFrame) return;
        if (_relayout) ForgetRows();
        _relayout = false;
        if (_rescanHide) Rescan();
        _ = Assert(_rowsUsed <= MaxRows);
    }

    // False (call not held) when a pool lacks room or the culler is not ready (no player: engine culling would throw)
    private static bool Rowed(FrustumCulling? culler, List<MeshDataPool> pools, bool on)
    {
        _callMaxRows = 0;
        if (!on || culler is null || PlayerPos(culler) is null || Planes(culler) is not { Length: >= 6 } ||
            !NotNull(pools)) return false;
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var pool = pools[i];
            if (Dimension(pool) != 0) continue;
            if (RegionOf(pool) is not { } region) return false;
            _callMaxRows = Math.Max(_callMaxRows, region.Count);
        }

        return Assert(_callMaxRows <= MaxPoolRows);
    }

    private static Region? RegionOf(MeshDataPool pool)
    {
        var locations = Locations(pool);
        if (!NotNull(locations) || locations.Count > MaxPoolRows) return null;
        var version = Version(locations);
        if (Regions.TryGetValue(pool, out var region) && ReferenceEquals(region.Owner, locations) &&
            region.Version == version)
            return region.Waiting.Count == 0 || Appended(region) ? region : null;
        if (region is not null && ReferenceEquals(region.Owner, locations) && Resynced(region, locations, version)) return region;
        if (region is null) Regions[pool] = region = new Region();
        (region.Vao, region.Placed) = (ModelRef(pool) is VAO vao ? vao.VaoId : 0, PoolOrigin(pool));
        Rewrites++;
        Unrow(region);
        region.Waiting.Clear();
        if (!Room(region, locations.Count)) return null;
        var span = CollectionsMarshal.AsSpan(locations);
        for (var i = 0; i < Math.Min(span.Length, MaxPoolRows); i++)
            if (span[i] is not { } location || !Append(region, location))
            {
                region.Owner = null; // its rows so far go when it comes again
                return null;
            }

        (region.Owner, region.Version) = (locations, version);
        return Assert(region.Count == locations.Count) ? region : null;
    }

    // Changed past TryAdd/RemoveLocation: patch only changed rows, not the whole region (up to 6 MB, a frame's stall)
    private static bool Resynced(Region region, List<ModelDataPoolLocation> locations, int version)
    {
        if (!Assert(region.Base + region.Count <= MaxRows) || ++_mark == int.MaxValue) return false;
        region.Waiting.Clear();
        var span = CollectionsMarshal.AsSpan(locations);
        for (var i = 0; i < Math.Min(span.Length, MaxPoolRows); i++)
        {
            if (span[i] is not { } location) return false;
            if (RowOf.TryGetValue(location, out var row) && row >= region.Base && row < region.Base + region.Count)
            {
                Marks[row] = _mark;
                Hide(row, location.Hide);
            }
            else region.Waiting.Add(location);
        }

        var (top, count) = (region.Base + region.Count - 1, region.Count); // Unrow shrinks Count
        _ = Assert(count <= MaxPoolRows) && Assert(region.Waiting.Count <= MaxPoolRows);
        for (var k = 0; k < Math.Min(count, MaxPoolRows); k++) // from the top: the last, already kept, takes a gap
            if (Marks[top - k] != _mark) Unrow(region, top - k);
        Resyncs++;
        if (!Appended(region)) return false;
        (region.Owner, region.Version) = (locations, version);
        return Assert(region.Count == locations.Count);
    }

    private static bool Appended(Region region)
    {
        if (!NotNull(region) || !Room(region, region.Count + region.Waiting.Count)) return false;
        foreach (var location in region.Waiting.Bounded(MaxPoolRows))
            if (!Append(region, location))
            {
                region.Owner = null; // written anew when drawn next
                return false;
            }

        region.Waiting.Clear();
        return Assert(region.Count <= region.Capacity);
    }

    private static bool Append(Region region, ModelDataPoolLocation location)
    {
        if (!Assert(region.Count < region.Capacity) || !Row(region.Base + region.Count, location, region)) return false;
        Changed(region.Base + region.Count, 1);
        region.Count++;
        region.Triangles += (location.IndicesEnd - location.IndicesStart) / 3;
        return Assert(region.Triangles >= 0);
    }

    // The region's own room, or a larger block (freed or past the last) its rows move to
    private static bool Room(Region region, int rows)
    {
        if (rows <= region.Capacity) return true;
        var (capacity, at) = (Math.Max(rows + rows / 4 + 64, MinCapacity), -1);
        for (var i = 0; i < Math.Min(FreeBlocks.Count, MaxBlocks) && at < 0; i++) // no closure
            if (FreeBlocks[i].Capacity >= capacity) at = i;
        int start;
        if (at >= 0)
        {
            (start, capacity) = FreeBlocks[at];
            FreeBlocks.RemoveAt(at);
        }
        else if (_rowsUsed + capacity <= MaxRows) (start, _rowsUsed) = (_rowsUsed, _rowsUsed + capacity);
        else
        {
            _relayout = true; // the free blocks are too small or scattered
            return false;
        }

        for (var i = 0; i < Math.Min(region.Count, MaxPoolRows); i++)
        {
            var (from, to) = (region.Base + i, start + i);
            RowData.AsSpan(from * RowUints, RowUints).CopyTo(RowData.AsSpan(to * RowUints, RowUints));
            if (RowLocations[from] is { } location) RowOf[location] = to;
            (RowLocations[to], RowLocations[from]) = (RowLocations[from], null);
        }

        if (region.Capacity > 0 && FreeBlocks.Count < MaxBlocks) FreeBlocks.Add((region.Base, region.Capacity));
        Changed(start, region.Count);
        (region.Base, region.Capacity) = (start, capacity);
        return Assert(region.Base + region.Capacity <= MaxRows);
    }

    // false when the chunk table is full
    private static bool Row(int row, ModelDataPoolLocation location, Region region)
    {
        if (!Index(row, MaxRows) || !NotNull(location)) return false;
        var chunk = ChunkSlot(location.CullVisible);
        if (chunk < 0) return false;
        var (at, sphere, lod) = (row * RowUints, location.FrustumCullSphere, location.LodLevel);
        var data = RowData.AsSpan(at, RowUints);
        (data[0], data[1], data[2]) = (SingleToUInt32Bits(sphere.x), SingleToUInt32Bits(sphere.y), SingleToUInt32Bits(sphere.z));
        data[3] = (uint)(location.IndicesEnd - location.IndicesStart);
        (data[4], data[5], data[6]) = (SingleToUInt32Bits(sphere.radius / Sqrt3), SingleToUInt32Bits(sphere.radiusY / Sqrt3), SingleToUInt32Bits(sphere.radiusZ / Sqrt3));
        data[7] = (uint)location.IndicesStart;
        var code = lod switch { > 3 => AboveLod, < 0 => BelowLod, _ => lod }; // the far shadow pass draws any level >= 1
        data[8] = (location.Hide ? 1u : 0u) | ((uint)code << 1);
        (data[9], data[10], data[11]) = ((uint)chunk, 0, 0);
        data[8] |= Directed(data, location, region) ? SortedBit : 0;
        RowLocations[row] = location;
        RowOf[location] = row;
        return Assert(data[3] < 1u << 31);
    }

    // FaceSorting slabs into the row: faces per slab (two per uint, direction d slab j at d * Slabs + j), and each
    // slab's min/max plane relative to the row centre in quarter blocks as signed bytes, rounded outward, two slabs per
    // uint. false (range drawn whole) when unsorted or a plane overflows a byte.
    private static bool Directed(Span<uint> data, ModelDataPoolLocation location, Region region)
    {
        const int slabs = FaceSorting.Directions * FaceSorting.Slabs;
        var into = data[12..];
        into.Clear(); // data[10..11] Row zeroed already
        var faces = (location.IndicesEnd - location.IndicesStart) / 6;
        if (region.Vao == 0 || !Assert(into.Length >= slabs) || region.Placed is not { } origin ||
            FaceSorting.Of(region.Vao, location.VerticesStart) is not { } sorted || sorted.Faces != faces) return false;
        var counts = sorted.SlabCounts;
        if (!Assert(counts.Length == slabs) || counts.Max() > ushort.MaxValue) return false;
        var sphere = location.FrustumCullSphere;
        (double X, double Y, double Z) centre = (sphere.x, sphere.y, sphere.z);
        for (var i = 0; i < slabs; i++)
        {
            into[i / 2] |= (uint)counts[i] << (16 * (i % 2));
            if (counts[i] == 0) continue;
            var (o, c) = (i / FaceSorting.Slabs / 2) switch
            {
                0 => (origin.X, centre.X), 1 => (origin.Y, centre.Y), _ => (origin.Z, centre.Z)
            };
            var low = Math.Floor((o + (double)sorted.Low[i] - c) * SlabSteps);
            var high = Math.Ceiling((o + (double)sorted.High[i] - c) * SlabSteps);
            if (!(low >= sbyte.MinValue && high <= sbyte.MaxValue)) return false;
            var (l, h) = ((byte)(sbyte)low, (byte)(sbyte)high);
            into[slabs / 2 + i / 2] |= (l | ((uint)h << 8)) << (16 * (i % 2));
        }

        return Assert(faces >= sorted.Counts.Sum());
    }

    private static void Unrow(Region region)
    {
        if (!NotNull(region) || !Assert(region.Base + region.Count <= MaxRows)) return;
        for (var row = region.Base; row < Math.Min(region.Base + region.Count, MaxRows); row++) Forget(row);
        (region.Count, region.Triangles, region.Owner) = (0, 0, null);
    }

    // The region's last row takes its place
    private static void Unrow(Region region, int row)
    {
        var last = region.Base + region.Count - 1;
        if (!Assert(row >= region.Base && row <= last)) return;
        region.Triangles -= (int)(RowData[row * RowUints + 3] / 3);
        Forget(row);
        if (row != last)
        {
            RowData.AsSpan(last * RowUints, RowUints).CopyTo(RowData.AsSpan(row * RowUints, RowUints));
            if (RowLocations[last] is { } moved) RowOf[moved] = row;
            (RowLocations[row], RowLocations[last]) = (RowLocations[last], null);
            Changed(row, 1);
        }

        region.Count--;
        _ = Assert(region.Count >= 0);
    }

    private static void Forget(int row)
    {
        if (RowLocations[row] is { } location && RowOf.TryGetValue(location, out var at) && at == row)
            _ = RowOf.Remove(location);
        RowLocations[row] = null;
        Release((int)RowData[row * RowUints + 9]);
        RowData[row * RowUints + 9] = 0;
    }

    // Chunk table entry for CullVisible bits (0: none, never visible); -1 when full
    private static int ChunkSlot(Bools? bools)
    {
        if (bools is null) return 0;
        if (!ChunkOf.TryGetValue(bools, out var slot))
        {
            if (FreeChunks.Count > 0) slot = FreeChunks.Pop();
            else if (_chunksUsed < MaxChunks) slot = _chunksUsed++;
            else return -1;
            (ChunkOf[bools], ChunkBools[slot]) = (slot, bools);
            _ = Assert(_chunksUsed <= MaxChunks);
            ChunkBits[slot] = (uint)Bits(bools);
            (_chunksFrom, _chunksBefore) = (Math.Min(_chunksFrom, slot), Math.Max(_chunksBefore, slot + 1));
        }

        ChunkRefs[slot]++;
        return Assert(slot is > 0 and < MaxChunks) ? slot : -1;
    }

    private static void Release(int chunk)
    {
        if (chunk <= 0 || !Index(chunk, MaxChunks) || --ChunkRefs[chunk] > 0) return;
        if (ChunkBools[chunk] is { } bools) _ = ChunkOf.Remove(bools);
        (ChunkBools[chunk], ChunkRefs[chunk]) = (null, 0);
        FreeChunks.Push(chunk);
    }

    private static void Changed(int start, int count)
    {
        if (count <= 0 || !Assert(start >= 0 && start + count <= MaxRows)) return;
        if (Dirty.Count > 0 && Dirty[^1] is var (lastFrom, lastCount) && start <= lastFrom + lastCount &&
            start + count >= lastFrom)
        {
            var first = Math.Min(lastFrom, start); // touching the last run: it grows
            Dirty[^1] = (first, Math.Max(lastFrom + lastCount, start + count) - first);
            return;
        }

        if (Dirty.Count < MaxRuns)
        {
            Dirty.Add((start, count));
            return;
        }

        var end = start + count;
        foreach (var (from, n) in Dirty.Bounded(MaxRuns)) end = Math.Max(end, from + n);
        Dirty.Clear();
        Dirty.Add((0, end)); // too many runs: one over all
    }

    // Postfixes on the engine methods that write Hide
    private static void ChunkShown(ClientChunk __instance)
    {
        if (!NotNull(__instance) || RowOf.Count == 0) return;
        Rehide(Center(__instance));
        Rehide(Edge(__instance));
    }

    // First argument, named differently per seam: Harmony passes it by position
    private static void Located(ModelDataPoolLocation[] __0)
    {
        if (Assert(_mainThread != 0)) Rehide(__0); // InstallRows ran
    }

    internal static void Rehide(ModelDataPoolLocation[]? locations)
    {
        if (locations is null || RowOf.Count == 0) return;
        if (Environment.CurrentManagedThreadId != _mainThread)
        {
            _rescanHide = true; // every location is read again at the next frame's start
            return;
        }

        foreach (var location in locations.Bounded(MaxChunkLocations))
            if (location is not null && RowOf.TryGetValue(location, out var row))
                Hide(row, location.Hide);
    }

    private static void Hide(int row, bool hide)
    {
        if (!Index(row, MaxRows) || !Assert(row < _rowsUsed)) return;
        var at = row * RowUints + 8;
        var flags = (RowData[at] & ~1u) | (hide ? 1u : 0u);
        if (flags == RowData[at]) return;
        RowData[at] = flags;
        Changed(row, 1);
    }

    private static void Rescan()
    {
        _rescanHide = false;
        _ = Assert(_rowsUsed <= MaxRows);
        for (var row = 0; row < Math.Min(_rowsUsed, MaxRows); row++)
            if (RowLocations[row] is { } location)
                Hide(row, location.Hide);
        _ = Assert(!_rescanHide);
    }

    // First FrustumCull prefix: in a rows call a pool with locations always draws, triangles counted as allocated
    private static bool Bypassed(MeshDataPool __instance)
    {
        if (!_rowsCall || !NotNull(__instance) || Dimension(__instance) != 0) return true;
        var has = Locations(__instance) is { Count: > 0 };
        __instance.indicesGroupsCount = has ? 1 : 0;
        if (has && __instance.indicesSizes is { Length: > 0 } sizes) sizes[0] = 0; // the engine would draw nothing
        var triangles = Regions.TryGetValue(__instance, out var region) ? region.Triangles : 0;
        (__instance.RenderedTriangles, __instance.AllocatedTris) = (triangles, triangles);
        return !Assert(triangles >= 0);
    }

    private static void UploadRows((int First, int Count) draws, FrustumCulling culler, Vec3d camera,
        (int Mode, bool BackFaces) how)
    {
        if (!Assert(draws.First + draws.Count <= 2 * MaxDraws) || !NotNull(culler)) return;
        Dirty.Sort();
        var (start, end) = (-1, -1);
        foreach (var (from, count) in Dirty.Bounded(MaxRuns))
        {
            if (from <= end)
            {
                end = Math.Max(end, from + count);
                continue;
            }

            UploadRun(start, end);
            (start, end) = (from, from + count);
        }

        UploadRun(start, end);
        Dirty.Clear();
        Gpu.UploadAt<uint>(_poolBuffer, draws.First * 8, PoolRows.AsSpan(2 * draws.First, 2 * draws.Count));
        UploadChunks();
        Frustum(culler, camera, how);
    }

    private static void UploadRun(int start, int end)
    {
        if (start < 0 || !Assert(end <= MaxRows) || end <= start || !Assert(end <= _rowsUsed)) return;
        Gpu.UploadAt<uint>(_rowBuffer, start * RowBytes, RowData.AsSpan(start * RowUints, (end - start) * RowUints));
    }

    // All chunk bits reread on a visible-buffer swap, else only new entries (culler thread writes the other buffer)
    private static void UploadChunks()
    {
        var index = ModelDataPoolLocation.VisibleBufIndex;
        _ = Assert(_chunksUsed <= MaxChunks);
        var (from, before) = (_chunksFrom, _chunksBefore);
        if (_chunkIndex != index || _chunkFrame < 0)
        {
            (_chunkFrame, _chunkIndex, from, before) = (_frame, index, 1, _chunksUsed);
            for (var c = 1; c < Math.Min(_chunksUsed, MaxChunks); c++)
                ChunkBits[c] = ChunkBools[c] is { } bools ? (uint)Bits(bools) : 0;
        }

        (_chunksFrom, _chunksBefore) = (int.MaxValue, 0);
        if (from >= before || !Assert(before <= MaxChunks)) return;
        Gpu.UploadAt<uint>(_chunkBuffer, from * 4, ChunkBits.AsSpan(from, before - from));
    }

    // rows.comp Frustum block as IsVisible reads it; mode 0 CullNormal, 1/2 near/far shadow, 3 the far cache's pass
    private static void Frustum(FrustumCulling culler, Vec3d camera, (int Mode, bool BackFaces) how)
    {
        var mode = how.Mode;
        var (planes, player) = (Planes(culler), PlayerPos(culler));
        if (!NotNull(planes) || !NotNull(player) || !Assert(planes.Length >= 6) || !Assert(mode is >= 0 and <= 3)) return;
        Span<double> block = stackalloc double[FrustumDoubles];
        block.Clear();
        for (var p = 0; p < 6; p++)
            (block[4 * p], block[4 * p + 1], block[4 * p + 2], block[4 * p + 3]) =
                (planes[p].normalX, planes[p].normalY, planes[p].normalZ, planes[p].D);
        (block[24], block[25], block[26]) = (camera.X, camera.Y, camera.Z);
        (block[28], block[29]) = (player.X, player.Z);
        (block[32], block[33], block[34]) = ((double)(culler.lod0BiasSq + 1024f), culler.ViewDistanceSq, culler.lod2BiasSq);
        block[35] = culler.lod0BiasSq > 0f ? 1 : 0;
        (block[36], block[37]) = (culler.shadowRangeX, culler.shadowRangeZ);
        var casting = mode is 1 or 2 && _casting;
        if (casting) ShadowView.Moved(block[Casters..]);
        (block[64], block[65], block[66]) = (camera.X + _view.X, camera.Y + _view.Y, camera.Z + _view.Z);
        FarSplit(block, block[..20], mode);
        var ints = MemoryMarshal.Cast<double, int>(block);
        (ints[80], ints[81], ints[82], ints[83]) =
            (ModelDataPoolLocation.VisibleBufIndex, mode, how.BackFaces ? 2 : 0, casting ? 1 : 0); // 2: each slab tested
        Gpu.Upload<double>(_frustumBuffer, block);
        Gpu.Uniforms(FrustumBinding, _frustumBuffer);
    }

    private static void BindRows(int counters)
    {
        Bind(counters);
        Gpu.Storage(RowsBinding, _rowBuffer);
        Gpu.Storage(ChunksBinding, _chunkBuffer);
        Gpu.Storage(PoolsBinding, _poolBuffer);
        Gpu.Storage(KeysBinding, _keys);
        _ = Assert(_rowBuffer != 0) && Assert(_poolBuffer != 0);
    }

    // After a camera rows pass (base 0, params bound): copies each draw's commands near to far into the sorted buffer,
    // false without the program. Shadows unsorted: light-first was no faster (TerrainBenchGpuTests).
    private static bool Sorted((int First, int Count) draws)
    {
        if (_sortProgram == 0 || !Assert(draws.Count > 0) || !Assert(_sorted != 0)) return false;
        Gpu.Storage(SortedBinding, _sorted);
        Gpu.Dispatch(_sortProgram, 1, draws.Count);
        Gpu.Barrier(GpuBarrier.Indirect | GpuBarrier.Storage);
        SortedDraws += draws.Count;
        return true;
    }

    public static long SortedDraws { get; private set; }

    // Tests and benches: sort regardless of GL state
    internal static bool SortAlways { get; set; }

    // Depth LESS/LEQUAL+write, no blending (GL state as Vulkan taps it): nearest wins in any order; untapped: unknown
    internal static bool Orderless()
    {
        if (SortAlways) return true;
        if (!Vulkan.GlTap.Tapped) return false;
        var state = Vulkan.GlTap.State;
        if (!state.DepthTest || state.DepthCompare is not (1 or 3) || !state.DepthWrite) return false;
        var blended = false;
        for (var i = 0; i < Vulkan.GlTap.MaxBuffers; i++) blended |= state.Blend.Blending(i);
        return !blended;
    }

    // Each draw's commands region is its pool's row count
    private static void IssuingRows(FrustumCulling culler, List<MeshDataPool> pools, Vec3d camera)
    {
        var (first, cursor) = (_frameRanges, _frameRanges);
        if (!NotNull(culler) || !NotNull(pools)) return;
        var draws = Lay(pools, camera, ref cursor, (_frameDraws, MaxDraws), _backFaces ? Runs : 1);
        _rowsCuller = culler;
        if (draws > _frameDraws) Cull((first, cursor - first), (_frameDraws, draws - _frameDraws), (culler, camera));
        if (_lastSurface && _late) FinishSurfaces();
    }

    // Held pools' draws from draws.First (before End), commands from cursor (factor a row); returns the next draw
    private static int Lay(List<MeshDataPool> pools, Vec3d camera, ref int cursor, (int First, int End) draws, int factor)
    {
        var (next, held) = (draws.First, 0);
        for (var i = 0; i < Math.Min(pools.Count, MaxPools) && Assert(factor > 0); i++)
        {
            var pool = pools[i];
            if (Dimension(pool) != 0 || !Taken(ModelRef(pool), ref held) || !Index(next, draws.End) ||
                !Regions.TryGetValue(pool, out var region)) continue;
            var count = Math.Min(region.Count, (MaxRanges - cursor) / factor);
            (PoolRows[2 * next], PoolRows[2 * next + 1]) = ((uint)region.Base, (uint)count);
            Drawn(next++, pool, camera, cursor, factor * count);
            cursor += factor * count;
        }

        Held.Clear();
        return next;
    }

    // One non-surface rows call, engine-free (GPU tests): pools need locations, dimension 0, own VAO; draws nothing
    internal static bool RunRows(List<MeshDataPool> pools, FrustumCulling culler, Vec3d camera, float[] viewProjection,
        bool backFaces = false)
    {
        if (!NotNull(pools) || !NotNull(culler) || !Assert(viewProjection.Length == 16) || !Ready()) return false;
        BeginFrame(true);
        viewProjection.CopyTo(ViewProjection, 0);
        (_captured, _camera, _surface, _lastSurface, _builtThisFrame) = (true, camera, false, false, true);
        if (!Rowed(culler, pools, _rowsFrame))
        {
            EndFrame();
            return false;
        }

        Held.Clear();
        foreach (var pool in pools.Bounded(MaxPools))
            if (ModelRef(pool) is { } model)
                Held.Add(model);
        _backFaces = backFaces;
        IssuingRows(culler, pools, camera);
        _backFaces = false;
        EndFrame();
        return Assert(Held.Count == 0);
    }

    // Back faces culled and no face moved by the VS (GL state as Vulkan taps it, else RenderOpaque's; no world warp)
    private static bool BackFacesCulled()
    {
        if (!FaceSorting.Enabled || !ShadowView.Unwarped(_api) || !Assert(Runs >= 4)) return false;
        if (!Vulkan.GlTap.Tapped) return true;
        var state = Vulkan.GlTap.State;
        return state.Culls == 2 && state.CounterClockwise && Assert(state.Depth < 128);
    }

    // Surfaces' second pass over their rows, under the last call's frustum block
    private static void LateRows(int counters, bool again)
    {
        if (_rowsCuller is not { } culler || _camera is not { } camera || !Assert(_surfaceDraws > 0) ||
            !Assert(_surfaceRows)) return;
        Frustum(culler, camera, (0, _surfaceBackFaces));
        BindRows(counters);
        RowsPass(ViewProjection, (0, 0, 0), (1, again ? Again : KeepAgain, StatUints + MaxDraws, MaxRanges),
            (0, _surfaceDraws), _surfaceMaxRows);
    }

    // One work-group row per pool draw, x as wide as the largest pool needs
    private static void RowsPass(float[] matrix, (double X, double Y, double Z) shift, (int, int, int, int) flags,
        (int First, int Count) draws, int rows)
    {
        if (!Assert(matrix.Length == 16) || !Assert(draws.Count > 0) || rows <= 0) return;
        Span<float> p = stackalloc float[ShaderParams.TestFloats];
        ShaderParams.Test(p, matrix, shift, Depth, (0, 0), flags, (draws.First, draws.First + draws.Count));
        Gpu.Upload<float>(_params, p);
        Gpu.Uniforms(ParamsBinding, _params);
        Gpu.Texture(DepthPyramid.Unit, Depth.Texture);
        Gpu.Dispatch(_rowsProgram, (rows + Group - 1) / Group, draws.Count);
        Gpu.Barrier(GpuBarrier.Indirect | GpuBarrier.Storage | GpuBarrier.Copy);
    }
}
