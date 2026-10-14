using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Replaces TopsoilTesselator: soil with grass, forest floor, clay, peat: most of the surface, 7-8.5 % of tessellation
// in world-join traces (cube calls plus eight packed overlay coordinates per face). Its rules: oaatHashU alternates, no
// tiles; snow above swaps the overlay for "snowed" and clears colour maps; cell positions (no random offset or y
// adjust), main v unscaled, up overlay turned by MurmurHash3 mod 4. Passes without shorts and faults go to the engine.
internal sealed class OwnTopsoil(IBlockTesselator engine) : OwnSlot(engine)
{
    private const string Snowed = "snowed";
    private const int Turns = 4, MaxBlocks = 1 << 20;

    // Each type's "snowed" texture by BlockId, with its block: no string lookup per snowed block (thread's own)
    private Snow[] _snowed = [];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override bool Draw(TCTCache vars, Block block)
    {
        OwnQuads.Chosen chosen = default;
        var faces = vars.drawFaceFlags & OwnQuads.AllFaces;
        TextureAtlasPosition? overlay = null;
        var colorMap = 0;
        _ = NotNull(block); // OwnSlot's
        if (faces == 0 || !OwnQuads.Ready(vars, faces, out var rgbs) || !Second(vars, block, ref overlay, ref colorMap) ||
            !NotNull(overlay) || !Choose(vars, block, faces, ref chosen)) return false;
        Draw(vars, block, faces, rgbs, ref chosen, new Overlay(overlay), colorMap);
        return true;
    }

    // Overlay position, colour map: face 6 and the block's, or under snow "snowed" and 0; false where the engine faults
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool Second(TCTCache vars, Block block, ref TextureAtlasPosition? overlay, ref int colorMap)
    {
        var (fast, blocks, positions) = (vars.fastBlockTextureSubidsByFace, BlocksExt(vars.tct),
            vars.textureAtlasPositionsByTextureSubId);
        if (fast is not { Length: > Faces } || !NotNull(blocks) || !Assert(blocks.Length == ExtCells) ||
            !NotNull(positions) || !Index(vars.extIndex3d + TessSeams.Plane, blocks.Length) ||
            blocks[vars.extIndex3d + TessSeams.Plane] is not { } above) return false;
        var (id, value) = (fast[Faces], vars.ColorMapData.Value);
        if (above.BlockMaterial == EnumBlockMaterial.Snow || above.snowLevel > 0f)
        {
            var snow = Snowy(block);
            if (snow.Faults) return false;
            if (snow.Has) (id, value) = (snow.Id, 0);
        }

        if ((uint)id >= (uint)positions.Length || positions[id] is not { } found) return false;
        (overlay, colorMap) = (found, value);
        return true;
    }

    // The block's "snowed" texture: none, its id, or a fault where the engine faults (no textures, or not baked)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private Snow Snowy(Block block)
    {
        var id = block.BlockId;
        _ = NotNull(block) && Assert(_snowed.Length <= MaxBlocks);
        if ((uint)id < (uint)_snowed.Length && ReferenceEquals(_snowed[id].Owner, block)) return _snowed[id];
        Snow found;
        if (block.Textures is not { } textures) found = new Snow(block, true, false, 0);
        else if (!textures.TryGetValue(Snowed, out var snowed)) found = new Snow(block, false, false, 0);
        else found = snowed?.Baked is { } baked ? new Snow(block, false, true, baked.TextureSubId) : new Snow(block, true, false, 0);
        if (!Index(id, MaxBlocks)) return found;
        if (id >= _snowed.Length) Array.Resize(ref _snowed, Math.Min(Math.Max(id + 1, 2 * _snowed.Length), MaxBlocks));
        _snowed[id] = found;
        return found;
    }

    // Each drawn face's atlas position and pool mesh (needs the overlay's shorts); false, nothing written: engine
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Choose(TCTCache vars, Block block, int faces, ref OwnQuads.Chosen chosen)
    {
        var (fast, positions) = (vars.fastBlockTextureSubidsByFace, vars.textureAtlasPositionsByTextureSubId);
        var pools = OwnQuads.Pools(vars.tct, vars.RenderPass);
        var variants = block.HasAlternates ? block.FastTextureVariants : null;
        if (!NotNull(pools) || !NotNull(fast) || !NotNull(positions) || (block.HasAlternates && variants is null)) return false;
        var hash = variants is null ? 0u : GameMath.oaatHashU(vars.posX, vars.posY, vars.posZ);
        for (var i = 0; i < Faces; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            var id = fast[i];
            if (variants is not null)
            {
                if (!Index(i, variants.Length)) return false; // the engine faults
                if (variants[i] is { } choices)
                {
                    if (choices.Length == 0 || choices[hash % (uint)choices.Length] is not { } baked) return false;
                    id = baked.TextureSubId;
                }
            }

            if (!OwnQuads.Take(positions, pools, id, out var position, out var mesh) || mesh.CustomShorts is null)
                return false;
            (chosen.Positions[i], chosen.Meshes[i]) = (position, mesh);
        }

        return true;
    }

