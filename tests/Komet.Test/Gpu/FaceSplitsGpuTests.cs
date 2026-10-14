using Komet.Gpu;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The GPU culling leaving out what faces away (FaceSorting, rows.comp) on TerrainWorld's sections sorted as the tessellator's
// faces are: whole directions and each direction's part past its split. Every face that faces the eye, of every row the
// culling draws, is still in the commands, each step leaves out more, and the picture with back faces culled (as RenderOpaque
// draws opaque and topsoil) is the same pixel for pixel.
[NonParallelizable]
public sealed class FaceSplitsGpuTests
{
    private const string Culling = "Komet.Rendering.OcclusionCulling, Komet";
    private const int Width = 1280, Height = 683, Runs = 4, StatUints = 8;

    private static readonly (double Yaw, double Pitch)[] Views = [(0.3, -0.15), (2.4, -0.5), (4.2, 0.1), (1.1, -1.2)];

    // Whole; directions and their splits left out
    internal static readonly bool[] Variants = [false, true];

    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    [Category("Slow")]
    public void EveryFaceSeenIsDrawn(string backend) => ShadowCastersGpuTests.Run(backend, 160, world =>
    {
        foreach (var (yaw, pitch) in Views)
        {
            var view = new TerrainView(yaw, pitch, [0.4, 0.8, 0.3]);
            var drawn = Variants.Select(v => Culled(world, view, v)).ToArray();
            var triangles = drawn.Select(d => d.Sum(pool => pool.Sum(c => c.Count)) / 3).ToArray();
            TestContext.Out.WriteLine($"yaw {yaw}, pitch {pitch}: {triangles[0]} triangles whole, {triangles[1]} with the " +
                                      "directions facing away and their parts past the splits left out");
            Assert.Multiple(() =>
            {
                Assert.That(Missing(world, view, drawn[0], drawn[1]), Is.Zero, $"yaw {yaw}: directions and splits");
                Assert.That(triangles[1], Is.LessThan(triangles[0] * 9 / 10), $"yaw {yaw}: directions left out");
            });
        }
    }, sorted: true);

    [Test]
    [Category("Slow")]
    public void ThePictureStaysTheSame() => ShadowCastersGpuTests.Run(GpuRig.Gl, 160, world =>
    {
        var program = TerrainWorld.Program(TerrainWorld.OpaqueFragment);
        using var target = new DepthTarget(Width, Height, 1);
        try
        {
            foreach (var (yaw, pitch) in Views)
            {
                var view = new TerrainView(yaw, pitch, [0.4, 0.8, 0.3]);
                _ = Drawn(world, view, Variants[0], (program, target)); // the context's first picture differs in a few pixels
                var pictures = Variants.Select(v => Drawn(world, view, v, (program, target))).ToArray();
                Assert.Multiple(() =>
                {
                    Assert.That(pictures[0].Depth.Count(d => d < 1), Is.GreaterThan(Width * Height / 4), $"yaw {yaw}: drawn");
                    for (var v = 1; v < Variants.Length; v++)
                    {
                        Assert.That(Differing(pictures[0].Depth, pictures[v].Depth), Is.Zero, $"yaw {yaw}, {v}: depth");
                        Assert.That(Differing(pictures[0].Color, pictures[v].Color), Is.Zero, $"yaw {yaw}, {v}: colour");
                    }
                });
            }
        }
        finally
        {
            GL.DeleteProgram(program);
        }
    }, sorted: true);

    // Each pool's commands (first index, index count) after one culled call
    internal static List<(uint First, uint Count)>[] Culled(TerrainWorld world, TerrainView view, bool backFaces)
    {
        Run(world, view, backFaces);
        var gpu = CullingGpu(null);
        var counters = new uint[StatUints + OcclusionCulling.MaxDraws];
        var buffers = CounterBuffers(null);
        gpu.Read(buffers[(FrameCount(null) - 1) % buffers.Length], 0, counters);
        var (region, factor) = (0, backFaces ? Runs : 1);
        var pools = new List<(uint, uint)>[world.Pools.Count];
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var count = (int)counters[StatUints + p];
            var commands = new uint[5 * Math.Max(count, 1)];
            gpu.Read(CommandBuffer(null), 5 * region, commands);
            pools[p] = [.. Enumerable.Range(0, count).Select(c => (commands[5 * c + 2], commands[5 * c]))];
            region += factor * Listed(world, p).Count;
        }

