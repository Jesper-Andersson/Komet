using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Komet.Vulkan;

// Vulkan-only: pool face data in 52 bytes a quad instead of 64 (FaceData). Corner stays float (pools reach 5000 blocks
// from their origin, MeshDataPool.TryAdd); edges become halves (exact on the 1/16 grid up to 64 blocks, else 1/2048
// relative); uv, uvSize, flags and colormapData unchanged, so shaders decode the same.
//
// u0..u2 xyz u3 half xyzA.x | half xyzA.y << 16  u4 half xyzA.z | half xyzB.x << 16  u5 half xyzB.y | half xyzB.z << 16
// u6 uv  u7 uvSize  u8..u11 flags  u12 colormapData
//
// Packed in the staging copy (one pass); unpacked by the vertex shaders (Glsl) and for OpenGL's pools (Unpack).
internal static class CompactFaces
{
    public const int Engine = 64, Packed = 52, EngineUints = Engine / 4, PackedUints = Packed / 4;

    private const int MaxQuads = 1 << 24; // a pool of 2M vertices holds 512k quads

    public static bool Enabled { get; set; } = true;

    // Set for the running Vulkan-only renderer's life (TerrainPools.Compact, packed ports): the knob applies next
    // start.
    public static bool Active { get; set; }

    // Programs reading pool face data (decals.vsh declares the same block for its own, never packed buffers)
    private static readonly string[] Programs = ["chunkopaque", "chunktopsoil", "chunktransparent", "chunkshadowmap"];

    public static bool Reads(string program) => Assert(Programs.Length == 4) && Array.IndexOf(Programs, program) >= 0;

    // Whole quads only
    public static bool Aligned(long offset, long size) => Assert(offset >= 0 && size >= 0) && offset % Engine == 0 && size % Engine == 0;

    public static long PackedOf(long engineBytes) => Assert(engineBytes >= 0) ? engineBytes / Engine * Packed : 0;

    public static long EngineOf(long packedBytes) => Assert(packedBytes >= 0) ? packedBytes / Packed * Engine : 0;

    // Inside the pool upload: unchecked pointer loop, about the cost of reading the bytes once
    public static unsafe void Pack(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var quads = source.Length / Engine;
        if (!Assert(source.Length % Engine == 0) || !Assert(target.Length >= quads * Packed) || !Assert(quads <= MaxQuads)) return;
        fixed (byte* s = source, t = target)
        {
            uint* f = (uint*)s, p = (uint*)t;
            for (var q = 0; q < Math.Min(quads, MaxQuads); q++, f += EngineUints, p += PackedUints)
            {
                // xyzA uvSize and xyzB colormapData: each vec3 one vector, fourth lane unused
                var (a, b) = (Halves(Vector128.Load(f + 4)), Halves(Vector128.Load(f + 12)));
                (p[0], p[1], p[2]) = (f[0], f[1], f[2]);
                p[3] = a.GetElement(0) | (a.GetElement(1) << 16);
                p[4] = a.GetElement(2) | (b.GetElement(0) << 16);
                p[5] = b.GetElement(1) | (b.GetElement(2) << 16);
                (p[6], p[7], p[8], p[9], p[10], p[11], p[12]) = (f[3], f[7], f[8], f[9], f[10], f[11], f[15]);
            }
        }
    }

    public static void Unpack(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var quads = source.Length / Packed;
        if (!Assert(source.Length % Packed == 0) || !Assert(target.Length >= quads * Engine)) return;
        var from = MemoryMarshal.Cast<byte, uint>(source[..(quads * Packed)]);
        var to = MemoryMarshal.Cast<byte, uint>(target[..(quads * Engine)]);
        for (var q = 0; q < Math.Min(quads, MaxQuads); q++) UnpackQuad(from.Slice(q * PackedUints, PackedUints), to.Slice(q * EngineUints, EngineUints));
    }

