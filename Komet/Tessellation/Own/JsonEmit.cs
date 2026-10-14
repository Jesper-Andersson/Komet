using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// AddJsonModelDataToMesh without transform, decor or hooks, for JsonMesh-tabled meshes, straight into the pool arrays.
// Per face, engine order: boundary cull, pass/atlas pool, ColorMapData.FromValues, light by facing (own light, else
// BiLerpRgbaColor of the corners, or the corner if all four are equal: the lerp returns it), v offset. Per vertex:
// capacity check with engine growth, position (DisableRandoms rounded), UpdateChunkMinMax, uv, colour bytes (scale 1f,
// JSONAndWater never here), custom data, flags with fluid wind. CustomInts and indices go 4/6 at once where they fit.
internal static class JsonEmit
{
    private const int Calm = 0, Walled = 1, Waves = 2; // fluid at the ice check offset: none, solid-sided, other
    private const int WaveSet = 0x2C000000, WaveMask = ~0x40000000, WeakWind = 1 << 25, LeavesWind = 3 << 25;
    private const int LiquidFlag = 268435456, Own = JsonMesh.Lights - 1, Corners = JsonMesh.Corners;
    private const float AtlasRows = 32f;

    // ClientSettings lower-cases its key per read (a string per block); the atlas height holds for the session
    private static int AtlasHeight
    {
        get
        {
            if (field <= 0) field = ClientSettings.MaxTextureAtlasHeight; // unset (0) asks again, as every read did
            return field;
        }
    }

    // The faces of one source mesh at one lod; Pools checked every pool they draw into
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Mesh(TCTCache vars, JsonMesh table, MeshData source, MeshData[][] passes, ref State at)
    {
        var faces = table.Faces;
        _ = Assert(faces.Length == source.XyzFacesCount) && Assert(at.Lit.Length == JsonMesh.Lights);
        for (var l = 0; l < Math.Min(faces.Length, JsonMesh.MaxFaces); l++)
        {
            ref readonly var face = ref faces[l];
            if (face.Boundary && (at.DrawFaces & (1 << face.Facing)) == 0) continue;
            var pool = passes[face.Pass >= 0 ? face.Pass : at.Pass][face.Atlas];
            var value = ColorMapData.FromValues(face.Season, face.Climate, at.Temperature, at.Rainfall,
                face.Frost < 0 ? at.Frostable : face.Frost != 0, at.Extra);
            Face(vars, table, source, l, in face, pool, value, ref at);
        }
    }

    // Face l's vertices, custom data, indices: at once where four fit, else one by one with engine growth
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Face(TCTCache vars, JsonMesh table, MeshData source, int l, in JsonMesh.Face face, MeshData pool,
        int value, ref State at)
    {
        if (!Index(l, table.Faces.Length) || !Assert(pool.VerticesCount >= 0)) return; // Mesh's face, checked pool
        var (x0, z0) = face.Fixed ? (at.FixedX, at.FixedZ) : (at.X, at.Z);
        var lt = Light(in face, at.Lit, out var rt, out var lb, out var rb, out var even);
        var shift = face.Facing >= 0 && at.VOffset != 0f && ((1 << face.Facing) & at.AltFaces) == 0 ? at.VScaled : 0f;
        var first = pool.VerticesCount;
        var corner = new Corner(x0, at.Y, z0, shift, lt, rt, lb, rb, even, at.VertexFlags, at.Fluid);
        if (first + Corners <= pool.VerticesMax) Fast(vars, table, source, l, pool, value, face.Custom, in corner);
        else Slow(vars, table, source, l, pool, value, face.Custom, in corner);
        if (face.Custom == JsonMesh.Ints) Ints(pool.CustomInts, value);
        Indices(pool, table.Relative, l, first);
    }

