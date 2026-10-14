using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// JsonTesselator.Tesselate for plain JSON blocks (also JsonAndSnowLayer's JSON part): sand, gravel, stones, every class
// keeping Block's OnJsonTesselation, ~7000 vanilla variants. World-join traces: Tesselate 47-49 % of tessellation, its
// light ~21 %, its mesh copy 15-17 %; here the copy runs on JsonMesh tables via JsonEmit. Engine order: six faces'
// light (FaceLight.Fused, else the engine loop), lod 0 mesh where NotSurrounded, darkness test (sum 4934475: default
// mesh at lod 0), lod 2 mesh at lods 2-3, alternates by MurmurHash3Mod. The engine keeps the block, unwritten, for
// other OnJsonTesselation overrides, possible bleed, a block entity at the position, decor or transforms, no table, bad
// pools. Block's own OnJsonTesselation ORs constant wind bits into the shared mesh, so it runs once at table build,
// TessSafety.FlagWriters overrides (leaves, plants, ferns, seaweed) rewrite flags per position, so they run per mesh
// via TessSafety.Json and the copy reads their flags.
internal static class JsonMesher
{
    private const int MaxMeshes = 4096, MaxPlan = 3, MaxBlocks = 1 << 20, Own = JsonMesh.Lights - 1;

