using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// What Komet keeps by GL name goes with the name: a buffer given contents gets a mirror, a vertex array the tap's copy of its
// attributes. Rounds of making and deleting them - each round in one call of more names than GlTap's slice of 4096 - end where
// they began.
[NonParallelizable]
public sealed class HeldGpuTests
{
    private const int Churned = 5000, Rounds = 3;

    [Test]
    public void DeletedBuffersTakeTheirMirrorsAlong()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        Assert.That(GlTap.Tap(), Is.True, GlTap.Missing);
        try
        {
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            using var scene = SceneRenderer.Create(renderer!, null);
            Assert.That(scene, Is.Not.Null);
            GlTap.Writes = scene; // as VulkanRenderer wires it
            var (count, bytes) = (scene!.Mirrors.Count, scene.Mirrors.ManagedBytes);
            for (var round = 0; round < Rounds; round++)
            {
                var buffers = new int[Churned];
                GL.CreateBuffers(Churned, buffers);
                foreach (var buffer in buffers) GL.NamedBufferData(buffer, 256, IntPtr.Zero, BufferUsageHint.DynamicDraw);
                Assert.That(scene.Mirrors.Count, Is.EqualTo(count + Churned), "each mirrored from its first contents");
                Assert.That(scene.Mirrors.ManagedBytes, Is.GreaterThan(bytes));
                GL.DeleteBuffers(Churned, buffers);
                Assert.That(scene.Mirrors.Count, Is.EqualTo(count), $"round {round}: every mirror went with its buffer");
                Assert.That(scene.Mirrors.ManagedBytes, Is.EqualTo(bytes), $"round {round}");
            }

            Assert.That(VulkanRenderer.Held(), Does.Contain(" buffer mirrors ("), "the report names them");
        }
        finally
        {
            GlTap.Untap();
        }
    }

    [Test]
    public void DeletedVertexArraysLeaveTheTap()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        Assert.That(GlTap.Tap(), Is.True, GlTap.Missing);
        try
        {
            var (arrays, bytes) = (GlTap.Held.Arrays, GlTap.Held.Bytes);
            for (var round = 0; round < Rounds; round++)
            {
                // made in fifths (past a slice the tap asks on first sight), deleted at once
                var made = Enumerable.Range(0, 5).SelectMany(_ =>
                {
                    var fifth = new int[Churned / 5];
                    GL.GenVertexArrays(fifth.Length, fifth);
                    return fifth;
                }).ToArray();
                Assert.That(GlTap.Held.Arrays, Is.EqualTo(arrays + Churned), "each known from its making");
                GL.DeleteVertexArrays(Churned, made);
                Assert.That(GlTap.Held.Arrays, Is.EqualTo(arrays), $"round {round}: every one gone");
                Assert.That(GlTap.Held.Bytes, Is.EqualTo(bytes), $"round {round}");
            }
        }
        finally
        {
            GlTap.Untap();
        }
    }
}