    // Four vertices as the arrays stand (Pools checked VerticesMax; only GrowVertexBuffer raises it), bounds in locals
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Fast(TCTCache vars, JsonMesh table, MeshData source, int l, MeshData pool, int value, int custom,
        in Corner c)
    {
        var n = pool.VerticesCount;
        _ = Assert(n >= 0) && Assert(n + Corners <= pool.VerticesMax); // Face's choice
        var sxyz = source.xyz.AsSpan(3 * Corners * l, 3 * Corners);
        var suv = source.Uv.AsSpan(2 * Corners * l, 2 * Corners);
        var sflags = source.Flags.AsSpan(Corners * l, Corners);
        var lerp = table.Lerp.AsSpan(2 * Corners * l, 2 * Corners);
        var xyz = pool.xyz.AsSpan(3 * n, 3 * Corners);
        var uv = pool.Uv.AsSpan(2 * n, 2 * Corners);
        var rgba = pool.Rgba.AsSpan(4 * n, 4 * Corners);
        var flags = pool.Flags.AsSpan(n, Corners);
        var (xMin, xMax, yMin, yMax, zMin, zMax) = (vars.xMin, vars.xMax, vars.yMin, vars.yMax, vars.zMin, vars.zMax);
        for (var k = 0; k < Corners; k++)
        {
            var (x, y, z) = (sxyz[3 * k] + c.X, sxyz[3 * k + 1] + c.Y, sxyz[3 * k + 2] + c.Z);
            (xyz[3 * k], xyz[3 * k + 1], xyz[3 * k + 2]) = (x, y, z);
            if (x < xMin) xMin = x;
            else if (x > xMax) xMax = x;
            if (y < yMin) yMin = y;
            else if (y > yMax) yMax = y;
            if (z < zMin) zMin = z;
            else if (z > zMax) zMax = z;
            (uv[2 * k], uv[2 * k + 1]) = (suv[2 * k], suv[2 * k + 1] + c.Shift);
            var color = c.Even ? c.Lt : GameMath.BiLerpRgbaColor(lerp[2 * k], lerp[2 * k + 1], c.Lt, c.Rt, c.Lb, c.Rb);
            if (custom != JsonMesh.Ints) Custom(pool, source, custom, Corners * l + k, value);
            BinaryPrimitives.WriteInt32LittleEndian(rgba.Slice(4 * k, 4), color);
            flags[k] = Wind(c.Flags | sflags[k], sflags[k], c.Fluid);
        }

        (vars.xMin, vars.xMax, vars.yMin, vars.yMax, vars.zMin, vars.zMax) = (xMin, xMax, yMin, yMax, zMin, zMax);
        pool.VerticesCount = n + Corners;
    }

    // Four vertices one by one, growing the arrays as the engine's per-vertex calls do
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Slow(TCTCache vars, JsonMesh table, MeshData source, int l, MeshData pool, int value, int custom,
        in Corner c)
    {
        var (sxyz, suv, sflags, lerp) = (source.xyz, source.Uv, source.Flags, table.Lerp);
        var (xyz, uv, rgba, flags) = (pool.xyz, pool.Uv, pool.Rgba, pool.Flags);
        var n = pool.VerticesCount;
        _ = Assert(n >= 0) && NotNull(source);
        for (var k = 0; k < Corners; k++)
        {
            if (n >= pool.VerticesMax)
            {
                pool.VerticesCount = n;
                pool.GrowVertexBuffer();
                pool.GrowNormalsBuffer();
                (xyz, uv, rgba, flags) = (pool.xyz, pool.Uv, pool.Rgba, pool.Flags);
            }

            var v = Corners * l + k;
            var (x, y, z) = (sxyz[3 * v] + c.X, sxyz[3 * v + 1] + c.Y, sxyz[3 * v + 2] + c.Z);
            (xyz[3 * n], xyz[3 * n + 1], xyz[3 * n + 2]) = (x, y, z);
            OwnQuads.Bounds(vars, x, y, z);
            (uv[2 * n], uv[2 * n + 1]) = (suv[2 * v], suv[2 * v + 1] + c.Shift);
            var color = c.Even ? c.Lt : GameMath.BiLerpRgbaColor(lerp[2 * v], lerp[2 * v + 1], c.Lt, c.Rt, c.Lb, c.Rb);
            if (custom != JsonMesh.Ints) Custom(pool, source, custom, v, value);
            BinaryPrimitives.WriteInt32LittleEndian(rgba.AsSpan(4 * n, 4), color);
            flags[n] = Wind(c.Flags | sflags[v], sflags[v], c.Fluid);
            n++;
        }

        pool.VerticesCount = n;
    }

    // Corner light, left top first: own light without facing; `even` where no vertex needs interpolating
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Light(in JsonMesh.Face face, int[] light, out int rt, out int lb, out int rb, out bool even)
    {
        if (face.Facing < 0)
        {
            var own = light[Own];
            (rt, lb, rb, even) = (own, own, own, true);
            return own;
        }

        var (b, order) = (Corners * face.Facing, JsonMesh.CornerOrder);
        _ = Index(face.Facing, TessSeams.Faces) && Assert(light.Length == JsonMesh.Lights);
        var lt = light[b + order[b]];
        (rt, lb, rb) = (light[b + order[b + 1]], light[b + order[b + 2]], light[b + order[b + 3]]);
        even = face.Even && lt == rt && lt == lb && lt == rb;
        return lt;
    }

