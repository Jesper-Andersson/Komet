using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Shared by OwnCube, OwnTopsoil and OwnJsonSnow, each step the engine's for one face: CalcBlockFaceLight, four
// AddVertexWithFlags, Add4, AddQuadIndices, UpdateChunkMinMax. A face goes into the arrays at once when it fits as they
// stand, else through the engine's methods, the only growers: GrowVertexBuffer sizes from the current vertex count, so
// growing at a face's first vertex would change lengths.
internal static class OwnQuads
{
    public const int Corners = 4, AllFaces = (1 << Faces) - 1, Offsets = 8; // q7 is the last quad offset a face reads
    public const int Unknown = int.MinValue; // a texture id where the engine would fault
    private const int XyzFloats = 3 * Corners, UvFloats = 2 * Corners, QuadIndices = 6;

    // The corner light each vertex takes, two bits a vertex: 3, 1, 0, 2 for q7, q5, q4, q6
    private const int CornerOrder = 0b10_00_01_11;

    // The pools by lod, pass and atlas that ChunkTesselator.GetPoolForPass and GetMeshPoolForPass read
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentModeldataByRenderPassByLodLevel")]
    internal static extern ref MeshData[][][]? Lods(ChunkTesselator tesselator);

    // Pre-choice checks: standard moves, a chunk cell, halo light, face scratch, offsets; false where the engine faults
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Ready(TCTCache vars, int faces, [NotNullWhen(true)] out int[]? rgbs)
    {
        rgbs = null;
        var (tct, quads, corners) = (vars.tct, vars.blockFaceVertices, vars.CurrentLightRGBByCorner);
        if (tct is null || quads is not { Length: >= Faces } || !NotNull(corners) || !Assert(corners.Length >= Corners) ||
            !Standard(TileSideEnum.MoveIndex) || !Index(vars.extIndex3d - Origin, ExtCells - 2 * Origin) ||
            RgbsExt(tct) is not { Length: ExtCells } light) return false;
        for (var i = 0; i < Faces; i++)
            if ((faces & (1 << i)) != 0 && quads[i] is not { Length: >= Offsets })
                return false;
        rgbs = light;
        return true;
    }

    // ChunkTesselator.GetPoolForPass(pass, lod): the pass's meshes by atlas, or null where the engine would fault
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static MeshData[]? Pools(ChunkTesselator tesselator, EnumChunkRenderPass pass, int lod = 1)
    {
        var lods = Lods(tesselator);
        if (!NotNull(lods) || !Index(lod, lods.Length) || lods[lod] is not { } passes) return null;
        return Index((int)pass, passes.Length) ? passes[(int)pass] : null;
    }

    // Face texture id: hash alternate where variants have the face, else its own; Unknown where the engine would fault
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Texture(BakedCompositeTexture[][]? variants, int[] fast, int i, int hash, bool lenient)
    {
        if (!Index(i, fast.Length)) return Unknown;
        if (variants is null) return fast[i];
        if (i >= variants.Length) return lenient ? fast[i] : Unknown;
        if (variants[i] is not { } choices) return fast[i];
        return choices.Length > 0 && choices[GameMath.Mod(hash, choices.Length)] is { } baked ? baked.TextureSubId : Unknown;
    }

    // positions[id] and its atlas pool mesh with colour map ints (all passes but Liquid); false where the engine faults
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Take(TextureAtlasPosition[] positions, MeshData[] pools, int id,
        [NotNullWhen(true)] out TextureAtlasPosition? position, [NotNullWhen(true)] out MeshData? mesh)
    {
        (position, mesh) = (null, null);
        if ((uint)id >= (uint)positions.Length || positions[id] is not { } found) return false;
        if (!Index(found.atlasNumber, pools.Length) || pools[found.atlasNumber] is not { } pool ||
            !NotNull(pool.CustomInts)) return false;
        (position, mesh) = (found, pool);
        return true;
    }

