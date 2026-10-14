using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The GPU time of the terrain's culling and its draws from rows.comp's commands on TerrainWorld (radius 320, six pools), by
// OpenGL's timer: the far shadow map (4096, chunkshadowmap's alpha test) as the engine culls it and with the casters the view
// cannot see left out; the camera's view (2560x1366, four RGBA16F outputs, a fragment shader as heavy as chunkopaque's) as
// written and sorted near to far. Rounds alternate the variants; the medians are printed. Run it alone. Faces facing away: the camera's
// view of the world sorted as the tessellator sorts (FaceSorting), drawn with back faces culled as RenderOpaque draws, whole,
// with the directions facing away left out and with their parts past the splits too: the culling's and the draw's GPU time, the
// draw's vertex shader invocations, primitives and primitives after clipping (ARB_pipeline_statistics_query).
[NonParallelizable]
[Explicit("a benchmark")]
public sealed class TerrainBenchGpuTests
{
    private const int Rounds = 40, Map = 4096, Width = 2560, Height = 1366;

    private static readonly (double Yaw, double Pitch, double[] Sun)[] Views =
        [(0.3, -0.15, [0.4, 0.8, 0.3]), (2.4, -0.5, [-0.7, 0.35, 0.2]), (4.2, 0.1, [0.1, 0.25, -0.9])];

    [Test]
    public void TheTerrainsDrawsOnTheGpu() => ShadowCastersGpuTests.Run(GpuRig.Gl, 320, world =>
    {
        var (shadow, opaque) = (TerrainWorld.Program(TerrainWorld.ShadowFragment), TerrainWorld.Program(TerrainWorld.OpaqueFragment));
        using var map = new DepthTarget(Map, Map);
        using var screen = new DepthTarget(Width, Height, 4);
        var query = GL.GenQuery();
        try
        {
            TestContext.Out.WriteLine($"{world.Triangles / 1e6:0.00} M triangles in {world.Locations.Count()} locations");
            foreach (var (yaw, pitch, sun) in Views)
            {
                var view = new TerrainView(yaw, pitch, sun);
                var mvp = TerrainView.Floats(view.ShadowMvp);
                var culler = ShadowCastersGpuTests.Culler(view);
                var times = new List<double>[4];
                for (var v = 0; v < times.Length; v++) times[v] = [];
                for (var round = 0; round < Rounds; round++)
                {
                    times[0].Add(Shadow(world, view, null, (shadow, map, mvp, query)));
                    times[1].Add(Shadow(world, view, view.View, (shadow, map, mvp, query)));
                    times[2].Add(Camera(world, view, culler, false, (opaque, screen, query)));
                    times[3].Add(Camera(world, view, culler, true, (opaque, screen, query)));
                }

                TestContext.Out.WriteLine(
                    $"yaw {yaw}: far map {Median(times[0]):0.000} ms as the engine culls it, {Median(times[1]):0.000} with the " +
                    $"casters out of view left out; the view {Median(times[2]):0.000} ms as written, {Median(times[3]):0.000} " +
                    "sorted near to far");
            }
        }
        finally
        {
            OcclusionCulling.SortAlways = false;
            GL.DeleteQuery(query);
            GL.DeleteProgram(shadow);
            GL.DeleteProgram(opaque);
        }
    });