        return pools;
    }

    internal static void Run(TerrainWorld world, TerrainView view, bool backFaces) =>
        Assert.That(OcclusionCulling.RunRows(world.Pools, ShadowCastersGpuTests.Culler(view), view.Camera, view.CameraMvp,
            backFaces), Is.True);

    private static List<ModelDataPoolLocation> Listed(TerrainWorld world, int pool) =>
        (List<ModelDataPoolLocation>)AccessTools.Field(typeof(MeshDataPool), "poolLocations").GetValue(world.Pools[pool])!;

    // Faces facing the eye, of the locations drawn whole, the culled commands leave out
    private static int Missing(TerrainWorld world, TerrainView view, List<(uint First, uint Count)>[] whole,
        List<(uint First, uint Count)>[] culled)
    {
        var missing = 0;
        double[] eye = [view.Camera.X - TerrainWorld.Cx, view.Camera.Y, view.Camera.Z - TerrainWorld.Cz];
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var positions = world.Positions[p];
            var drawn = new bool[positions.Length / 12];
            foreach (var (first, count) in culled[p])
                for (var f = first / 6; f < (first + count) / 6; f++) drawn[f] = true;
            foreach (var location in Listed(world, p).Where(l => whole[p].Any(c => c.First == l.IndicesStart)))
                for (var f = location.IndicesStart / 6; f < location.IndicesEnd / 6; f++)
                    if (!drawn[f] && Facing(positions, f, eye))
                        missing++;
        }

        return missing;
    }

    // The quad's front side toward the eye
    private static bool Facing(float[] xyz, int face, double[] eye)
    {
        var v = 12 * face;
        double[] a = [xyz[v + 3] - xyz[v], xyz[v + 4] - xyz[v + 1], xyz[v + 5] - xyz[v + 2]];
        double[] b = [xyz[v + 6] - xyz[v], xyz[v + 7] - xyz[v + 1], xyz[v + 8] - xyz[v + 2]];
        double[] n = [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
        return n[0] * (eye[0] - xyz[v]) + n[1] * (eye[1] - xyz[v + 1]) + n[2] * (eye[2] - xyz[v + 2]) > 0;
    }

    private static (float[] Depth, float[] Color) Drawn(TerrainWorld world, TerrainView view, bool backFaces,
        (int Program, DepthTarget Target) into)
    {
        Run(world, view, backFaces);
        into.Target.Begin();
        BackCulled();
        world.DrawCulled(into.Program, view.CameraMvp, view.Camera, false, false, backFaces ? Runs : 1);
        GL.Disable(EnableCap.CullFace);
        var color = new float[4 * Width * Height];
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, into.Target.Framebuffer);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        GL.ReadPixels(0, 0, Width, Height, PixelFormat.Rgba, PixelType.Float, color);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (into.Target.Read(), color);
    }

    // As RenderOpaque draws: back faces culled, front faces counter-clockwise
    internal static void BackCulled()
    {
        GL.Enable(EnableCap.CullFace);
        GL.CullFace(TriangleFace.Back);
        GL.FrontFace(FrontFaceDirection.Ccw);
    }

    private static int Differing(float[] a, float[] b) =>
        a.Zip(b).Count(p => BitConverter.SingleToInt32Bits(p.First) != BitConverter.SingleToInt32Bits(p.Second));

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_Gpu")]
    private static extern IGpuBackend CullingGpu([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CounterBuffers")]
    private static extern ref int[] CounterBuffers([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_commands")]
    private static extern ref int CommandBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_frame")]
    private static extern ref int FrameCount([UnsafeAccessorType(Culling)] object? culling);
}
