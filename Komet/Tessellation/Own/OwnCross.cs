using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Replaces CrossTesselator (slot 11): grass and cross plants, the most common engine-drawn blocks after JSON (7.3 M of
// 73 M, debug bench 2026-10-08). Two diagonal quads with face 0's or 2's texture (MurmurHash3 alternates), the cell's
// light, flags without wind at the foot and with it at the top, up normal, colour map in CustomInts, pass pool at lod 1
// (lod 2 if not drawn there); random rotation via TesselationMetaData's matrices and Mat4f.MulWithVec3_Position; draw
// face flags set to 3. Faults go to the engine unwritten.
internal sealed class OwnCross(IBlockTesselator engine) : OwnSlot(engine)
{
    private const float Height = 1.41f;
    private const int Quads = 2, NoWind = -503316481, CrossFaces = 3;

    // The corners MulWithVec3_Position writes, this tesselator's own (one per ChunkTesselator, so per thread)
    private readonly Vec3f _start = new(), _end = new();

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override bool Draw(TCTCache vars, Block block)
    {
        OwnQuads.Chosen chosen = default;
        _ = NotNull(block); // OwnSlot's
        if (!Choose(vars, block, out var hash, out var color, ref chosen)) return false;
        vars.drawFaceFlags = CrossFaces;
        Draw(vars, block, hash, color, ref chosen);
        return true;
    }

    // Both quads' atlas positions and pool meshes, the position hash, the cell's light; false, nothing written: engine
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Choose(TCTCache vars, Block block, out int hash, out int color, ref OwnQuads.Chosen chosen)
    {
        (hash, color) = (0, 0);
        var (fast, positions, tct) = (vars.fastBlockTextureSubidsByFace, vars.textureAtlasPositionsByTextureSubId, vars.tct);
        if (block.VertexFlags is null || fast is null || positions is null || tct is null ||
            RgbsExt(tct) is not { } light || !Index(vars.extIndex3d, light.Length) ||
            OwnQuads.Pools(tct, block.RenderPass, block.DoNotRenderAtLod2 ? 2 : 1) is not { } pools) return false;
        var variants = block.HasAlternates ? block.FastTextureVariants : null;
        if (block.HasAlternates && variants is null) return false;
        if (block.HasAlternates || block.RandomizeRotations)
            hash = GameMath.MurmurHash3(vars.posX, block.RandomizeAxes == EnumRandomizeAxes.XYZ ? vars.posY : 0, vars.posZ);
        for (var q = 0; q < Quads; q++)
        {
            var id = OwnQuads.Texture(variants, fast, 2 * q, hash, false);
            if (id == OwnQuads.Unknown || !OwnQuads.Take(positions, pools, id, out var position, out var mesh)) return false;
            (chosen.Positions[q], chosen.Meshes[q]) = (position, mesh);
        }

        color = light[vars.extIndex3d];
        return true;
    }

    // DrawCross's two quads, each at the cell or between its turned corners
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Draw(TCTCache vars, Block block, int hash, int color, ref OwnQuads.Chosen chosen)
    {
        var up = BlockFacing.UP.NormalPackedFlags;
        var (all, value) = (block.VertexFlags.All, vars.ColorMapData.Value);
        var (foot, top) = ((all & NoWind) | up, all | up);
        var matrix = block.RandomizeRotations
            ? TesselationMetaData.randomRotMatrices[GameMath.Mod(hash, TesselationMetaData.randomRotations.Length)]
            : null;
        var quad = default(OwnQuads.Quad);
        for (var q = 0; q < Quads; q++)
        {
            var (position, mesh) = (chosen.Positions[q], chosen.Meshes[q]);
            if (!NotNull(position) || !NotNull(mesh)) continue; // Choose took both
            var (x0, y0, z0, x1, y1, z1) = Corners(vars, matrix, q);
            quad.Set(0, x1, y0, z1, position.x2, position.y2);
            quad.Set(1, x1, y1, z1, position.x2, position.y1);
            quad.Set(2, x0, y1, z0, position.x1, position.y1);
            quad.Set(3, x0, y0, z0, position.x1, position.y2);
            var first = mesh.VerticesCount;
            OwnQuads.Uniform(mesh, ref quad, color, foot, top);
            OwnQuads.Bounds(vars, x0, y0, z0);
            OwnQuads.Bounds(vars, x1, y1, z1);
            OwnQuads.Ints(mesh, value);
            OwnQuads.Indices(mesh, first);
        }
    }

    // The quad's foot corner (x0, y0, z0) and far top corner (x1, y1, z1), with the engine's float operations
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (float, float, float, float, float, float) Corners(TCTCache vars, float[]? matrix, int q)
    {
        var (x, y, z) = (vars.finalX, vars.finalY, vars.finalZ);
        _ = Assert(q is >= 0 and < Quads);
        if (matrix is null) return (x + q, y, z, x + (1f - q), y + Height, z + 1f);
        Mat4f.MulWithVec3_Position(matrix, q, 0f, 0f, _start);
        Mat4f.MulWithVec3_Position(matrix, 1f - q, Height, 1f, _end);
        return (x + _start.X, y + _start.Y, z + _start.Z, _end.X + x, _end.Y + y, _end.Z + z);
    }

    // The engine's CrossTesselator in the slot and the type it has in 1.22.7
    internal static bool Replaces([NotNullWhen(true)] IBlockTesselator? tesselator) =>
        tesselator is CrossTesselator && tesselator.GetType() == typeof(CrossTesselator);
}