    // Light, positions, tables and plan of this thread's blocks
    [ThreadStatic] private static Scratch? _scratch;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockEntitiesOfChunk")]
    private static extern ref Dictionary<BlockPos, BlockEntity>? Entities(TCTCache vars);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "TextureIdToReturnNum")]
    private static extern ref int[]? Atlases(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "quantityAtlasses")]
    private static extern ref int Quantity(ChunkTesselator tesselator);

    // Private fields the accessors read, with types (a missing one would throw on a tessellation thread)
    internal static bool Fields(ILogger? logger) => TessSeams.Fields(nameof(OwnTessellation), logger, "the engine draws the JSON blocks",
    [
        (typeof(TCTCache), "blockEntitiesOfChunk", typeof(Dictionary<BlockPos, BlockEntity>)),
        (typeof(ChunkTesselator), "TextureIdToReturnNum", typeof(int[])),
        (typeof(ChunkTesselator), "quantityAtlasses", typeof(int))
    ]);

    // The thread's BlockPos for JsonAndSnowLayerTesselator's question to the block below, at (x, y, z) of the dimension
    internal static BlockPos Below(int dimension, int x, int y, int z)
    {
        var s = _scratch ??= new Scratch();
        _ = Assert(dimension >= 0) && NotNull(s.Under);
        return s.Under.SetDimension(dimension).Set(x, y, z);
    }

    // The block's meshes, or false, nothing written, for the engine. drawType: the slot's, the block's own.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Tesselate(TCTCache vars, Block block, EnumDrawType drawType)
    {
        var s = _scratch ??= new Scratch();
        if (!ReferenceEquals(s.World, vars.shapes))
        {
            // another world's meshes and blocks (or none yet): let the last one's go
            (s.World, s.Kinds, s.Last) = (vars.shapes, [], null);
            s.Meshes.Clear();
        }

        if (!NotNull(block) || !Eligible(vars, block, drawType, s, out var rgbs, out var mesh)) return false;
        var sum = Light(vars, s.Json, rgbs);
        s.Count = 0;
        if (!Plan(vars, block, mesh, sum, s)) return false;
        Draw(vars, block, s);
        _ = Assert(s.Count <= MaxPlan);
        return true;
    }

    // Pre-light checks: plain kinds, halo, default mesh (made only if missing), ice-check fluid, no block entity
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Eligible(TCTCache vars, Block block, EnumDrawType drawType, Scratch s,
        [NotNullWhen(true)] out int[]? rgbs, [NotNullWhen(true)] out MeshData? mesh)
    {
        (rgbs, mesh) = (null, null);
        if (!OwnTessellation.JsonActive || block.DrawType != drawType || block.CanReceiveBleed || block.IsMissing ||
            block.RandomizeRotations || block.RandomSizeAdjust != 0f || vars.preRotationMatrix is not null ||
            vars.OverlayTextureFace is not null || !OwnQuads.Ready(vars, 0, out var light) || !Plain(s, block) ||
            vars.shapes?.blockModelDatas is not { } models || !Index(block.BlockId, models.Length) ||
            models[block.BlockId] is not { } found) return false;
        var fluid = vars.extIndex3d + block.IceCheckOffset * TessSeams.Plane;
        if (FluidsExt(vars.tct) is not { Length: ExtCells } fluids || !Index(fluid, ExtCells) || fluids[fluid] is null) return false;
        // Engine's lookup, skipped for decors: any entry at the position, even null, leaves the block to the engine
        if (!vars.isDecorTesselate && Entities(vars) is { Count: > 0 } entities)
        {
            _ = s.At.SetDimension(vars.dimension).Set(vars.posX, vars.posY, vars.posZ);
            if (entities.ContainsKey(s.At)) return false;
        }

        (rgbs, mesh) = (light, found);
        return true;
    }

    // Block's own OnJsonTesselation down the class chain, or only FlagWriters' overrides (s.Writes); cached by BlockId
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Plain(Scratch s, Block block)
    {
        var (kinds, id) = (s.Kinds, block.BlockId);
        if ((uint)id < (uint)kinds.Length && ReferenceEquals(kinds[id].Owner, block))
        {
            s.Writes = kinds[id].Writes;
            return kinds[id].IsPlain || (kinds[id].Writes && !TessSafety.Broken);
        }

        s.Writes = false;
        if (!Index(id, MaxBlocks)) return false;
        if (id >= kinds.Length) Array.Resize(ref s.Kinds, Math.Min(Math.Max(id + 1, 2 * kinds.Length), MaxBlocks));
        var hook = block.GetType().GetMethod(nameof(Block.OnJsonTesselation), BindingFlags.Public | BindingFlags.Instance,
            TessSafety.JsonArgs);
        var plain = NotNull(hook) && hook.DeclaringType == typeof(Block); // Block's own at least
        s.Writes = !plain && TessSafety.WritesFlags(block);
        s.Kinds[id] = new Kind(block, plain, s.Writes);
        return plain || s.Writes;
    }

    // SetUpLightRGBs into json[0..24] plus sum: FaceLight.Fused, else the engine loop via CalcBlockFaceLight's entry
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Light(TCTCache vars, int[] json, int[] rgbs)
    {
        if (FaceLight.Fused(vars, json, out var sum)) return sum;
        var (e, corners) = (vars.extIndex3d, vars.CurrentLightRGBByCorner);
        _ = Assert(json.Length == JsonMesh.Lights) && Assert(corners.Length >= JsonMesh.Corners);
        for (var t = 0; t < Faces; t++)
        {
            sum += FaceLight.Calc(vars, t, e + Moves[t]);
            corners.AsSpan(0, JsonMesh.Corners).CopyTo(json.AsSpan(JsonMesh.Corners * t));
        }

        json[Own] = rgbs[e];
        return sum + json[Own];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Plan(TCTCache vars, Block block, MeshData mesh, long sum, Scratch s)
    {
        _ = Assert(s.Count == 0) && NotNull(mesh);
        var farther = block.DoNotRenderAtLod2;
        if (block.Lod0Shape is not null)
        {
            if (!JsonTesselator.NotSurrounded(vars, vars.extIndex3d)) farther = true;
            else if (!Add(vars, block, block.Lod0Mesh, 0, s)) return false;
        }

        if (sum == JsonTesselator.Darkness) return Add(vars, block, mesh, 0, s);
        if (block.Lod2Mesh is not { } far) return Add(vars, block, mesh, farther ? 2 : 1, s);
        return Add(vars, block, mesh, 2, s) && Add(vars, block, far, 3, s);
    }

    // doMesh up to AddJsonModelDataToMesh: empty draws nothing, position alternates, FlagWriters rewrite; needs table
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Add(TCTCache vars, Block block, MeshData? mesh, int lod, Scratch s)
    {
        if (mesh is null || !Index(s.Count, MaxPlan)) return false;
        if (mesh.VerticesCount == 0) return true;
        var shapes = vars.shapes;
        var all = lod switch
        {
            0 => shapes.altblockModelDatasLod0,
            1 or 2 => shapes.altblockModelDatasLod1,
            _ => shapes.altblockModelDatasLod2
        };
        if (all is null || !Index(vars.blockId, all.Length)) return false;
        if (all[vars.blockId] is { } alternates)
        {
            if (alternates.Length == 0) return false;
            var y = block.RandomizeAxes == EnumRandomizeAxes.XYZ ? vars.posY : 0;
            if (alternates[GameMath.MurmurHash3Mod(vars.posX, y, vars.posZ, alternates.Length)] is not { } chosen) return false;
            mesh = chosen;
        }

        if (s.Writes && !Flags(vars, block, ref mesh, s)) return false;
        if (Table(vars, block, mesh, s) is not { } table || !Pools(vars, table, lod)) return false;
        s.Steps[s.Count++] = new Planned(mesh, table, lod);
        return true;
    }

    // A FlagWriters override as doMesh calls it; false if it returned another mesh or light than a flag writer does
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Flags(TCTCache vars, Block block, ref MeshData mesh, Scratch s)
    {
        if (BlocksExt(vars.tct) is not { } blocks || !Assert(s.Writes)) return false;
        var (same, light) = (mesh, s.Json);
        _ = s.At.SetDimension(vars.dimension).Set(vars.posX, vars.posY, vars.posZ);
        TessSafety.Json(block, ref same, ref light, s.At, blocks, vars.extIndex3d);
        if (same is null || !ReferenceEquals(light, s.Json) || !ReferenceEquals(same.xyz, mesh.xyz)) return false;
        mesh = same;
        return true;
    }

    // The mesh's table, built at first sight where doMesh calls OnJsonTesselation (Block's own runs then, once)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static JsonMesh? Table(TCTCache vars, Block block, MeshData mesh, Scratch s)
    {
        var (atlases, quantity) = (Atlases(vars.tct), Quantity(vars.tct));
        if (atlases is null || quantity < 1 || quantity > atlases.Length) return null;
        // the last table first: runs of one block type (sand, gravel, stones) ask for the same mesh block after block
        var known = s.Last;
        if ((known is not null && known.Fits(mesh, block, atlases, quantity)) ||
            (s.Meshes.TryGetValue(mesh, out known) && known.Fits(mesh, block, atlases, quantity)))
        {
            s.Last = known;
            return known.Usable ? known : null;
        }

        if (!JsonMesh.Shaped(mesh) || BlocksExt(vars.tct) is not { } blocks) return null;
        var (same, light) = (mesh, s.Json);
        _ = s.At.SetDimension(vars.dimension).Set(vars.posX, vars.posY, vars.posZ);
        if (!s.Writes) block.OnJsonTesselation(ref same, ref light, s.At, blocks, vars.extIndex3d);
        if (!Assert(ReferenceEquals(same, mesh)) || !Assert(ReferenceEquals(light, s.Json)) ||
            JsonMesh.Build(mesh, block, atlases, quantity) is not { } built) return null;
        if (s.Meshes.Count >= MaxMeshes) s.Meshes.Clear();
        (s.Meshes[mesh], s.Last) = (built, built);
        return built.Usable ? built : null;
    }

    // Every pool the mesh draws into at this lod is there, consistent, with the parts its faces append to
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Pools(TCTCache vars, JsonMesh table, int lod)
    {
        var lods = OwnQuads.Lods(vars.tct);
        if (lods is null || !Index(lod, lods.Length) || lods[lod] is not { } passes) return false;
        foreach (var use in table.Uses.Bounded(JsonMesh.MaxUses))
        {
            var pass = use.Pass >= 0 ? use.Pass : (int)vars.RenderPass;
            if (!Index(pass, passes.Length) || passes[pass] is not { } atlases || !Index(use.Atlas, atlases.Length) ||
                atlases[use.Atlas] is not { } pool) return false;
            var (n, max) = (pool.VerticesCount, pool.VerticesMax);
            if (n < 0 || n > max || !(pool.xyz?.Length >= 3 * max) || !(pool.Uv?.Length >= 2 * max) ||
                !(pool.Rgba?.Length >= 4 * max) || !(pool.Flags?.Length >= max) || pool.Indices is null ||
                pool.IndicesCount < 0 || pool.CustomInts is null ||
                ((use.Needs & JsonMesh.NeedsFloats) != 0 && pool.CustomFloats is null) ||
                ((use.Needs & JsonMesh.NeedsShorts) != 0 && pool.CustomShorts is null)) return false;
        }

        return true;
    }

    // AddJsonModelDataToMesh per planned mesh in order, on the block state as read at each call's start
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Draw(TCTCache vars, Block block, Scratch s)
    {
        var (lods, fluids) = (OwnQuads.Lods(vars.tct), FluidsExt(vars.tct));
        if (!NotNull(lods) || !NotNull(fluids)) return; // Eligible and Pools found both
        var at = new JsonEmit.State(vars, block, s.Json, fluids[vars.extIndex3d + block.IceCheckOffset * TessSeams.Plane]);
        for (var i = 0; i < Math.Min(s.Count, MaxPlan); i++)
        {
            var plan = s.Steps[i];
            if (lods[plan.Lod] is { } passes) JsonEmit.Mesh(vars, plan.Copy, plan.Mesh, passes, ref at);
        }
    }

    private readonly record struct Kind(Block? Owner, bool IsPlain, bool Writes);

    private readonly record struct Planned(MeshData Mesh, JsonMesh Copy, int Lod);

    private sealed class Scratch
    {
        public readonly int[] Json = new int[JsonMesh.Lights];
        public readonly BlockPos At = new(0), Under = new(0);
        public readonly Dictionary<MeshData, JsonMesh> Meshes = new(ReferenceEqualityComparer.Instance);
        public readonly Planned[] Steps = new Planned[MaxPlan];
        public Kind[] Kinds = [];
        public JsonMesh? Last; // the table Table handed out last
        public int Count;
        public bool Writes; // the block's override is one of FlagWriters'
        public ShapeTesselatorManager? World; // whose meshes the tables are of
    }
}
