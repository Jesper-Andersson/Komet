using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// JSON slot (8): JsonMesher draws plain blocks, the replaced JsonTesselator the rest. The public jsonTesselator field
// stays the engine's, so AddDecor and the bleeding tesselator still reach it.
internal sealed class OwnJson(IBlockTesselator engine) : IBlockTesselator
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Tesselate(TCTCache vars)
    {
        _ = Assert(engine is not OwnJson);
        var ours = NotNull(vars) && vars.block is { } block && JsonMesher.Tesselate(vars, block, EnumDrawType.JSON);
        OwnTessellation.Count(ours, true);
        if (!ours) engine.Tesselate(vars);
    }
}

// JSONAndSnowLayer slot (17): where the block below allows snow, a 1/8-high opaque cube with the snow texture
// (fastBlockTextureSubidsByFace[6]) at the cell's x/z, flags without wind and z offset, colour map 0; then the block as
// JSON (JsonMesher, else the inner JsonTesselator). Snow faces draw as DrawBlockFace (OwnQuads); while either path
// stood down, or the snow lacks anything, the whole block goes to the engine.
internal sealed class OwnJsonSnow(JsonAndSnowLayerTesselator engine) : IBlockTesselator
{
    private const float Height = 0.125f;
    private const int SnowFlags = 0x1FFFFFF & ~0x700; // the engine's & 0x1FFFFFF & -1793: no wind, no z offset

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "json")]
    private static extern ref IBlockTesselator? Inner(JsonAndSnowLayerTesselator tesselator);

    // JsonAndSnowLayerTesselator's inner JSON tesselator field: present, of its 1.22.7 type
    internal static bool Fields(ILogger? logger)
    {
        var field = AccessTools.DeclaredField(typeof(JsonAndSnowLayerTesselator), "json");
        if (field?.FieldType == typeof(IBlockTesselator)) return Assert(!field.IsStatic);
        logger?.Warning("Komet OwnTessellation: JsonAndSnowLayerTesselator.json is missing or of another type, the engine draws " +
                        "the JSON blocks with snow");
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Tesselate(TCTCache vars)
    {
        var block = NotNull(vars) ? vars.block : null;
        var faces = block is null ? 0 : vars.drawFaceFlags & OwnQuads.AllFaces;
        if (!OwnTessellation.Active || !OwnTessellation.JsonActive || block is null || Inner(engine) is not { } json ||
            !Snow(vars, faces, out var rgbs, out var below, out var game, out var snow))
        {
            OwnTessellation.Count(false);
            engine.Tesselate(vars);
            return;
        }

        var at = JsonMesher.Below(vars.dimension, vars.posX, vars.posY - 1, vars.posZ);
        if (below.AllowSnowCoverage(game, at)) Draw(vars, block, faces, rgbs, snow.Position, snow.Mesh);
        var ours = JsonMesher.Tesselate(vars, block, EnumDrawType.JSONAndSnowLayer);
        OwnTessellation.Count(ours, true);
        if (!ours) json.Tesselate(vars);
    }

    // Block below, its world, snow atlas position and Opaque lod 1 pool mesh; false, nothing written: engine
    private static bool Snow(TCTCache vars, int faces, [NotNullWhen(true)] out int[]? rgbs, [NotNullWhen(true)] out Block? below,
        [NotNullWhen(true)] out ClientMain? game, out Snowed snow)
    {
        (below, game, snow) = (null, Game(vars.tct), default);
        if (!OwnQuads.Ready(vars, faces, out rgbs) || BlocksExt(vars.tct) is not { Length: ExtCells } blocks ||
            game is null || vars.fastBlockTextureSubidsByFace is not { Length: > Faces } fast ||
            vars.textureAtlasPositionsByTextureSubId is not { } positions ||
            OwnQuads.Pools(vars.tct, EnumChunkRenderPass.Opaque) is not { } pools ||
            !OwnQuads.Take(positions, pools, fast[Faces], out var position, out var mesh)) return false;
        var under = vars.extIndex3d + Moves[Down];
        if (!Index(under, ExtCells)) return false; // Ready: a cell of the chunk
        (below, snow) = (blocks[under], new Snowed(position, mesh));
        return NotNull(below);
    }

    // The snow cube's drawn faces, each lit just before it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Draw(TCTCache vars, Block block, int faces, int[] rgbs, TextureAtlasPosition position, MeshData mesh)
    {
        var (quads, corners, e) = (vars.blockFaceVertices, vars.CurrentLightRGBByCorner, vars.extIndex3d);
        var (flags, x, y, z) = (vars.VertexFlags & SnowFlags, (float)vars.lx, vars.finalY, (float)vars.lz);
        _ = Assert(quads.Length >= Faces) && Assert(corners.Length >= OwnQuads.Corners); // Ready checked both
        for (var i = 0; i < Faces; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            OwnQuads.Light(vars, block, i, e + Moves[i], rgbs, corners);
            OwnQuads.Face(vars, i, quads[i], position, mesh, flags | BlockFacing.ALLFACES[i].NormalPackedFlags, 0, Height,
                x, y, z);
        }
    }

    private readonly record struct Snowed(TextureAtlasPosition Position, MeshData Mesh);
}
