using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// MeshDataPool's edits without passes over every location. InsertAt rebases indices and xyz SIMD-wide, skipping the
// index rebase for SSBO meshes (UpdateChunkMesh never reads Indices). RemoveLocation becomes bisection plus RemoveAt.
// CalcFragmentation's two sums stay exact ints updated from neighbours (int wrap is order-free, so UsedVertices and
// CurrentFragmentation match the engine bit for bit). TrySqueezeInbetween returns null where gap bounds prove a miss.
// 1000 removals over 40 pools 28 -> 0.8 ms; streaming adds (30/frame) median 1.46 -> 0.14 ms. Invariant: lists change
// only via Insert, Add, RemoveAt, so _version moves on each change (FrustumSweep relies on it); foreign changes
// Recount.
internal static class MeshPool
{
    private const int MaxLocations = 65536, MaxSteps = 32, MaxVertices = 1 << 21, MaxIndices = 1 << 22, MaxLanes = 32;

    // Not by pool id: the decal pool shares id 0
    private static readonly ConditionalWeakTable<MeshDataPool, State> States = [];

    public static bool Enabled { get; set; } = true;
    public static long Models { get; private set; } // totals while Counting.Hud, main thread
    public static long Vertices { get; private set; }
    public static long Skipped { get; private set; } // indices left alone on the SSBO path
    public static long Ticks { get; private set; }
    public static long Removed { get; private set; } // RemoveLocation calls, engine path included
    public static long Squeezes { get; private set; }
    public static long Skips { get; private set; } // squeezes answered by the bounds without a walk
    public static long Recounts { get; private set; }

    // Fields shared with FrustumSweep; modelRef's users check it themselves
    internal static bool Seams()
    {
        var pool = typeof(MeshDataPool);
        return Assert(AccessTools.Field(pool, "poolLocations") != null) &&
               Assert(AccessTools.Field(pool, "poolId") != null) &&
               Assert(AccessTools.Field(pool, "poolOrigin") != null) &&
               Assert(AccessTools.Field(typeof(List<ModelDataPoolLocation>), "_version") != null);
    }

