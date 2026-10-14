using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Back-face-culled pass meshes sorted by facing direction so rows.comp can skip a whole direction. A face counts only
// when its corners share a plane on that axis, both triangles wind alike and the VS neither moves a corner (WindMode)
// nor offsets depth (ZOffset). Within a direction, faces go by plane, least first, in Slabs runs of about equal count
// with min/max planes: a +X slab is seen only from above its least plane, -X from below its greatest; each slab box is
// tested against the depth pyramid, so a hill can hide slabs of a part it does not hide whole.
internal static class FaceSorting
{
    public const int Directions = 6, Slabs = 4; // rows.comp's too; a row's verdict holds a bit for each of its slabs below Whole
    private const int MaxFaces = 1 << 20, CulledOpaque = 0, CulledTopsoil = 5, MaxPerFace = 64;
    private const int WindMode = 0xF << 25, ZOffset = 0x7 << 8;

    // Tessellation threads put, upload takes. Not a ConditionalWeakTable: its dependent handles are walked by every GC
    // and recycled meshes live as long as the pool (gen0/gen1 ~30 % longer with GPU culling, 2026-10-06).
    // Retessellation replaces an entry; never-uploaded ones go after StaleMs.
    private static readonly Dictionary<MeshData, (Sorted Sorted, long Since)> ByMesh = new(ReferenceEqualityComparer.Instance);
    private static readonly Lock Gate = new();
    private static readonly List<MeshData> Stale = [];
    private const long StaleMs = 30_000;
    private const int MaxMeshes = 1 << 18;
    private static long _swept;
    private static readonly Dictionary<(int Vao, int Vertex), Sorted> ByLocation = []; // main thread

    [ThreadStatic] private static byte[]? _scratch;
    [ThreadStatic] private static int[]? _groups, _to;
    [ThreadStatic] private static float[]? _planes;
    [ThreadStatic] private static long[]? _keys;
    private const int FaceBits = 20; // a sort key's face: MaxFaces

    // First and Last find InsertAt's move again; chunk-relative until uploaded
    public sealed class Sorted
    {
        public int Faces { get; init; }
        public int[] Counts { get; } = new int[Directions];
        public int[] SlabCounts { get; } = new int[Directions * Slabs]; // direction d's slab j at d * Slabs + j, least planes first
        public float[] Low { get; } = new float[Directions * Slabs]; // a slab's least plane
        public float[] High { get; } = new float[Directions * Slabs]; // and its greatest
        public (float X, float Y, float Z) First { get; set; }
        public (float X, float Y, float Z) Last { get; set; }
    }

    public static bool Enabled { get; set; } = true;

    // Only GPU rows read the order; without them the sort costs 0.25 ms per tessellation pass (measured)
    private static bool Wanted => Enabled && OcclusionCulling.RowsActive;

    public static int Located => ByLocation.Count;

