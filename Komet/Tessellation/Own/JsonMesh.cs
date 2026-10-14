using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// A JSON source mesh as AddJsonModelDataToMesh walks it, computed once instead of per block: per face facing, boundary
// bit, pass and DisableRandoms bit, pool atlas (GetMeshPoolForPass searches at every texture change), colour map ids,
// frost bit, custom data kind, indices relative to its first vertex; per vertex the clamped light-lerp coordinates.
// Valid for its block (flags follow its wind mode), its atlases, and while the mesh keeps arrays and counts; a face the
// engine faults or retries on (RetryTesselationException) makes it unusable.
internal sealed class JsonMesh
{
    public const int MaxFaces = 1 << 14, Corners = 4, Indices = 6, Lights = 25, MaxUses = 64;

    // The custom data a face's pass takes, and what its pool needs for them
    public const int Ints = 0, LiquidZero = 1, LiquidOwn = 2, Shorts = 3, NeedsFloats = 1, NeedsShorts = 2;

    private const int LiquidPass = (int)EnumChunkRenderPass.Liquid, TopSoilPass = (int)EnumChunkRenderPass.TopSoil;
    private const int PassBits = 0x3FF, MaxAtlases = 256, Coordinates = 9, Planes = 3;
    private const float Edge = 0.01f, Below = -0.0001f, Above = 1.0001f;

    // JsonTesselator's faceCoordLookup, axesByFacingLookup, indexesByFacingLookup (1.22.7) flattened; Tables verifies
    public static readonly int[] FaceCoord = [2, 0, 2, 0, 1, 1];
    public static readonly int[] Axes = [0, 1, 1, 2, 0, 1, 1, 2, 0, 2, 0, 2];
    public static readonly int[] CornerOrder = [3, 2, 1, 0, 3, 1, 2, 0, 2, 3, 0, 1, 2, 0, 3, 1, 3, 2, 1, 0, 1, 0, 3, 2];

    private readonly MeshData _mesh;
    private readonly Block _owner;
    private readonly EnumWindBitMode _wind;
    private readonly float _waveMinY;
    private readonly int _vertices, _faces, _quantity;
    private readonly float[] _xyz, _uv;
    private readonly int[] _flags, _indices, _textureIds;
    private readonly byte[] _xyzFaces, _textureIndices, _climate, _season;
    private readonly short[]? _passes;
    private readonly bool[]? _frost;
    private readonly object? _ints, _floats, _shorts;
    private int[] _atlases;

    private JsonMesh(MeshData mesh, Block owner, int[] atlases, int quantity)
    {
        (_mesh, _owner, _atlases, _quantity) = (mesh, owner, atlases, quantity);
        (_wind, _waveMinY) = (owner.VertexFlags.WindMode, WaveMinY(owner));
        (_vertices, _faces) = (mesh.VerticesCount, mesh.XyzFacesCount);
        (_xyz, _uv, _flags, _indices, _textureIds) = (mesh.xyz, mesh.Uv, mesh.Flags, mesh.Indices, mesh.TextureIds);
        (_xyzFaces, _textureIndices, _climate, _season) =
            (mesh.XyzFaces, mesh.TextureIndices, mesh.ClimateColorMapIds, mesh.SeasonColorMapIds);
        (_passes, _frost, _ints, _floats, _shorts) =
            (mesh.RenderPassesAndExtraBits, mesh.FrostableBits, mesh.CustomInts, mesh.CustomFloats, mesh.CustomShorts);
        Faces = new Face[_faces];
        Lerp = new float[2 * Corners * _faces];
        Relative = new int[Indices * _faces];
        _ = Assert(_faces <= MaxFaces) && Assert(_vertices >= Corners * _faces);
    }

    public Face[] Faces { get; }

    // lx and ly of every vertex of a face with a facing, as GameMath.Clamp gives them
    public float[] Lerp { get; }

    // Each face's six indices minus its first vertex: the engine adds the pool's count minus that vertex
    public int[] Relative { get; }

    // The distinct pools the faces draw into, by pass (-1: the block's) and atlas, with what each needs
    public Use[] Uses { get; private set; } = [];