    // CalcBlockFaceLight(t, front) into the corners, non-JSON (engine mixes in a JSON block's light): no smooth
    // shadows: front cell's light in all four; else FaceLight's corners while it lights engine faces, else the engine
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Light(TCTCache vars, Block block, int t, int front, int[] rgbs, int[] corners)
    {
        if (!vars.aoAndSmoothShadows || (block.SideAo & (1 << t)) == 0)
        {
            if (block.DrawType != EnumDrawType.JSON && Index(front, rgbs.Length))
            {
                corners.AsSpan(0, Corners).Fill(rgbs[front]);
                return;
            }
        }
        else if (FaceLight.Lit(vars, t, front)) return;

        _ = FaceLight.Calc(vars, t, front); // the engine's, through its entry and so through FaceLight's prefix
    }

    // DrawBlockFace for face i, height h, corners lit: sides take that texture share; v as the engine's, even at 1f
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Face(TCTCache vars, int i, FastVec3f[] q, TextureAtlasPosition position, MeshData mesh, int flags,
        int colorMap, float h, float x, float y, float z)
    {
        if (!Index(i, Faces) || !Assert(q.Length >= Offsets)) return; // callers' faces, offsets Ready checked
        var quad = default(Quad);
        var (top, share) = (position.y2, i <= West ? h : 1f);
        var v = top + (position.y1 - top) * share;
        quad.Set(0, x + q[7].X, y + q[7].Y * h, z + q[7].Z, position.x2, top);
        quad.Set(1, x + q[5].X, y + q[5].Y * h, z + q[5].Z, position.x2, v);
        quad.Set(2, x + q[4].X, y + q[4].Y * h, z + q[4].Z, position.x1, v);
        quad.Set(3, x + q[6].X, y + q[6].Y * h, z + q[6].Z, position.x1, top);
        var first = mesh.VerticesCount;
        Vertices(mesh, ref quad, vars.CurrentLightRGBByCorner, flags);
        Ints(mesh, colorMap);
        Indices(mesh, first);
        Bounds(vars, x, y, z);
        Bounds(vars, x + 1f, y + h, z + 1f);
    }

    // Four AddVertexWithFlags (corner light 3, 1, 0, 2): at once when they fit without growing, else the engine's calls
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Vertices(MeshData mesh, ref Quad quad, int[] corners, int flags)
    {
        var n = mesh.VerticesCount;
        if (!Fits(mesh, n) || !Assert(corners.Length >= Corners))
        {
            Engine(mesh, ref quad, corners, flags);
            return;
        }

        Place(mesh, ref quad, n);
        // AddVertexWithFlags stores the int through a pointer: the machine's byte order, as here
        var light = MemoryMarshal.Cast<byte, int>(mesh.Rgba.AsSpan(n * 4, 4 * Corners));
        (light[0], light[1], light[2], light[3]) = (corners[3], corners[1], corners[0], corners[2]);
        mesh.Flags.AsSpan(n, Corners).Fill(flags);
        mesh.VerticesCount = n + Corners;
    }

    // Four AddVertexWithFlags of one colour, flags low at 0 and 3, high at 1 and 2 (cross: wind at top only)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Uniform(MeshData mesh, ref Quad quad, int color, int low, int high)
    {
        var n = mesh.VerticesCount;
        if (!Fits(mesh, n))
        {
            for (var k = 0; k < Corners; k++)
                mesh.AddVertexWithFlags(quad.Xyz[3 * k], quad.Xyz[3 * k + 1], quad.Xyz[3 * k + 2], quad.Uv[2 * k],
                    quad.Uv[2 * k + 1], color, k is 0 or 3 ? low : high);
            return;
        }

        Place(mesh, ref quad, n);
        MemoryMarshal.Cast<byte, int>(mesh.Rgba.AsSpan(n * 4, 4 * Corners)).Fill(color); // native byte order
        var all = mesh.Flags.AsSpan(n, Corners);
        (all[0], all[1], all[2], all[3]) = (low, high, high, low);
        mesh.VerticesCount = n + Corners;
        _ = Assert(mesh.VerticesCount <= mesh.VerticesMax);
    }