    [Test]
    public void FacesFacingAwayLeftOut() => ShadowCastersGpuTests.Run(GpuRig.Gl, 320, world =>
    {
        var opaque = TerrainWorld.Program(TerrainWorld.OpaqueFragment);
        using var screen = new DepthTarget(Width, Height, 4);
        int[] queries = [GL.GenQuery(), GL.GenQuery(), GL.GenQuery(), GL.GenQuery()];
        try
        {
            TestContext.Out.WriteLine($"{world.Triangles / 1e6:0.00} M triangles in {world.Locations.Count()} locations");
            foreach (var (yaw, pitch, _) in Views)
            {
                var view = new TerrainView(yaw, pitch, [0.4, 0.8, 0.3]);
                var samples = FaceSplitsGpuTests.Variants.Select(_ => new List<double[]>()).ToArray();
                for (var round = 0; round < Rounds; round++)
                    for (var v = 0; v < samples.Length; v++)
                        samples[v].Add(Facing(world, view, FaceSplitsGpuTests.Variants[v], (opaque, screen, queries)));
                var lines = samples.Select(s => string.Create(CultureInfo.InvariantCulture,
                    $"{MedianOf(s, 0):0.000} ms, {MedianOf(s, 1) / 1e6:0.00} M vertex shader invocations, " +
                    $"{MedianOf(s, 2) / 1e6:0.00} M primitives, {MedianOf(s, 3) / 1e6:0.00} M after clipping")).ToArray();
                TestContext.Out.WriteLine($"yaw {yaw}: whole {lines[0]}; directions facing away and their parts past the " +
                                          $"splits left out {lines[1]}");
            }
        }
        finally
        {
            foreach (var query in queries) GL.DeleteQuery(query);
            GL.DeleteProgram(opaque);
        }
    }, sorted: true);

    private const int VertexShaderInvocations = 0x82F0, PrimitivesSubmitted = 0x82EF, ClippingOutputPrimitives = 0x82F7;

    // The culling's and the draw's GPU time, then the draw's statistics
    private static double[] Facing(TerrainWorld world, TerrainView view, bool backFaces,
        (int Program, DepthTarget Screen, int[] Queries) with)
    {
        var culling = Timed(with.Queries[0], () => FaceSplitsGpuTests.Run(world, view, backFaces));
        with.Screen.Begin();
        FaceSplitsGpuTests.BackCulled();
        int[] targets = [VertexShaderInvocations, PrimitivesSubmitted, ClippingOutputPrimitives];
        for (var q = 0; q < targets.Length; q++) GL.BeginQuery((QueryTarget)targets[q], with.Queries[q + 1]);
        var drawing = Timed(with.Queries[0],
            () => world.DrawCulled(with.Program, view.CameraMvp, view.Camera, false, false, backFaces ? 4 : 1));
        foreach (var target in targets) GL.EndQuery((QueryTarget)target);
        GL.Disable(EnableCap.CullFace);
        var result = new double[4];
        result[0] = culling + drawing;
        for (var q = 0; q < targets.Length; q++)
        {
            GL.GetQueryObject(with.Queries[q + 1], GetQueryObjectParam.QueryResult, out long count);
            result[q + 1] = count;
        }

        return result;
    }

    private static double MedianOf(List<double[]> samples, int at) => Median([.. samples.Select(s => s[at])]);

    private static double Shadow(TerrainWorld world, TerrainView view, FrustumCulling? casters,
        (int Program, DepthTarget Map, float[] Mvp, int Query) with)
    {
        var culling = Timed(with.Query, () => Assert.That(OcclusionCulling.RunShadow(world.Pools, view.ShadowCuller,
            view.Camera, EnumFrustumCullMode.CullInstantShadowPassFar, casters), Is.True));
        with.Map.Begin();
        return culling + Timed(with.Query, () => world.DrawCulled(with.Program, with.Mvp, view.Camera, true, false));
    }

    private static double Camera(TerrainWorld world, TerrainView view, FrustumCulling culler, bool sorted,
        (int Program, DepthTarget Screen, int Query) with)
    {
        OcclusionCulling.SortAlways = sorted;
        var culling = Timed(with.Query,
            () => Assert.That(OcclusionCulling.RunRows(world.Pools, culler, view.Camera, view.CameraMvp), Is.True));
        with.Screen.Begin();
        return culling + Timed(with.Query, () => world.DrawCulled(with.Program, view.CameraMvp, view.Camera, false, sorted));
    }

    private static double Timed(int query, Action draw)
    {
        GL.Finish();
        GL.BeginQuery(QueryTarget.TimeElapsed, query);
        draw();
        GL.EndQuery(QueryTarget.TimeElapsed);
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long ns);
        return ns / 1e6;
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
