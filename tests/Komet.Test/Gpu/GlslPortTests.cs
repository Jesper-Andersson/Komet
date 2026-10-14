using System.Text.RegularExpressions;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class GlslPortTests
{
    private static readonly (string, int, int, int)[] Laid =
        [("mvp", 0, 0, 1), ("fog", 64, 0, 1), ("lights", 80, 16, 4)];

    private static readonly (string, int)[] Samplers =
        [("tex", GlslPort.FirstSampler), ("glow", GlslPort.FirstSampler + 1)];

    private static readonly float[] Fog = [0.5f], Tint = [1, 0.5f, 0.25f, 1];
    private static readonly int[] TwoOutputs = [0, 1];
    private static readonly string[] Unconditional = ["mvp", "lights"];
    private static readonly GlslPort.Attribute[] Xyz = [new("xyz", "vec3", 0)], BoundXyz = [new("xyz", "vec3", 5)];
    private static readonly (string, int)[] Columns = [("vec4", 4), ("vec4", 5), ("vec4", 6), ("vec4", 7), ("vec4", 8)];
    private static readonly GlslPort.BlockBinding[] Faces = [new("faces", true, 3, GlslPort.FirstStorage + 3)];

    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 xyz;
        uniform mat4 mvp;
        uniform float fog = 0.5;
        uniform vec3 lights[2 * 2];
        uniform sampler2D tex;
        out vec4 a;
        out vec2 b;
        flat out int c;
        void main() {
            a = texture(tex, xyz.xy) * fog + vec4(lights[3], 1); b = xyz.xy; c = gl_VertexID;
            gl_Position = mvp * vec4(xyz, 1); }
        """;

    private const string Fragment = """
        #version 330 core
        uniform sampler2D tex;
        uniform sampler2D glow;
        uniform vec4 tint = vec4(1, 0.5, 0.25, 1);
        flat in int c;
        in vec4 a;
        layout(location = 0) out vec4 color;
        layout(location = 1) out vec4 bright;
        void main() { color = a * tint * float(c); bright = texture(glow, vec2(0)) + texture(tex, vec2(0)); }
        """;

    [OneTimeSetUp]
    public void RequireShaderc()
    {
        if (!Shaderc.Load()) Assert.Ignore("libshaderc is not installed");
    }

    [Test]
    public void VulkanOnlysVertexStageWritesOpenGLsPointSize()
    {
        GlslPort.PointSized = true;
        try
        {
            Assert.That(Port(Vertex, Fragment).VertexSource, Does.Contain("komet_main(); gl_PointSize = 1.0;"));
            var sized = Vertex.Replace("gl_Position = mvp", "gl_PointSize = 4.0; gl_Position = mvp", StringComparison.Ordinal);
            Assert.That(Port(sized, Fragment).VertexSource, Does.Not.Contain("komet_main"), "the shader's own size stays");
        }
        finally
        {
            GlslPort.PointSized = false;
        }
    }

    private static GlslPort.Ported Port(string vertex, string fragment, Dictionary<string, int>? attributes = null)
    {
        var ported = GlslPort.Build("test", vertex, fragment, attributes, out var error);
        Assert.That(ported, Is.Not.Null, error);
        return ported!;
    }

    [Test]
    public void LooseUniformsBecomeAStd140BlockPerStageWithTheirDefaults()
    {
        var ported = Port(Vertex, Fragment);
        var members = ported.VertexUniforms.Members;
        Assert.That(members.Select(u => (u.Name, u.Offset, u.Stride, u.Count)), Is.EqualTo(Laid));
        Assert.That(ported.VertexUniforms.Size, Is.EqualTo(144));
        Assert.That(members[1].Default, Is.EqualTo(Fog));
        Assert.That(members[0].Default, Is.Null);
        Assert.That(ported.FragmentUniforms.Members.Single().Default, Is.EqualTo(Tint));
        Assert.That(ported.VertexUniforms.Binding, Is.EqualTo(GlslPort.VertexBlock));
        Assert.That(ported.FragmentUniforms.Binding, Is.EqualTo(GlslPort.FragmentBlock));
    }

    [Test]
    public void ASamplerKeepsOneBindingAcrossTheStages()
    {
        var ported = Port(Vertex, Fragment);
        Assert.That(ported.Samplers.Select(s => (s.Name, s.Binding)), Is.EqualTo(Samplers));
    }

    [Test]
    public void FragmentInputsMeetTheVertexOutputsOfTheirName()
    {
        var ported = Port(Vertex, Fragment);
        Assert.That(Location(ported.VertexSource, "out", "a"), Is.EqualTo(0));
        Assert.That(Location(ported.VertexSource, "out", "c"), Is.EqualTo(2));
        Assert.That(Location(ported.FragmentSource, "in", "c"), Is.EqualTo(2), "declared first, matched by name");
        Assert.That(Location(ported.FragmentSource, "in", "a"), Is.EqualTo(0));
        Assert.That(ported.Outputs, Is.EqualTo(TwoOutputs));
        Assert.That(ported.Attributes, Is.EqualTo(Xyz));
    }

    [Test]
    public void AMatrixInputTakesALocationPerColumn()
    {
        GlslPort.Attribute[] inputs = [new("light", "vec4", 4), new("transform", "mat4", 5)];
        Assert.That(GlslPort.Columns(inputs).Select(a => (a.Type, a.Location)),
            Is.EqualTo(Columns));
        Assert.That(GlslPort.Columns(Xyz), Is.SameAs(Xyz));
    }

    [Test]
    public void TheVersionStaysAndTheLinesKeepTheirNumbers()
    {
        var ported = Port(Vertex, Fragment);
        Assert.That(ported.VertexSource, Does.StartWith("#version 330 core\n"));
        Assert.That(ported.VertexSource, Does.Contain("gl_VertexIndex").And.Not.Contain("gl_VertexID"));
        var broken = Vertex.Replace("b = xyz.xy;", "b = nope;", StringComparison.Ordinal);
        Assert.That(GlslPort.Build("test", broken, Fragment, null, out var error), Is.Null);
        Assert.That(error, Does.Contain("test.vsh:11:").And.Contain("nope"), "main's body is line 11 of the source");
    }

    [Test]
    public void ConditionalsAreResolvedBeforeTheUniformsMove()
    {
        const string conditional = "#define FOG 0\n#if FOG > 0\nuniform float fog = 0.5;\n" +
                                   "#else\nconst float fog = 1.0;\n#endif";
        var vertex = Vertex.Replace("uniform float fog = 0.5;", conditional, StringComparison.Ordinal);
        var ported = Port(vertex, Fragment);
        Assert.That(ported.VertexUniforms.Members.Select(u => u.Name), Is.EqualTo(Unconditional));
    }

    [Test]
    public void StorageBuffersMoveAboveTheSamplers()
    {
        const string faces = "#version 430 core\nstruct F { vec4 p; };\n" +
                             "layout(binding = 3, std430) readonly buffer faces { F f[]; };";
        var vertex = Vertex.Replace("#version 330 core", faces, StringComparison.Ordinal)
            .Replace("xyz.xy; c", "xyz.xy + f[gl_VertexID].p.xy; c", StringComparison.Ordinal);
        var ported = Port(vertex, Fragment);
        Assert.That(ported.Blocks, Is.EqualTo(Faces));
    }

    [Test]
    public void AnAttributeTakesItsBoundLocationWhenTheSourceNamesNone()
    {
        var vertex = Vertex.Replace("layout(location = 0) in vec3 xyz;", "in vec3 xyz;", StringComparison.Ordinal);
        Assert.That(GlslPort.Build("test", vertex, Fragment, null, out var error), Is.Null);
        Assert.That(error, Does.Contain("xyz"));
        var ported = Port(vertex, Fragment, new Dictionary<string, int> { ["xyz"] = 5 });
        Assert.That(ported.Attributes, Is.EqualTo(BoundXyz));
    }

    private static int Location(string source, string direction, string name)
    {
        var match = Regex.Match(source, $@"layout\(location = (\d+)\) (?:flat )?{direction} \w+ {name};");
        Assert.That(match.Success, Is.True, $"{direction} {name}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