    // Groups leave with their location even without GPU culling, else every location ever pooled stays (up to MaxFaces)
    public static void Install(Harmony harmony)
    {
        Clear();
        var populate = AccessTools.DeclaredMethod(typeof(ChunkTesselator), "populateTesselatedChunkPart");
        var remove = AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation));
        if (!NotNull(harmony) || !NotNull(populate) || !NotNull(remove) ||
            !Assert(AccessTools.Field(typeof(TesselatedChunkPart), "modelDataLod0") != null) ||
            !Assert(AccessTools.Field(typeof(MeshDataPool), "modelRef") != null)) return;
        _ = NotNull(harmony.Patch(populate, postfix: new HarmonyMethod(AccessTools.Method(typeof(FaceSorting),
            nameof(Populated)))));
        _ = NotNull(harmony.Patch(remove, postfix: new HarmonyMethod(AccessTools.Method(typeof(FaceSorting),
            nameof(Removed)))));
    }

    public static void Clear()
    {
        ByLocation.Clear();
        lock (Gate) ByMesh.Clear();
        _ = Assert(ByLocation.Count == 0) && Assert(Located == 0);
    }

    private static void Removed(MeshDataPool __instance, ModelDataPoolLocation? location)
    {
        if (location is null || !NotNull(__instance) || Fields.ModelRef(__instance) is not VAO vao) return;
        Gone(vao.VaoId, location.VerticesStart);
        _ = Assert(ByLocation.Count <= MaxFaces);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pass")]
    private static extern ref EnumChunkRenderPass Pass(TesselatedChunkPart part);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelDataLod0")]
    private static extern ref MeshData? Lod0(TesselatedChunkPart part);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelDataLod1")]
    private static extern ref MeshData? Lod1(TesselatedChunkPart part);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelDataNotLod2Far")]
    private static extern ref MeshData? NotLod2Far(TesselatedChunkPart part);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelDataLod2Far")]
    private static extern ref MeshData? Lod2Far(TesselatedChunkPart part);

    // Tessellation thread
    private static void Populated(TesselatedChunkPart[]? tessChunkParts)
    {
        if (tessChunkParts is null || !Assert(tessChunkParts.Length <= 256) || !Wanted) return;
        foreach (var part in tessChunkParts.Bounded(256))
        {
            if (part is null) continue;
            var culled = (int)Pass(part) is CulledOpaque or CulledTopsoil;
            Sort(Lod0(part), culled);
            Sort(Lod1(part), culled);
            Sort(NotLod2Far(part), culled);
            Sort(Lod2Far(part), culled);
        }
    }

    internal static void Sort(MeshData? mesh, bool culled)
    {
        if (mesh is null || !Assert(mesh.VerticesCount >= 0)) return;
        lock (Gate) _ = ByMesh.Remove(mesh);
        if (!culled || Sorting(mesh) is not { } sorted) return;
        lock (Gate)
            if (ByMesh.Count < MaxMeshes) ByMesh[mesh] = (sorted, Environment.TickCount64);
    }

    // Main thread, periodically: sorted but never uploaded
    private static void Sweep(long now)
    {
        if (now - _swept < StaleMs / 4 || !Assert(Stale.Count == 0)) return;
        _swept = now;
        lock (Gate)
        {
            foreach (var (mesh, entry) in ByMesh.Bounded(MaxMeshes))
                if (now - entry.Since > StaleMs) Stale.Add(mesh);
            foreach (var mesh in Stale.Bounded(MaxMeshes)) _ = ByMesh.Remove(mesh);
        }

        Stale.Clear();
    }

    // null leaves the mesh unchanged
    internal static Sorted? Sorting(MeshData mesh)
    {
        var faces = mesh.VerticesCount / 4;
        if (!NotNull(mesh) || faces == 0 || faces > MaxFaces || mesh.VerticesCount != 4 * faces || !Laid(mesh, faces))
            return null;
        if ((_groups?.Length ?? 0) < faces) (_groups, _planes) = (new int[Math.Max(faces, 4096)], new float[Math.Max(faces, 4096)]);
        var (groups, planes) = (_groups!, _planes!);
        var sorted = new Sorted { Faces = faces };
        for (var f = 0; f < Math.Min(faces, MaxFaces); f++)
        {
            var (direction, plane) = Facing(mesh, f);
            (groups[f], planes[f]) = (direction, plane);
            if (direction < Directions) sorted.Counts[direction]++;
        }

        Permuted(mesh, groups.AsSpan(0, faces), planes.AsSpan(0, faces), sorted);
        var last = 3 * (mesh.VerticesCount - 1);
        sorted.First = (mesh.xyz[0], mesh.xyz[1], mesh.xyz[2]);
        sorted.Last = (mesh.xyz[last], mesh.xyz[last + 1], mesh.xyz[last + 2]);
        return Assert(sorted.Counts.Sum() <= faces) ? sorted : null;
    }

    private static bool Laid(MeshData mesh, int faces)
    {
        if (!Assert(faces > 0) || mesh.VerticesPerFace != 4 || mesh.IndicesPerFace != 6 || mesh.IndicesCount != 6 * faces ||
            mesh.xyz is null || mesh.xyz.Length < 12 * faces || mesh.Flags is null || mesh.Flags.Length < 4 * faces ||
            mesh.Indices is null || mesh.Indices.Length < 6 * faces) return false;
        ReadOnlySpan<int> quad = [0, 1, 2, 0, 2, 3];
        for (var i = 0; i < Math.Min(6 * faces, 6 * MaxFaces); i++)
            if (mesh.Indices[i] != i / 6 * 4 + quad[i % 6])
                return false;
        return (mesh.Uv is null || mesh.Uv.Length >= 8 * faces) && (mesh.Rgba is null || mesh.Rgba.Length >= 16 * faces) &&
               PerFace(mesh.XyzFacesCount, faces) && PerFace(mesh.TextureIndicesCount, faces) &&
               PerFace(mesh.ColorMapIdsCount, faces) && PerFace(mesh.RenderPassCount, faces) &&
               (mesh.NormalsCount == 0 || mesh.NormalsCount == mesh.VerticesCount) &&
               PerVertex(mesh.CustomFloats?.Count, mesh) && PerVertex(mesh.CustomShorts?.Count, mesh) &&
               PerVertex(mesh.CustomInts?.Count, mesh) && PerVertex(mesh.CustomBytes?.Count, mesh);
    }

    private static bool PerFace(int count, int faces) =>
        Assert(count >= 0) && (count == 0 || (count == faces && Assert(faces > 0)));

    private static bool PerVertex(int? count, MeshData mesh) =>
        Assert(mesh.VerticesCount > 0) &&
        (count is null or 0 || (count % mesh.VerticesCount == 0 && count / mesh.VerticesCount <= MaxPerFace));

    // Direction (0 -X, 1 +X, 2 -Y, 3 +Y, 4 -Z, 5 +Z, Directions: none) and plane coordinate
    private static (int Direction, float Plane) Facing(MeshData mesh, int face)
    {
        var (xyz, flags, v) = (mesh.xyz, mesh.Flags, 4 * face);
        if (!Assert(3 * v + 12 <= xyz.Length) || !Assert(v + 4 <= flags.Length)) return (Directions, 0);
        for (var k = 0; k < 4; k++)
            if ((flags[v + k] & (WindMode | ZOffset)) != 0)
                return (Directions, 0);
        for (var axis = 0; axis < 3; axis++)
        {
            var c = xyz[3 * v + axis];
            if (!float.IsFinite(c)) continue;
            var bits = BitConverter.SingleToInt32Bits(c); // exactly equal: one plane
            if (BitConverter.SingleToInt32Bits(xyz[3 * v + 3 + axis]) != bits ||
                BitConverter.SingleToInt32Bits(xyz[3 * v + 6 + axis]) != bits ||
                BitConverter.SingleToInt32Bits(xyz[3 * v + 9 + axis]) != bits) continue;
            var (first, second) = (Winding(xyz, v, (0, 1, 2), axis), Winding(xyz, v, (0, 2, 3), axis));
            if (first > 0 && second > 0) return (2 * axis + 1, c);
            if (first < 0 && second < 0) return (2 * axis, c);
            return (Directions, 0);
        }

        return (Directions, 0);
    }

    // Axis component of (b - a) x (c - a): the triangle's front side
    private static double Winding(float[] xyz, int v, (int A, int B, int C) corners, int axis)
    {
        var (i, j) = ((axis + 1) % 3, (axis + 2) % 3);
        if (!Assert(axis is >= 0 and < 3) || !Assert(3 * v + 12 <= xyz.Length)) return 0;
        var (a, b, c) = (3 * (v + corners.A), 3 * (v + corners.B), 3 * (v + corners.C));
        double ui = xyz[b + i] - (double)xyz[a + i], uj = xyz[b + j] - (double)xyz[a + j];
        double wi = xyz[c + i] - (double)xyz[a + i], wj = xyz[c + j] - (double)xyz[a + j];
        return ui * wj - uj * wi;
    }

    // By group, within a direction by plane (least first), else in order; then each direction's slabs
    private static void Permuted(MeshData mesh, ReadOnlySpan<int> groups, ReadOnlySpan<float> planes, Sorted sorted)
    {
        var faces = groups.Length;
        if (!NotNull(mesh) || !Assert(faces <= MaxFaces) || !Assert(planes.Length == faces)) return;
        if ((_to?.Length ?? 0) < faces) (_to, _keys) = (new int[Math.Max(faces, 4096)], new long[Math.Max(faces, 4096)]);
        var to = _to!.AsSpan(0, faces);
        var keys = _keys!.AsSpan(0, faces);
        for (var f = 0; f < Math.Min(faces, MaxFaces); f++)
            keys[f] = ((long)groups[f] << (32 + FaceBits)) |
                      ((long)(groups[f] < Directions ? Ordered(planes[f]) : 0) << FaceBits) | (uint)f;
        keys.Sort();
        var identity = true;
        for (var i = 0; i < Math.Min(faces, MaxFaces); i++)
        {
            var f = Face(keys[i]);
            to[f] = i;
            identity &= f == i;
        }

        var start = 0;
        for (var d = 0; d < Directions; d++)
        {
            Slabbed(sorted, d, keys[start..], planes);
            start += sorted.Counts[d];
        }

        _ = Assert(start <= faces);
        if (!identity) Moved(mesh, to);
    }

    private static int Face(long key) => Assert(key >= 0) ? (int)(key & ((1 << FaceBits) - 1)) : 0;

    // Float bits ordered for unsigned compare
    private static uint Ordered(float value)
    {
        _ = Finite(value); // a plane that is not finite went to the rest group, which is never ordered
        var bits = BitConverter.SingleToInt32Bits(value);
        return (uint)(bits ^ ((bits >> 31) & 0x7FFFFFFF)) ^ 0x80000000u;
    }

    // Slabs runs of about equal count; an empty one has planes 0
    private static void Slabbed(Sorted sorted, int d, ReadOnlySpan<long> keys, ReadOnlySpan<float> planes)
    {
        var n = sorted.Counts[d];
        if (!Assert(keys.Length >= n) || !Assert(n <= MaxFaces)) return;
        for (var j = 0; j < Slabs; j++)
        {
            var (from, to, at) = (j * n / Slabs, (j + 1) * n / Slabs, d * Slabs + j);
            sorted.SlabCounts[at] = to - from;
            (sorted.Low[at], sorted.High[at]) =
                to > from ? (planes[Face(keys[from])], planes[Face(keys[to - 1])]) : (0f, 0f);
            _ = Assert(sorted.Low[at] <= sorted.High[at]);
        }
    }

    private static void Moved(MeshData mesh, ReadOnlySpan<int> to)
    {
        if (!NotNull(mesh) || !Assert(to.Length <= MaxFaces)) return;
        Moved(mesh.xyz, 12, to);
        Moved(mesh.Flags, 4, to);
        if (mesh.Uv is not null) Moved(mesh.Uv, 8, to);
        if (mesh.Rgba is not null) Moved(mesh.Rgba, 16, to);
        if (mesh.NormalsCount > 0 && mesh.Normals is not null) Moved(mesh.Normals, 4, to);
        if (mesh.CustomFloats is { Count: > 0 } floats) Moved(floats.Values, 4 * floats.Count / mesh.VerticesCount, to);
        if (mesh.CustomShorts is { Count: > 0 } shorts) Moved(shorts.Values, 4 * shorts.Count / mesh.VerticesCount, to);
        if (mesh.CustomInts is { Count: > 0 } ints) Moved(ints.Values, 4 * ints.Count / mesh.VerticesCount, to);
        if (mesh.CustomBytes is { Count: > 0 } bytes) Moved(bytes.Values, 4 * bytes.Count / mesh.VerticesCount, to);
        if (mesh.XyzFacesCount > 0) Moved(mesh.XyzFaces, 1, to);
        if (mesh.TextureIndicesCount > 0) Moved(mesh.TextureIndices, 1, to);
        if (mesh.ColorMapIdsCount > 0)
        {
            Moved(mesh.ClimateColorMapIds, 1, to);
            Moved(mesh.SeasonColorMapIds, 1, to);
            Moved(mesh.FrostableBits, 1, to);
        }

        if (mesh.RenderPassCount > 0) Moved(mesh.RenderPassesAndExtraBits, 1, to);
    }

    private static void Moved<T>(T[]? array, int perFace, ReadOnlySpan<int> to) where T : unmanaged
    {
        var faces = to.Length;
        if (array is null || !Assert(perFace > 0) || !Assert(faces <= MaxFaces) || array.Length < perFace * faces) return;
        var bytes = Unsafe.SizeOf<T>() * perFace * faces;
        if ((_scratch?.Length ?? 0) < bytes) _scratch = new byte[Math.Max(bytes, 1 << 16)];
        var copy = MemoryMarshal.Cast<byte, T>(_scratch.AsSpan(0, bytes));
        array.AsSpan(0, perFace * faces).CopyTo(copy);
        for (var f = 0; f < Math.Min(faces, MaxFaces); f++)
            copy.Slice(f * perFace, perFace).CopyTo(array.AsSpan(to[f] * perFace, perFace));
    }

    // Planes shifted by InsertAt's whole-block vertex offset
    public static void Uploaded(int vao, MeshData mesh)
    {
        if (!NotNull(mesh) || mesh.XyzOffset % 12 != 0 || (ByLocation.Count == 0 && ByMesh.Count == 0)) return; // sorting idle
        var vertex = mesh.XyzOffset / 12;
        _ = ByLocation.Remove((vao, vertex));
        Sweep(Environment.TickCount64);
        Sorted sorted;
        lock (Gate)
        {
            if (!ByMesh.Remove(mesh, out var entry)) return;
            sorted = entry.Sorted;
        }

        if (sorted.Faces * 4 != mesh.VerticesCount) return;
        var last = 3 * (mesh.VerticesCount - 1);
        var moved = (mesh.xyz[0] - sorted.First.X, mesh.xyz[1] - sorted.First.Y, mesh.xyz[2] - sorted.First.Z);
        var also = (mesh.xyz[last] - sorted.Last.X, mesh.xyz[last + 1] - sorted.Last.Y, mesh.xyz[last + 2] - sorted.Last.Z);
        if (moved != also) return; // not the mesh as sorted
        // entry left ByMesh, so it moves in place: no per-upload garbage for ByLocation to keep alive into gen2
        _ = Assert(sorted.Low.Length == Directions * Slabs) && Assert(sorted.High.Length == Directions * Slabs);
        ReadOnlySpan<float> shift = [moved.Item1, moved.Item2, moved.Item3];
        for (var i = 0; i < Directions * Slabs; i++)
        {
            sorted.Low[i] += shift[i / Slabs / 2];
            sorted.High[i] += shift[i / Slabs / 2];
        }

        if (ByLocation.Count < MaxFaces) ByLocation[(vao, vertex)] = sorted;
        _ = Assert(sorted.Faces > 0);
    }

    // Pool-relative; null when not sorted
    public static Sorted? Of(int vao, int vertex) =>
        Assert(vertex >= 0) && Assert(ByLocation.Count <= MaxFaces) ? ByLocation.GetValueOrDefault((vao, vertex)) : null;

    public static void Gone(int vao, int vertex) => _ = ByLocation.Remove((vao, vertex)) || Assert(vertex >= 0);
}