    public static void Install(Harmony harmony)
    {
        var insert = AccessTools.Method(typeof(MeshDataPool), "InsertAt");
        var remove = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation));
        var squeeze = AccessTools.Method(typeof(MeshDataPool), "TrySqueezeInbetween");
        HarmonyMethod onInsert = new(InsertAt), onRemove = new(RemoveLocation), onSqueeze = new(TrySqueezeInbetween);
        if (!NotNull(harmony) || !NotNull(insert) || !NotNull(remove) || !NotNull(squeeze) || !Seams() ||
            !Assert(AccessTools.Field(typeof(MeshDataPool), "modelRef") != null) ||
            !Assert(Vector<float>.Count is >= 4 and <= MaxLanes) || !Assert(Il.Binds(insert, onInsert.method)) ||
            !Assert(Il.Binds(remove, onRemove.method)) || !Assert(Il.Binds(squeeze, onSqueeze.method))) return;
        _ = NotNull(harmony.Patch(insert, onInsert));
        _ = NotNull(harmony.Patch(remove, onRemove));
        _ = NotNull(harmony.Patch(squeeze, onSqueeze));
    }

    private static bool RemoveLocation(MeshDataPool __instance, ModelDataPoolLocation location)
    {
        if (Counting.Hud) Removed++;
        return !Enabled || !NotNull(__instance) || !Remove(__instance, location);
    }

    private static bool TrySqueezeInbetween(MeshDataPool __instance, MeshData modeldata,
        ref ModelDataPoolLocation? __result)
    {
        if (Counting.Hud) Squeezes++;
        if (!Enabled || !NotNull(__instance) || !Misses(__instance, modeldata)) return true;
        __result = null;
        return false;
    }

    // In MeshDataPool.InsertAt's order
    private static bool InsertAt(MeshDataPool __instance, ICoreClientAPI capi, MeshData modeldata, Vec3i modelOrigin,
        Sphere frustumCullSphere, int indexPosition, int vertexPosition, int listPosition,
        ref ModelDataPoolLocation __result)
    {
        if (!Enabled || !NotNull(capi) || !NotNull(modeldata) || !NotNull(__instance)) return true;
        var (locations, model) = (Locations(__instance), ModelRef(__instance));
        if (!NotNull(locations) || !NotNull(model) || !NotNull(modeldata.xyz) ||
            !NotNull(modeldata.Indices)) return true;
        if (!Assert(indexPosition >= 0) || !Assert(vertexPosition >= 0) ||
            !Assert(listPosition == -1 || Index(listPosition, locations.Count + 1)) || !Fits(modeldata)) return true;
        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        Rebase(capi, __instance, modeldata, modelOrigin, vertexPosition);
        Offsets(modeldata, indexPosition, vertexPosition);
        capi.Render.UpdateChunkMesh(model, modeldata);
        var location = new ModelDataPoolLocation
        {
            IndicesStart = indexPosition, IndicesEnd = indexPosition + modeldata.IndicesCount,
            VerticesStart = vertexPosition, VerticesEnd = vertexPosition + modeldata.VerticesCount,
            PoolId = PoolId(__instance), FrustumCullSphere = frustumCullSphere
        };
        var state = Current(__instance, locations); // as of the list one mutation before this insert
        if (listPosition == -1) locations.Add(location);
        else locations.Insert(listPosition, location);
        if (!Inserted(__instance, locations, state, listPosition == -1 ? locations.Count - 1 : listPosition))
            __instance.CalcFragmentation();
        __result = location;
        if (start != 0)
            (Models, Vertices, Ticks) = (Models + 1, Vertices + modeldata.VerticesCount,
                Ticks + Stopwatch.GetTimestamp() - start);
        return false;
    }

    // Nothing may fail once the rebase has begun
    private static bool Fits(MeshData modeldata) => Assert(modeldata.VerticesCount is >= 0 and <= MaxVertices) &&
        Assert(modeldata.IndicesCount is >= 0 and <= MaxIndices) &&
        Assert(3 * modeldata.VerticesCount <= modeldata.xyz.Length) &&
        Assert(modeldata.IndicesCount <= modeldata.Indices.Length);

    // Engine rebases indices only for a mesh not starting at zero, vertices only for a pool with an origin
    private static void Rebase(ICoreClientAPI capi, MeshDataPool pool, MeshData modeldata, Vec3i modelOrigin,
        int vertexPosition)
    {
        var origin = PoolOrigin(pool);
        if (vertexPosition > 0)
        {
            if (KeepsIndices(capi, modeldata)) Shift(modeldata.Indices, modeldata.IndicesCount, vertexPosition);
            else if (Counting.Hud) Skipped += modeldata.IndicesCount;
        }

        if (origin is null || !NotNull(modelOrigin) || !Assert(modeldata.VerticesCount >= 0)) return;
        Shift(modeldata.xyz, modeldata.VerticesCount, modelOrigin.X - origin.X, modelOrigin.Y - origin.Y,
            modelOrigin.Z - origin.Z);
    }

    // UpdateChunkMesh sends all but wide custom ints via SSBO, which never uploads index values
    private static bool KeepsIndices(ICoreClientAPI capi, MeshData modeldata)
    {
        if (!NotNull(capi.Render) || !Assert(modeldata.IndicesCount >= 0)) return true;
        return !capi.Render.UseSSBOs || modeldata.CustomInts is { InterleaveStride: >= 8 };
    }

    private static void Offsets(MeshData modeldata, int indexPosition, int vertexPosition)
    {
        if (!Assert(vertexPosition >= 0) || !Assert(indexPosition >= 0)) return;
        (modeldata.XyzOffset, modeldata.NormalsOffset, modeldata.RgbaOffset, modeldata.Rgba2Offset) =
            (vertexPosition * 12, vertexPosition * 4, vertexPosition * 4, vertexPosition * 4);
        (modeldata.UvOffset, modeldata.FlagsOffset, modeldata.IndicesOffset) =
            (vertexPosition * 8, vertexPosition * 4, indexPosition * 4);
        if (modeldata.CustomFloats is { } floats) floats.BaseOffset = vertexPosition * floats.InterleaveStride;
        if (modeldata.CustomShorts is { } shorts) shorts.BaseOffset = vertexPosition * shorts.InterleaveStride;
        if (modeldata.CustomBytes is { } bytes) bytes.BaseOffset = vertexPosition * bytes.InterleaveStride;
        if (modeldata.CustomInts is { } ints) ints.BaseOffset = vertexPosition * ints.InterleaveStride;
    }

    internal static void Shift(int[] values, int count, int delta)
    {
        var w = Vector<int>.Count;
        if (!NotNull(values) || !Assert(count >= 0 && count <= values.Length) ||
            !Assert(w is >= 4 and <= MaxLanes)) return;
        var blocks = count / w;
        var d = new Vector<int>(delta);
        for (var b = 0; b < Math.Min(blocks, MaxIndices); b++)
            (new Vector<int>(values, b * w) + d).CopyTo(values, b * w);
        for (var i = blocks * w; i < Math.Min(count, MaxIndices); i++) values[i] += delta;
    }

    // Three vectors repeat dx, dy, dz, so a block of 3w floats ends on a vertex boundary
    internal static void Shift(float[] xyz, int count, int dx, int dy, int dz)
    {
        var w = Vector<float>.Count;
        var floats = 3 * count;
        if (!NotNull(xyz) || !Assert(count >= 0 && floats <= xyz.Length) || !Assert(w is >= 4 and <= MaxLanes)) return;
        Span<float> pattern = stackalloc float[3 * MaxLanes];
        for (var k = 0; k < Math.Min(3 * w, 3 * MaxLanes); k += 3)
            (pattern[k], pattern[k + 1], pattern[k + 2]) = (dx, dy, dz);
        Vector<float> p0 = new(pattern), p1 = new(pattern[w..]), p2 = new(pattern[(2 * w)..]);
        var blocks = floats / (3 * w);
        for (var b = 0; b < Math.Min(blocks, MaxVertices); b++)
        {
            var i = b * 3 * w;
            (new Vector<float>(xyz, i) + p0).CopyTo(xyz, i);
            (new Vector<float>(xyz, i + w) + p1).CopyTo(xyz, i + w);
            (new Vector<float>(xyz, i + 2 * w) + p2).CopyTo(xyz, i + 2 * w);
        }

        for (var i = blocks * 3 * w; i < Math.Min(floats, 3 * MaxVertices); i += 3)
            (xyz[i], xyz[i + 1], xyz[i + 2]) = (xyz[i] + dx, xyz[i + 1] + dy, xyz[i + 2] + dz);
    }

    // false: engine call (throws for a foreign location). Only plain ModelDataPoolLocation lists (Equals is reference).
    private static bool Remove(MeshDataPool pool, ModelDataPoolLocation? location)
    {
        if (!NotNull(pool) || location is null || location.GetType() != typeof(ModelDataPoolLocation) ||
            location.PoolId != PoolId(pool)) return false;
        var list = Locations(pool);
        var state = Current(pool, list);
        if (state is null) return false;
        var span = CollectionsMarshal.AsSpan(list);
        var i = Find(span, location, state.Sorted);
        if (i < 0 || !Assert(ReferenceEquals(span[i], location)) || !Assert(state.Count == span.Length)) return false;
        var (prevI, prevV) = i > 0 ? (span[i - 1].IndicesEnd, span[i - 1].VerticesEnd) : (0, 0);
        state.Gaps -= Math.Max(0, location.VerticesStart - prevV);
        if (i + 1 < span.Length)
        {
            var next = span[i + 1];
            state.Gaps += Math.Max(0, next.VerticesStart - prevV) -
                          Math.Max(0, next.VerticesStart - location.VerticesEnd);
            (state.MaxIndexGap, state.MaxVertexGap) = (Math.Max(state.MaxIndexGap, next.IndicesStart - prevI),
                Math.Max(state.MaxVertexGap, next.VerticesStart - prevV)); // the two gaps around it merge
        }

        state.Used -= location.VerticesEnd - location.VerticesStart;
        list.RemoveAt(i);
        if (list.Count == 0)
            (pool.indicesPosition, pool.verticesPosition) = (0, 0); // the engine's position rule, verbatim
        else if (location.IndicesEnd == pool.indicesPosition && location.VerticesEnd == pool.verticesPosition)
            (pool.indicesPosition, pool.verticesPosition) = (list[^1].IndicesEnd, list[^1].VerticesEnd);
        Publish(pool, list, state);
        return true;
    }

    // InsertAt's closing CalcFragmentation for the location at index. Uses verticesPosition as is, still old for an
    // append (engine calculates before TryAppend), so the first mesh in an empty pool leaves UsedVertices 0 until the
    // next change, as in the engine. False hands back to the engine.
    private static bool Inserted(MeshDataPool pool, List<ModelDataPoolLocation> list, State? state, int index)
    {
        if (state is null) return false;
        if (!Assert(state.Plain) || !Assert(ReferenceEquals(state.Owner, list) && list.Count == state.Count + 1) ||
            !Index(index, list.Count) || list[index]?.GetType() != typeof(ModelDataPoolLocation))
        {
            state.Owner = null; // whatever happened, the next use counts again
            return false;
        }

        var span = CollectionsMarshal.AsSpan(list);
        var location = span[index];
        var (prevI, prevV) = index > 0 ? (span[index - 1].IndicesEnd, span[index - 1].VerticesEnd) : (0, 0);
        if (index + 1 < span.Length)
        {
            var next = span[index + 1];
            state.Gaps += Math.Max(0, next.VerticesStart - location.VerticesEnd) -
                          Math.Max(0, next.VerticesStart - prevV);
            (state.MaxIndexGap, state.MaxVertexGap) = (
                Math.Max(state.MaxIndexGap, next.IndicesStart - location.IndicesEnd),
                Math.Max(state.MaxVertexGap, next.VerticesStart - location.VerticesEnd));
        }

        state.Gaps += Math.Max(0, location.VerticesStart - prevV);
        (state.MaxIndexGap, state.MaxVertexGap) = (Math.Max(state.MaxIndexGap, location.IndicesStart - prevI),
            Math.Max(state.MaxVertexGap, location.VerticesStart - prevV));
        state.Used += location.VerticesEnd - location.VerticesStart;
        state.Sorted &= (index == 0 || span[index - 1].IndicesStart <= location.IndicesStart) &&
                        (index + 1 == span.Length || location.IndicesStart <= span[index + 1].IndicesStart);
        Publish(pool, list, state);
        return true;
    }

    // True only where TrySqueezeInbetween returns null (no gap wider on both axes). Bounds decide when the mesh is at
    // least the widest gap on an axis; else the engine's walk runs, and a miss sets exact bounds. Squeezes only shrink
    // gaps, so bounds raised between full passes stay valid.
    internal static bool Misses(MeshDataPool pool, MeshData? modeldata)
    {
        if (!NotNull(pool) || modeldata is null) return false;
        var list = Locations(pool);
        var state = Current(pool, list);
        if (state is null) return false;
        var (indices, vertices) = (modeldata.IndicesCount, modeldata.VerticesCount);
        if (indices >= state.MaxIndexGap || vertices >= state.MaxVertexGap)
        {
            if (Counting.Hud) Skips++;
            return true;
        }

        var span = CollectionsMarshal.AsSpan(list);
        if (!Assert(state.Count == span.Length)) return false;
        int prevI = 0, prevV = 0, widestI = int.MinValue, widestV = int.MinValue;
        for (var i = 0; i < Math.Min(span.Length, MaxLocations); i++)
        {
            var location = span[i];
            int gapI = location.IndicesStart - prevI, gapV = location.VerticesStart - prevV;
            if (gapI > indices && gapV > vertices) return false;
            (widestI, widestV) = (Math.Max(widestI, gapI), Math.Max(widestV, gapV));
            (prevI, prevV) = (location.IndicesEnd, location.VerticesEnd);
        }

        (state.MaxIndexGap, state.MaxVertexGap) = (widestI, widestV);
        return Assert(span.Length <= MaxLocations);
    }

    // Recounted when the list changed behind it; null when the pool is the engine's alone
    private static State? Current(MeshDataPool pool, List<ModelDataPoolLocation>? list)
    {
        if (!NotNull(list) || list.Count > MaxLocations) return null;
        var state = States.GetValue(pool, static _ => new State());
        if (!ReferenceEquals(state.Owner, list) || state.ListVersion != Version(list) || state.Count != list.Count)
            Recount(state, list);
        return Assert(state.Count == list.Count) && state.Plain ? state : null;
    }

    // CalcFragmentation plus widest gaps. Null or derived entries leave the pool to the engine (throws; custom Equals).
    private static void Recount(State state, List<ModelDataPoolLocation> list)
    {
        var span = CollectionsMarshal.AsSpan(list);
        if (!Assert(span.Length <= MaxLocations)) return;
        (state.Gaps, state.Used, state.MaxIndexGap, state.MaxVertexGap, state.Plain, state.Sorted) =
            (0, 0, int.MinValue, int.MinValue, true, true);
        int prevI = 0, prevV = 0;
        for (var i = 0; i < Math.Min(span.Length, MaxLocations); i++)
        {
            var location = span[i];
            if (location?.GetType() != typeof(ModelDataPoolLocation))
            {
                state.Plain = false;
                break;
            }

            state.Sorted &= i == 0 || span[i - 1].IndicesStart <= location.IndicesStart;
            state.Gaps += Math.Max(0, location.VerticesStart - prevV);
            state.Used += location.VerticesEnd - location.VerticesStart;
            (state.MaxIndexGap, state.MaxVertexGap) = (Math.Max(state.MaxIndexGap, location.IndicesStart - prevI),
                Math.Max(state.MaxVertexGap, location.VerticesStart - prevV));
            (prevI, prevV) = (location.IndicesEnd, location.VerticesEnd);
        }

        (state.Owner, state.ListVersion, state.Count) = (list, Version(list), list.Count);
        if (Counting.Hud) Recounts++;
    }

    // Ordered by IndicesStart; zero-size meshes share a start, so search the first equal start and walk the run,
    // finding the copy List.Remove takes. Unsorted lists walk everything.
    private static int Find(ReadOnlySpan<ModelDataPoolLocation> span, ModelDataPoolLocation location, bool sorted)
    {
        if (!Assert(span.Length <= MaxLocations) || !NotNull(location)) return -1;
        var (lo, hi, key) = (sorted ? 0 : span.Length, span.Length - 1, location.IndicesStart);
        for (var step = 0; step < MaxSteps && lo <= hi; step++)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (span[mid].IndicesStart < key) lo = mid + 1;
            else hi = mid - 1;
        }

        for (var i = lo; i < Math.Min(span.Length, MaxLocations) && span[i].IndicesStart == key; i++)
            if (ReferenceEquals(span[i], location)) return i;
        for (var i = 0; i < Math.Min(span.Length, MaxLocations); i++)
            if (ReferenceEquals(span[i], location)) return i;
        return -1;
    }

    private static void Publish(MeshDataPool pool, List<ModelDataPoolLocation> list, State state)
    {
        (pool.UsedVertices, pool.CurrentFragmentation) =
            pool.verticesPosition == 0 ? (0, 0f) : (state.Used, (float)state.Gaps / pool.verticesPosition);
        (state.ListVersion, state.Count) = (Version(list), list.Count);
        _ = Assert(ReferenceEquals(state.Owner, list)) && Assert(state.Count <= MaxLocations);
    }

    private sealed class State
    {
        public int ListVersion, Count, Gaps, Used; // the engine sums in int, so these wrap where its pass would

        // Upper bounds, exact after a full pass or a missed walk
        public int MaxIndexGap = int.MinValue, MaxVertexGap = int.MinValue;

        public List<ModelDataPoolLocation>? Owner;
        public bool Plain; // nothing but plain locations, none null
        public bool Sorted; // IndicesStart never falls from one location to the next, so Find may bisect
    }
}
