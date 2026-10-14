using Komet.Gpu;
using Komet.Rendering;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The camera's draws sorted near to far: the same commands as rows.comp wrote, each draw's in the order of their boxes' distance,
// and only where the order changes nothing but ties
[NonParallelizable]
public sealed class SortedDrawsGpuTests
{
    private const string Culling = "Komet.Rendering.OcclusionCulling, Komet";
    private static readonly bool[] Expected = [true, false, false, false, true, false];

    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void TheSortedCommandsAreTheWrittenOnesNearestFirst(string backend) => ShadowCastersGpuTests.Run(backend, 160, world =>
    {
        var view = new TerrainView(0.3, -0.15, [0.4, 0.8, 0.3]);
        OcclusionCulling.SortAlways = true;
        try
        {
            Assert.That(OcclusionCulling.RunRows(world.Pools, ShadowCastersGpuTests.Culler(view), view.Camera, view.CameraMvp),
                Is.True);
        }
        finally
        {
            OcclusionCulling.SortAlways = false;
        }

        var gpu = CullingGpu(null);
        var counters = new uint[8 + OcclusionCulling.MaxDraws];
        var buffers = CounterBuffers(null);
        gpu.Read(buffers[(FrameCount(null) - 1) % buffers.Length], 0, counters);
        var (region, reordered) = (0, 0);
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var listed = (List<ModelDataPoolLocation>)AccessTools.Field(typeof(MeshDataPool), "poolLocations")
                .GetValue(world.Pools[p])!;
            var count = (int)counters[8 + p];
            var (written, sorted) = (new uint[5 * Math.Max(count, 1)], new uint[5 * Math.Max(count, 1)]);
            gpu.Read(CommandBuffer(null), 5 * region, written);
            gpu.Read(TerrainWorld.SortedBuffer(null), 5 * region, sorted);
            var buckets = Enumerable.Range(0, count)
                .Select(c => Bucket(listed.First(l => l.IndicesStart == (int)sorted[5 * c + 2]).FrustumCullSphere, view.Camera))
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(Commands(sorted, count), Is.EquivalentTo(Commands(written, count)), $"pool {p}: the same commands");
                Assert.That(buckets.Zip(buckets.Skip(1)).All(b => b.Second >= b.First - 1), Is.True,
                    $"pool {p}: nearest first: {string.Join(' ', buckets)}");
            });
            if (!Commands(sorted, count).SequenceEqual(Commands(written, count))) reordered++;
            region += listed.Count;
        }

        Assert.That(reordered, Is.Positive, "some draw's order changed");
    });

    // Sorted only while OpenGL tests and writes depth with LESS or LEQUAL and blends into no draw buffer
    [Test]
    public void OnlyAnOrderlessStateIsSorted()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        Assert.That(OcclusionCulling.Orderless(), Is.False, "not tapped: OpenGL's state unknown");
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            var states = new List<bool> { OcclusionCulling.Orderless() };
            GL.Enable(IndexedEnableCap.Blend, 3);
            states.Add(OcclusionCulling.Orderless());
            GL.Disable(EnableCap.Blend);
            GL.DepthMask(false);
            states.Add(OcclusionCulling.Orderless());
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Always);
            states.Add(OcclusionCulling.Orderless());
            GL.DepthFunc(DepthFunction.Less);
            states.Add(OcclusionCulling.Orderless());
            GL.Disable(EnableCap.DepthTest);
            states.Add(OcclusionCulling.Orderless());
            Assert.That(states, Is.EqualTo(Expected),
                "LEQUAL, a blending buffer, no depth writes, ALWAYS, LESS, no depth test");
        }
        finally
        {
            GlTap.Untap();
        }
    }

    private static List<(uint, uint)> Commands(uint[] commands, int count) =>
        [.. Enumerable.Range(0, count).Select(c => (commands[5 * c], commands[5 * c + 2]))];

    // rows.comp's bucket, in doubles
    private static int Bucket(Sphere sphere, Vec3d camera)
    {
        var e = sphere.radius / 1.7320508f;
        double[] d = [Math.Abs(sphere.x - camera.X) - e, Math.Abs(sphere.y - camera.Y) - e, Math.Abs(sphere.z - camera.Z) - e];
        var distance = Math.Sqrt(d.Sum(v => Math.Max(v, 0) * Math.Max(v, 0)));
        return Math.Min((int)(Math.Sqrt(distance) * 2.8), 63);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_Gpu")]
    private static extern IGpuBackend CullingGpu([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CounterBuffers")]
    private static extern ref int[] CounterBuffers([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_commands")]
    private static extern ref int CommandBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_frame")]
    private static extern ref int FrameCount([UnsafeAccessorType(Culling)] object? culling);
}
