using System.Text.RegularExpressions;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// The distant cascade's shader additions on the game's own includes, and the geometry of its map: a centre on the texel grid, the
// matrix that takes it to the middle of the map, and when a map is due again. Its drawing needs a GL context: that is checked in game.
public sealed class DistantShadowsTests
{
    // ShaderProgram.collectUniformNames: the uniforms the engine finds and gives locations and texture units
    private static readonly Regex EngineUniforms = new(
        @"(\s|\r\n)uniform\s*(?<type>float|int|ivec2|ivec3|ivec4|vec2|vec3|vec4|sampler2DShadow|sampler2D|" +
        @"samplerCube|mat3|mat4x3|mat4)\s*(\[[\d\w]+\])?\s*(?<var>[\d\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    private static string Include(string name)
    {
        GameInstall.RequireAssets();
        return File.ReadAllText(Path.Combine(GameInstall.Assets, "game", "shaderincludes", name));
    }

    private static List<string> Uniforms(string code) =>
        [.. EngineUniforms.Matches(code).Select(m => m.Groups["var"].Value)];

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-distantshadows");
        DistantShadows.Install(harmony, new QuietLogger());
        Assert.That(DistantShadows.Matched, Is.True, "a seam is not the body verified");
    }

    // Added once each, after the far cascade; the engine finds no new uniform, so no sampler moves to another texture unit
    [TestCase(DistantShadows.VertexFile)]
    [TestCase(DistantShadows.FragmentFile)]
    public void TheIncludesGetTheCascadeOnceAndTheEngineSeesNoNewUniform(string file)
    {
        var source = Include(file);
        var added = file == DistantShadows.VertexFile ? DistantShadows.Vertex(source) : DistantShadows.Fragment(source);
        Assert.That(added, Is.Not.Null, "the anchors are not in the game's include");
        var again = file == DistantShadows.VertexFile ? DistantShadows.Vertex(added) : DistantShadows.Fragment(added);
        Assert.Multiple(() =>
        {
            Assert.That(added, Does.Contain("shadowCoordsDistant").And.Contain(DistantShadows.Marker));
            Assert.That(added!.IndexOf("shadowCoordsDistant", StringComparison.Ordinal),
                Is.GreaterThan(added.IndexOf("shadowCoordsFar", StringComparison.Ordinal)), "after the far cascade");
            Assert.That(again, Is.Null, "added twice");
            Assert.That(Uniforms(added), Is.EqualTo(Uniforms(source)), "a new uniform the engine would bind");
        });
    }

    // The fragment side only compiles where the vertex side writes shadowCoordsDistant
    [Test]
    public void TheFragmentPartNeedsTheDefine()
    {
        var added = DistantShadows.Fragment(Include(DistantShadows.FragmentFile))!;
        var guarded = Regex.Count(added, @"#if[^\n]*KOMET_DISTANT", RegexOptions.None, TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(guarded, Is.EqualTo(2), "declarations and body");
            Assert.That(DistantShadows.Definition, Does.StartWith("#define KOMET_DISTANT"));
        });
    }

    [Test]
    public void AnotherShaderIsLeftAlone()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DistantShadows.Vertex("void main() {}"), Is.Null);
            Assert.That(DistantShadows.Fragment(null), Is.Null);
        });
    }

    // The centre moves off the camera only across the light, onto a texel corner in the light's plane, wherever the camera is
    [TestCase(512568.3, 200.7, 512874.9, 0.3, 0.8, 0.52)]
    [TestCase(1000.5, 110.2, -2345.25, -0.6, 0.45, 0.66)]
    [TestCase(0.0, 0.0, 0.0, 0.1, 0.99, 0.1)]
    public void AMapIsCentredOnTheTexelGrid(double x, double y, double z, double lx, double ly, double lz)
    {
        double[] light = Unit([lx, ly, lz]), camera = [x, y, z];
        var map = DistantShadows.Plan(light, camera, 576, 4096);
        var texel = 2 * 576.0 / 4096;
        double[] delta = [map.Center[0] - x, map.Center[1] - y, map.Center[2] - z];
        Assert.Multiple(() =>
        {
            for (var row = 0; row < 2; row++)
            {
                var along = map.View[row] * map.Center[0] + map.View[4 + row] * map.Center[1] +
                            map.View[8 + row] * map.Center[2];
                Assert.That(Math.Abs(along / texel - Math.Round(along / texel)), Is.LessThan(1e-6),
                    $"row {row} off the grid");
            }

            Assert.That(delta[0] * light[0] + delta[1] * light[1] + delta[2] * light[2], Is.EqualTo(0).Within(1e-6));
            Assert.That(Math.Sqrt(delta.Sum(d => d * d)), Is.LessThanOrEqualTo(texel), "more than a texel away");
        });
    }

    // Camera-relative positions go to clip space: the centre to the middle, a point at the radius across the light to the edge
    [Test]
    public void TheMatrixTakesTheCentreToTheMiddle()
    {
        double[] light = [0.3, 0.8, 0.52], camera = [512568.3, 200.7, 512874.9];
        var map = DistantShadows.Plan(light, camera, 576, 4096);
        double[] moved = [camera[0] + 40, camera[1], camera[2] - 25];
        var clip = DistantShadows.Transform(map, moved);
        var (cx, cy) = Apply(clip, map.Center[0] - moved[0], map.Center[1] - moved[1], map.Center[2] - moved[2]);
        double[] right = [map.View[0], map.View[4], map.View[8]];
        var (ex, _) = Apply(clip, map.Center[0] + 576 * right[0] - moved[0], map.Center[1] + 576 * right[1] - moved[1],
            map.Center[2] + 576 * right[2] - moved[2]);
        Assert.Multiple(() =>
        {
            Assert.That((cx, cy), Is.EqualTo((0.0, 0.0)).Using<(double, double)>((a, b) =>
                Math.Abs(a.Item1 - b.Item1) < 1e-6 && Math.Abs(a.Item2 - b.Item2) < 1e-6));
            Assert.That(ex, Is.EqualTo(1).Within(1e-6));
        });
    }

    private static double[] Unit(double[] v)
    {
        var length = Math.Sqrt(v.Sum(c => c * c));
        return [.. v.Select(c => c / length)];
    }

    private static (double X, double Y) Apply(double[] m, double x, double y, double z) =>
        (m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13]);

    [Test]
    public void AMapIsDueWhenTheLightTurnsOrTheCameraLeavesTheMargin()
    {
        double[] light = Unit([0.3, 0.8, 0.52]), camera = [100, 120, 100];
        var map = DistantShadows.Plan(light, camera, 576, 2048);
        map.Valid = true;
        var turned = Unit([light[0] + 0.01, light[1], light[2]]);
        Assert.Multiple(() =>
        {
            Assert.That(DistantShadows.Due(new DistantShadows.Map(), light, camera, 576), Is.True, "no map yet");
            Assert.That(DistantShadows.Due(map, light, camera, 576), Is.False, "nothing changed");
            Assert.That(DistantShadows.Due(map, light, [140, 300, 130], 576), Is.False,
                "within the margin, height aside");
            Assert.That(DistantShadows.Due(map, light, [210, 120, 100], 576), Is.True, "out of the margin");
            Assert.That(DistantShadows.Due(map, turned, camera, 576), Is.True, "the light turned half a degree");
            Assert.That(DistantShadows.Due(map, light, camera, 1088), Is.True, "another view distance");
        });
    }
}