    // Flags as AdjustWindWaveForFluids leaves them: solid-sided fluid clears wind bits, another makes weak/leaves waves
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Wind(int flags, int source, int wind)
    {
        if (wind == Calm) return flags;
        if (wind == Walled) return flags & VertexFlags.ClearWindModeBitsMask;
        var mode = source & VertexFlags.WindModeBitsMask;
        return mode is WeakWind or LeavesWind ? (flags | WaveSet) & WaveMask : flags;
    }

    // A Liquid or TopSoil face's per-vertex appends, in the engine's order
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Custom(MeshData pool, MeshData source, int custom, int v, int value)
    {
        var (ints, floats, shorts) = (pool.CustomInts, pool.CustomFloats, pool.CustomShorts);
        _ = Assert(v >= 0) && NotNull(ints);
        ints.Add(value);
        switch (custom)
        {
            case JsonMesh.LiquidZero:
                ints.Add(LiquidFlag);
                floats.Add(0f);
                floats.Add(0f);
                break;
            case JsonMesh.LiquidOwn:
                ints.Add(source.CustomInts.Values[v]);
                floats.Add(source.CustomFloats.Values[2 * v]);
                floats.Add(source.CustomFloats.Values[2 * v + 1]);
                break;
            default:
                shorts.Add(source.CustomShorts.Values[2 * v]);
                shorts.Add(source.CustomShorts.Values[2 * v + 1]);
                break;
        }
    }

    // Four CustomInts.Add(value)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ints(CustomMeshDataPartInt ints, int value)
    {
        if (!NotNull(ints)) return; // Pools checked the pool's
        var (values, count) = (ints.Values, ints.Count);
        if (values is null || count < 0 || count + Corners > values.Length)
        {
            for (var k = 0; k < Corners; k++) ints.Add(value);
            return;
        }

        values.AsSpan(count, Corners).Fill(value);
        ints.Count = count + Corners;
    }

    // AddIndices: source indices plus the pool's vertex count before the face minus the face's first source vertex
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Indices(MeshData pool, int[] relative, int l, int first)
    {
        _ = Index(l, relative.Length / JsonMesh.Indices) && Assert(first >= 0);
        var r = relative.AsSpan(JsonMesh.Indices * l, JsonMesh.Indices);
        var (count, indices) = (pool.IndicesCount, pool.Indices);
        if (count < 0 || count + JsonMesh.Indices > pool.IndicesMax || indices is null ||
            count + JsonMesh.Indices > indices.Length)
        {
            pool.AddIndices(false, first + r[0], first + r[1], first + r[2], first + r[3], first + r[4], first + r[5]);
            return;
        }

        var six = indices.AsSpan(count, JsonMesh.Indices);
        for (var k = 0; k < JsonMesh.Indices; k++) six[k] = first + r[k];
        pool.IndicesCount = count + JsonMesh.Indices;
    }

    // What every vertex of a face adds or takes: the offset, the texture's v shift, the corner light and the flags
    private readonly record struct Corner(float X, float Y, float Z, float Shift, int Lt, int Rt, int Lb, int Rb, bool Even,
        int Flags, int Fluid);

    // What AddJsonModelDataToMesh reads from the block's state at its start, the same for each mesh of the block
    internal readonly struct State
    {
        public readonly float X, Y, Z, FixedX, FixedZ, VOffset, VScaled;
        public readonly int VertexFlags, DrawFaces, Pass, AltFaces, Extra, Fluid;
        public readonly byte Temperature, Rainfall;
        public readonly bool Frostable;
        public readonly int[] Lit;

        public State(TCTCache vars, Block block, int[] light, Block fluid)
        {
            _ = NotNull(vars) && NotNull(block) && NotNull(fluid);
            (X, Y, Z) = (vars.finalX, vars.finalY, vars.finalZ);
            // the engine's (int)(finalX + 0.5f), as a float
            (FixedX, FixedZ) = ((int)(vars.finalX + 0.5f), (int)(vars.finalZ + 0.5f));
            (VertexFlags, DrawFaces, Pass, Lit) = (vars.VertexFlags, vars.drawFaceFlags, (int)vars.RenderPass, light);
            (Temperature, Rainfall) = (vars.ColorMapData.Temperature, vars.ColorMapData.Rainfall);
            (Frostable, Extra, AltFaces) = (block.Frostable, block.ExtraColorBits, block.alternatingVOffsetFaces);
            VOffset = vars.textureVOffset;
            VScaled = VOffset != 0f ? VOffset / (AtlasHeight / AtlasRows) : 0f;
            Fluid = Calm;
            if (fluid.BlockId != 0) Fluid = fluid.SideSolid.Any ? Walled : Waves;
            _ = Assert(Lit.Length == JsonMesh.Lights);
        }
    }
}
