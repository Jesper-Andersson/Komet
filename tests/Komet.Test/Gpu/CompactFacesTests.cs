using System.Runtime.InteropServices;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class CompactFacesTests
{
    // Grid geometry comes back bit for bit, the corner always (also thousands of blocks from the pool's origin); a rotated quad's
    // edges within the half floats' steps; the rest untouched
    [Test]
    public void RoundTrip()
    {
        float[] grid = [4931.0625f, -0.5f, 12.25f, 0, 0.5f, 0, -0.03125f, 0, 0, 0, 0, 0, 0, 0.5f, 0.25f, 0];
        float[] turned = [-4807.1234567f, 40.987654f, -3.3333333f, 0, 0.35355338f, -0.1234567f, 0.70710677f, 0, 0, 0, 0, 0, -1.4142135f, 0.0f, 2.2222222f, 0];
        int[] words = [0, 0, 0, 0x12345678, 0, 0, 0, unchecked((int)0x80004000), 1, -2, 0x0F0F0F0F, int.MinValue, 0, 0, 0, 0x7FEEDDCC];
        var engine = new uint[32];
        for (var i = 0; i < 16; i++)
        {
            engine[i] = words[i] != 0 ? (uint)words[i] : BitConverter.SingleToUInt32Bits(grid[i]);
            engine[16 + i] = words[i] != 0 ? (uint)words[i] : BitConverter.SingleToUInt32Bits(turned[i]);
        }

        var packed = new byte[2 * CompactFaces.Packed];
        var back = new uint[32];
        CompactFaces.Pack(MemoryMarshal.AsBytes(engine.AsSpan()), packed);
        CompactFaces.Unpack(packed, MemoryMarshal.AsBytes(back.AsSpan()));
        Assert.That(back.AsSpan(0, 16).ToArray(), Is.EqualTo(engine.AsSpan(0, 16).ToArray()));
        for (var i = 0; i < 16; i++)
            if (words[i] != 0) Assert.That(back[16 + i], Is.EqualTo(engine[16 + i]), $"word {i}");
            else
                Assert.That(BitConverter.UInt32BitsToSingle(back[16 + i]), Is.EqualTo(turned[i]).Within(i < 3 ? 0f : 2e-3f), $"float {i}");
        Assert.That(CompactFaces.Patch("x layout(binding = 3, std430) readonly buffer faceDataBuf  { FaceData faces[]; }; y = faces[gl_VertexID / 4];"),
            Does.Contain("kometFace(gl_VertexID / 4)").And.Not.Contain("FaceData faces[]"));
    }
}