    // Every face one the engine draws without faulting or retrying
    public bool Usable { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "waveFlagMinY")]
    private static extern ref float WaveMinY(Block block);

    // Engine tables still the ones above, and WaveMinY's field present (else JSON blocks stay the engine's)
    public static bool Tables(ILogger? logger)
    {
        var json = typeof(JsonTesselator);
        var same = AccessTools.DeclaredField(typeof(Block), "waveFlagMinY")?.FieldType == typeof(float) &&
                   AccessTools.DeclaredField(json, "faceCoordLookup")?.GetValue(null) is int[] coord &&
                   coord.AsSpan().SequenceEqual(FaceCoord) &&
                   AccessTools.DeclaredField(json, "axesByFacingLookup")?.GetValue(null) is int[][] axes &&
                   Flat(axes, 2).AsSpan().SequenceEqual(Axes) &&
                   AccessTools.DeclaredField(json, "indexesByFacingLookup")?.GetValue(null) is int[][] corners &&
                   Flat(corners, Corners).AsSpan().SequenceEqual(CornerOrder);
        if (!same) logger?.Warning("Komet OwnTessellation: JsonTesselator's tables changed, the engine draws the JSON blocks");
        return Assert(FaceCoord.Length == TessSeams.Faces) && Assert(Axes.Length == 2 * TessSeams.Faces) && same;
    }

    private static int[] Flat(int[][] rows, int width)
    {
        if (!Assert(rows.Length == TessSeams.Faces) || !Assert(width <= Corners)) return [];
        var flat = new int[TessSeams.Faces * width];
        for (var f = 0; f < TessSeams.Faces; f++)
        {
            if (rows[f] is not { } row || row.Length != width) return [];
            row.CopyTo(flat, f * width);
        }

        return flat;
    }

    // Every array the engine reads for the faces, and supported counts and face shape (else it warns or faults)
    public static bool Shaped(MeshData mesh)
    {
        if (!NotNull(mesh)) return false;
        var (faces, vertices) = (mesh.XyzFacesCount, mesh.VerticesCount);
        if (mesh.VerticesPerFace != Corners || mesh.IndicesPerFace != Indices || !Index(faces, MaxFaces + 1) ||
            vertices < Corners * faces || vertices > Corners * MaxFaces) return false;
        return mesh.xyz?.Length >= 3 * vertices && mesh.Uv?.Length >= 2 * vertices && mesh.Flags?.Length >= vertices &&
               mesh.Indices?.Length >= Indices * faces && mesh.XyzFaces?.Length >= faces &&
               mesh.TextureIndices?.Length >= faces && mesh.TextureIds is not null &&
               mesh.ClimateColorMapIds?.Length >= faces && mesh.SeasonColorMapIds?.Length >= faces &&
               (mesh.FrostableBits is null || mesh.FrostableBits.Length >= faces) &&
               (mesh.RenderPassesAndExtraBits is not { Length: > 0 } passes || passes.Length >= faces);
    }

    // Table, unusable where a face faults or retries (kept: no rework per block); null for a mesh Shaped rejects
    public static JsonMesh? Build(MeshData mesh, Block owner, int[] atlases, int quantity)
    {
        if (!Shaped(mesh) || !NotNull(owner) || owner.VertexFlags is null) return null;
        var built = new JsonMesh(mesh, owner, atlases, quantity);
        var uses = new List<Use>();
        for (var l = 0; l < Math.Min(built._faces, MaxFaces); l++)
        {
            if (built.Take(mesh, l) is not { } face) return built;
            built.Faces[l] = face;
            var needs = face.Custom switch { LiquidZero or LiquidOwn => NeedsFloats, Shorts => NeedsShorts, _ => 0 };
            var at = Find(uses, face.Pass, face.Atlas);
            if (at >= 0) uses[at] = uses[at] with { Needs = uses[at].Needs | needs };
            else if (uses.Count < MaxUses) uses.Add(new Use(face.Pass, face.Atlas, needs));
            else return built;
        }

        built.Uses = [.. uses];
        built.Usable = Assert(built.Uses.Length <= MaxUses);
        return built;
    }

    // The use of this pass and atlas, or -1 (a loop: FindIndex's lambda would allocate a closure per face)
    private static int Find(List<Use> uses, int pass, int atlas)
    {
        if (!NotNull(uses)) return -1;
        for (var i = 0; i < Math.Min(uses.Count, MaxUses); i++)
            if (uses[i].Pass == pass && uses[i].Atlas == atlas)
                return i;
        _ = Assert(uses.Count <= MaxUses); // Build adds no more
        return -1;
    }

    // Still the mesh, block and atlases it was made for, the mesh with the arrays and counts it had
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Fits(MeshData mesh, Block owner, int[] atlases, int quantity)
    {
        if (!NotNull(mesh) || !ReferenceEquals(mesh, _mesh) || !ReferenceEquals(owner, _owner) || quantity != _quantity ||
            owner.VertexFlags?.WindMode != _wind ||
            BitConverter.SingleToInt32Bits(WaveMinY(owner)) != BitConverter.SingleToInt32Bits(_waveMinY)) return false;
        if (mesh.VerticesCount != _vertices || mesh.XyzFacesCount != _faces || mesh.VerticesPerFace != Corners ||
            mesh.IndicesPerFace != Indices || !ReferenceEquals(mesh.xyz, _xyz) || !ReferenceEquals(mesh.Uv, _uv) ||
            !ReferenceEquals(mesh.Flags, _flags) || !ReferenceEquals(mesh.Indices, _indices) ||
            !ReferenceEquals(mesh.TextureIds, _textureIds) || !ReferenceEquals(mesh.XyzFaces, _xyzFaces) ||
            !ReferenceEquals(mesh.TextureIndices, _textureIndices) || !ReferenceEquals(mesh.ClimateColorMapIds, _climate) ||
            !ReferenceEquals(mesh.SeasonColorMapIds, _season) || !ReferenceEquals(mesh.RenderPassesAndExtraBits, _passes) ||
            !ReferenceEquals(mesh.FrostableBits, _frost) || !ReferenceEquals(mesh.CustomInts, _ints) ||
            !ReferenceEquals(mesh.CustomFloats, _floats) || !ReferenceEquals(mesh.CustomShorts, _shorts)) return false;
        if (ReferenceEquals(atlases, _atlases)) return true;
        // a ChunkTesselator of the same thread with the same atlases
        if (!atlases.AsSpan().SequenceEqual(_atlases)) return false;
        _atlases = atlases;
        return Assert(atlases.Length >= quantity);
    }

    // Face l as the engine's loop sees it, with indices and light coordinates; null where it faults or retries
    private Face? Take(MeshData mesh, int l)
    {
        var (xyz, first) = (mesh.xyz, Corners * l);
        if (!Index(l, _faces)) return null;
        var facing = mesh.XyzFaces[l] - 1;
        var texture = mesh.TextureIndices[l];
        if (facing >= TessSeams.Faces || !Index(texture, mesh.TextureIds.Length)) return null;
        var atlas = Search(_atlases, _quantity, mesh.TextureIds[texture]);
        var pass = mesh.RenderPassesAndExtraBits is { Length: > 0 } passes ? passes[l] : -1;
        var fixedXz = pass >= JsonTesselator.DisableRandomsFlag;
        if (fixedXz) pass &= PassBits;
        var custom = pass switch
        {
            LiquidPass when mesh.CustomFloats is null => LiquidZero,
            LiquidPass => LiquidOwn,
            TopSoilPass => Shorts,
            _ => Ints
        };
        if (atlas < 0 || !Copied(mesh, custom, first)) return null;
        for (var k = 0; k < Indices; k++) Relative[Indices * l + k] = mesh.Indices[Indices * l + k] - first;
        var even = true;
        if (facing >= 0)
            for (var k = 0; k < Corners; k++)
            {
                var v = first + k;
                var (lx, ly) = (GameMath.Clamp(xyz[3 * v + Axes[2 * facing]], 0f, 1f),
                    GameMath.Clamp(xyz[3 * v + Axes[2 * facing + 1]], 0f, 1f));
                (Lerp[2 * v], Lerp[2 * v + 1]) = (lx, ly);
                even &= !float.IsNaN(lx) && !float.IsNaN(ly);
            }

        var frost = -1;
        if (mesh.FrostableBits is { } bits) frost = bits[l] ? 1 : 0;
        return new Face(facing, facing >= 0 && OnBoundary(xyz, l, facing), fixedXz, pass >= 0 ? pass : -1, atlas,
            mesh.SeasonColorMapIds[l], mesh.ClimateColorMapIds[l], frost, custom, even);
    }

    // The custom data the engine copies from the source mesh for the face's four vertices are there
    private static bool Copied(MeshData mesh, int custom, int first)
    {
        _ = Assert(first >= 0);
        return custom switch
        {
            LiquidOwn => mesh.CustomInts?.Values?.Length >= first + Corners &&
                         mesh.CustomFloats?.Values?.Length >= 2 * (first + Corners),
            Shorts => mesh.CustomShorts?.Values?.Length >= 2 * (first + Corners),
            _ => Index(custom, Shorts + 1)
        };
    }

    // GetMeshPoolForPass's search: the texture's first atlas, the first looked at whatever the count; -1 for none
    private static int Search(int[] atlases, int quantity, int texture)
    {
        if (!NotNull(atlases) || !Assert(quantity >= 1 && quantity <= atlases.Length)) return -1;
        for (var i = 0; i < Math.Min(quantity, MaxAtlases); i++)
            if (atlases[i] == texture)
                return i;
        return -1;
    }

    // Engine boundary test: facing-axis coordinate of vertices 0 and 2 at the block's side (within 0.01), rest inside
    private static bool OnBoundary(float[] xyz, int l, int facing)
    {
        if (!Index(facing, TessSeams.Faces) || !Assert(xyz.Length >= 3 * Corners * (l + 1))) return false;
        var flip = facing is 1 or 2 or 4;
        var at = 3 * Corners * l + FaceCoord[facing];
        var first = flip ? 1f - xyz[at] : xyz[at];
        if (!(first <= Edge)) return false;
        var second = flip ? 1f - xyz[at + 2 * Planes] : xyz[at + 2 * Planes];
        if (!(second <= Edge)) return false;
        for (var m = 0; m < Coordinates; m++)
        {
            if (m is >= Planes and < 2 * Planes) continue; // the engine skips vertex 1
            var i = 3 * Corners * l + m;
            if (i == at || i == at + 2 * Planes) continue;
            if (xyz[i] < Below || xyz[i] > Above) return false;
        }

        return true;
    }

    // Facing -1: none (block's own light). Pass/Frost -1: the block's. Even: no light coordinate is NaN.
    internal readonly record struct Face(
        int Facing,
        bool Boundary,
        bool Fixed,
        int Pass,
        int Atlas,
        byte Season,
        byte Climate,
        int Frost,
        int Custom,
        bool Even);

    internal readonly record struct Use(int Pass, int Atlas, int Needs);
}