    // TopsoilTesselator.DrawBlockFaceTopSoil for every drawn face, each lit just before it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Draw(TCTCache vars, Block block, int faces, int[] rgbs, ref OwnQuads.Chosen chosen,
        Overlay overlay, int colorMap)
    {
        var (quads, corners, e, flags) = (vars.blockFaceVertices, vars.CurrentLightRGBByCorner, vars.extIndex3d,
            vars.VertexFlags);
        _ = Assert(quads.Length >= Faces) && Assert(corners.Length >= OwnQuads.Corners); // Ready checked both
        var turn = GameMath.MurmurHash3Mod(vars.posX, vars.posY, vars.posZ, Turns);
        var (x, y, z) = ((float)vars.lx, (float)vars.ly, (float)vars.lz);
        var quad = default(OwnQuads.Quad);
        for (var i = 0; i < Faces; i++)
        {
            if ((faces & (1 << i)) == 0) continue;
            OwnQuads.Light(vars, block, i, e + Moves[i], rgbs, corners);
            var (position, mesh) = (chosen.Positions[i], chosen.Meshes[i]);
            if (!NotNull(position) || !NotNull(mesh)) continue; // Choose took every drawn face
            var q = quads[i];
            quad.Set(0, x + q[7].X, y + q[7].Y, z + q[7].Z, position.x2, position.y2);
            quad.Set(1, x + q[5].X, y + q[5].Y, z + q[5].Z, position.x2, position.y1);
            quad.Set(2, x + q[4].X, y + q[4].Y, z + q[4].Z, position.x1, position.y1);
            quad.Set(3, x + q[6].X, y + q[6].Y, z + q[6].Z, position.x1, position.y2);
            var first = mesh.VerticesCount;
            OwnQuads.Vertices(mesh, ref quad, corners, flags | BlockFacing.ALLFACES[i].NormalPackedFlags);
            overlay.Write(mesh.CustomShorts, i == Up ? turn : 0);
            OwnQuads.Ints(mesh, colorMap);
            OwnQuads.Indices(mesh, first);
            OwnQuads.Bounds(vars, x, y, z);
            OwnQuads.Bounds(vars, x + 1f, y + 1f, z + 1f);
        }
    }

    private readonly record struct Snow(Block? Owner, bool Faults, bool Has, int Id);

    // Overlay corners as AddPackedUV packs them (left half of the second texture; far u/v add 1 to the short). Vertex k
    // takes far u where k < 2 on even turns (k >= 2 odd), far v where k is 0 or 3 on turns 0-1 (1 or 2 on turns 2-3).
    private readonly struct Overlay
    {
        private readonly float _u1, _u2, _v1, _v2;
        private readonly short _packedU1, _packedU2, _packedV1, _packedV2;

        public Overlay(TextureAtlasPosition position)
        {
            _ = NotNull(position) && Finite(position.x1) && Finite(position.x2);
            (_u1, _u2, _v1, _v2) = (position.x1, position.x1 + (position.x2 - position.x1) / 2f, position.y1, position.y2);
            (_packedU1, _packedU2, _packedV1, _packedV2) = (Pack(_u1, 0), Pack(_u2, 1), Pack(_v1, 0), Pack(_v2, 1));
        }

        private static short Pack(float value, int far) => unchecked((short)(ushort)((int)(value * 32768f + 0.5f) + far));

        // Pairs for turn 0-3 (others add none): at once where they fit, else the engine's growing calls
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(CustomMeshDataPartShort shorts, int turn)
        {
            if (!Index(turn, Turns) || !NotNull(shorts)) return; // a hash modulo 4; Choose took the shorts
            var (values, count) = (shorts.Values, shorts.Count);
            if (values is null || count < 0 || count + 2 * OwnQuads.Corners > values.Length)
            {
                for (var k = 0; k < OwnQuads.Corners; k++)
                {
                    var (u, v) = ((k < 2) == (turn % 2 == 0), (k is 0 or 3) == (turn < 2));
                    shorts.AddPackedUV(u ? _u2 : _u1, v ? _v2 : _v1, u, v);
                }

                return;
            }

            var pairs = values.AsSpan(count, 2 * OwnQuads.Corners);
            for (var k = 0; k < OwnQuads.Corners; k++)
                (pairs[2 * k], pairs[2 * k + 1]) = ((k < 2) == (turn % 2 == 0) ? _packedU2 : _packedU1,
                    (k is 0 or 3) == (turn < 2) ? _packedV2 : _packedV1);
            shorts.Count = count + 2 * OwnQuads.Corners;
        }
    }
}