    // Four vertices fit at n without growing any array: the case Vertices and Uniform write at once
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Fits(MeshData mesh, int n) =>
        NotNull(mesh) && n >= 0 && n + Corners <= mesh.VerticesMax && mesh.xyz?.Length >= (n + Corners) * 3 &&
        mesh.Uv?.Length >= (n + Corners) * 2 && mesh.Rgba?.Length >= (n + Corners) * 4 && mesh.Flags?.Length >= n + Corners;

    // The quad's positions and texture coordinates at vertex n, where Fits holds
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Place(MeshData mesh, ref Quad quad, int n)
    {
        _ = Assert(n >= 0) && Assert(Fits(mesh, n)); // the callers' check
        ((ReadOnlySpan<float>)quad.Xyz).CopyTo(mesh.xyz.AsSpan(n * 3, XyzFloats));
        ((ReadOnlySpan<float>)quad.Uv).CopyTo(mesh.Uv.AsSpan(n * 2, UvFloats));
    }

    private static void Engine(MeshData mesh, ref Quad quad, int[] corners, int flags)
    {
        if (!NotNull(mesh) || !Assert(corners.Length >= Corners)) return;
        for (var k = 0; k < Corners; k++)
            mesh.AddVertexWithFlags(quad.Xyz[3 * k], quad.Xyz[3 * k + 1], quad.Xyz[3 * k + 2], quad.Uv[2 * k],
                quad.Uv[2 * k + 1], corners[(CornerOrder >> (2 * k)) & 3], flags);
    }

    // CustomInts.Add4(value)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Ints(MeshData mesh, int value)
    {
        var ints = mesh.CustomInts;
        var (values, count) = NotNull(ints) ? (ints.Values, ints.Count) : (null, 0); // Take checked they are there
        if (values is null || count < 0 || count + Corners > values.Length)
        {
            mesh.CustomInts.Add4(value);
            return;
        }

        values.AsSpan(count, Corners).Fill(value);
        ints.Count = count + Corners;
    }

    // AddQuadIndices(first): two triangles, 0 1 2 and 0 2 3
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Indices(MeshData mesh, int first)
    {
        var (count, indices) = (mesh.IndicesCount, mesh.Indices);
        _ = Assert(first >= 0); // the vertex count before the face's
        if (count < 0 || count + QuadIndices > mesh.IndicesMax || indices is null || count + QuadIndices > indices.Length)
        {
            mesh.AddQuadIndices(first);
            return;
        }

        var six = indices.AsSpan(count, QuadIndices);
        (six[0], six[1], six[2], six[3], six[4], six[5]) = (first, first + 1, first + 2, first, first + 2, first + 3);
        mesh.IndicesCount = count + QuadIndices;
    }

    // TCTCache.UpdateChunkMinMax: a value that lowers the minimum is not compared with the maximum
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Bounds(TCTCache vars, float x, float y, float z)
    {
        if (x < vars.xMin) vars.xMin = x;
        else if (x > vars.xMax) vars.xMax = x;
        if (y < vars.yMin) vars.yMin = y;
        else if (y > vars.yMax) vars.yMax = y;
        if (z < vars.zMin) vars.zMin = z;
        else if (z > vars.zMax) vars.zMax = z;
    }

    [InlineArray(XyzFloats)]
    internal struct QuadXyz
    {
        private float _element;
    }

    [InlineArray(UvFloats)]
    internal struct QuadUv
    {
        private float _element;
    }

    // One face's vertices in the engine's order, q7, q5, q4, q6
    internal struct Quad
    {
        public QuadXyz Xyz;
        public QuadUv Uv;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int k, float x, float y, float z, float u, float v)
        {
            if (!Index(k, Corners)) return;
            (Xyz[3 * k], Xyz[3 * k + 1], Xyz[3 * k + 2]) = (x, y, z);
            (Uv[2 * k], Uv[2 * k + 1]) = (u, v);
        }
    }

    [InlineArray(Faces)]
    internal struct FaceTextures
    {
        private TextureAtlasPosition? _element;
    }

    [InlineArray(Faces)]
    internal struct FaceMeshes
    {
        private MeshData? _element;
    }

    // Each drawn face's atlas position and pool mesh, chosen before anything is written
    internal struct Chosen
    {
        public FaceTextures Positions;
        public FaceMeshes Meshes;
    }
}