    private static void UnpackQuad(ReadOnlySpan<uint> p, Span<uint> f)
    {
        _ = Assert(p.Length == PackedUints) && Assert(f.Length == EngineUints);
        (f[0], f[1], f[2]) = (p[0], p[1], p[2]);
        (f[4], f[5], f[6]) = (Float(p[3]), Float(p[3] >> 16), Float(p[4]));
        (f[12], f[13], f[14]) = (Float(p[4] >> 16), Float(p[5]), Float(p[5] >> 16));
        (f[3], f[7], f[8], f[9], f[10], f[11], f[15]) = (p[6], p[7], p[8], p[9], p[10], p[11], p[12]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Halves(Vector128<uint> bits)
    {
        const uint infinity = 255u << 23, halfMax = (127u + 16) << 23, denormal = ((127u - 15) + (23 - 10) + 1) << 23;
        var sign = bits & Vector128.Create(0x80000000u);
        var a = bits ^ sign;
        var small = (a.AsSingle() + Vector128.Create(denormal).AsSingle()).AsUInt32() - Vector128.Create(denormal);
        var normal = Vector128.ShiftRightLogical(a + Vector128.Create(unchecked((uint)((15 - 127) << 23)) + 0xFFF) +
                                                 (Vector128.ShiftRightLogical(a, 13) & Vector128<uint>.One), 13);
        var below = Vector128.ShiftRightArithmetic((a - Vector128.Create(113u << 23)).AsInt32(), 31).AsUInt32();
        var half = Vector128.ConditionalSelect(below, small, normal);
        var over = Vector128.GreaterThanOrEqual(a, Vector128.Create(halfMax));
        var special = Vector128.ConditionalSelect(Vector128.GreaterThan(a, Vector128.Create(infinity)), Vector128.Create(0x7E00u),
            Vector128.Create(0x7C00u));
        return Vector128.ConditionalSelect(over, special, half) | Vector128.ShiftRightLogical(sign, 16);
    }

    private static uint Float(uint half) => !Assert((half >> 16) <= 0xFFFF) ? 0 : BitConverter.SingleToUInt32Bits((float)BitConverter.UInt16BitsToHalf((ushort)(half & 0xFFFF)));

    // FaceData read from the packed words; the engine's struct and code unchanged
    public const string Glsl = """
        layout(binding = 3, std430) readonly buffer faceDataBuf { uint kometFaces[]; };
        FaceData kometFace(int quad) {
            int b = quad * 13;
            FaceData f;
            f.xyz = uintBitsToFloat(uvec3(kometFaces[b], kometFaces[b + 1], kometFaces[b + 2]));
            vec2 a = unpackHalf2x16(kometFaces[b + 3]), c = unpackHalf2x16(kometFaces[b + 4]), e = unpackHalf2x16(kometFaces[b + 5]);
            f.xyzA = vec3(a.x, a.y, c.x);
            f.xyzB = vec3(c.y, e.x, e.y);
            f.uv = int(kometFaces[b + 6]);
            f.uvSize = int(kometFaces[b + 7]);
            f.flags = ivec4(int(kometFaces[b + 8]), int(kometFaces[b + 9]), int(kometFaces[b + 10]), int(kometFaces[b + 11]));
            f.colormapData = int(kometFaces[b + 12]);
            return f;
        }
        """;

    private const string Declaration = "layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; };";
    private const string Read = "faces[gl_VertexID / 4]";

    // null without the engine's declaration and read
    public static string? Patch(string source)
    {
        if (!NotNull(source) || !Assert(Declaration.Length > 0) || !Assert(Read.Length > 0)) return null;
        var at = source.IndexOf(Declaration, StringComparison.Ordinal);
        if (at < 0 || !source.Contains(Read, StringComparison.Ordinal)) return null;
        _ = Assert(at + Declaration.Length <= source.Length) && Assert(Glsl.Length > Declaration.Length);
        var patched = source[..at] + Glsl + source[(at + Declaration.Length)..];
        return Assert(patched.Length > source.Length) ? patched.Replace(Read, "kometFace(gl_VertexID / 4)", StringComparison.Ordinal) : null;
    }
}
