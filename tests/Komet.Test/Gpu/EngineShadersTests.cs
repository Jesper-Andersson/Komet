using System.Text.RegularExpressions;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

// Put together as ShaderRegistry and ClientPlatformWindows.CompileShader do. Graphics settings change the defines, so the
// terrain ports with everything off and everything on.
public sealed class EngineShadersTests
{
    private static readonly GlslPort.Attribute Light = new("rgbaLightIn", "vec4", 0);
    private static readonly int[] Low = [0, 1], High = [0, 1, 2, 3];
    private static readonly float[] One = [1f];
    private static readonly GlslPort.BlockBinding Faces = new("faceDataBuf", true, 3, GlslPort.FirstStorage + 3);

    public static IEnumerable<TestCaseData> Settings() =>
        ((string[])["chunkopaque", "chunktopsoil"]).SelectMany(name => (TestCaseData[])
        [
            new TestCaseData(name, 0).SetArgDisplayNames(name, "low"),
            new TestCaseData(name, 1).SetArgDisplayNames(name, "high")
        ]);

    [OneTimeSetUp]
    public void Require()
    {
        GameInstall.RequireAssets();
        if (!Shaderc.Load()) Assert.Ignore("libshaderc is not installed");
    }

    [TestCaseSource(nameof(Settings))]
    public void TheTerrainPorts(string name, int high)
    {
        ArgumentNullException.ThrowIfNull(name);
        var (vertex, fragment) = EngineShaders.Program(name, high == 1);
        var ported = GlslPort.Build(name, vertex, fragment, null, out var error);
        Assert.That(ported, Is.Not.Null, error);
        Assert.That(ported!.Blocks, Does.Contain(Faces));
        Assert.That(ported.Attributes, Does.Contain(Light));
        Assert.That(ported.Samplers.Select(s => s.Name), Does.Contain("terrainTex").And.Contain("terrainTexLinear"));
        Assert.That(ported.Outputs, Is.EqualTo(high == 1 ? High : Low));
        var matrices = ported.VertexUniforms.Members.Where(u => u.Type == "mat4").Select(u => u.Name);
        Assert.That(matrices, Does.Contain("projectionMatrix").And.Contain("modelViewMatrix"));
        var uniforms = ported.VertexUniforms.Members.Concat(ported.FragmentUniforms.Members);
        Assert.That(uniforms.First(u => u.Name == "shadowIntensity").Default, Is.EqualTo(One));
    }

    // Vulkan-only's packed face data: each chunk program takes the patch and still ports and compiles, reading the same block
    [TestCase("chunkopaque"), TestCase("chunktopsoil"), TestCase("chunktransparent"), TestCase("chunkshadowmap")]
    public void TheChunkProgramsReadPackedFaces(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var (vertex, fragment) = EngineShaders.Program(name, true);
        var patched = CompactFaces.Patch(vertex);
        Assert.That(patched, Is.Not.Null.And.Contains("kometFace(gl_VertexID / 4)"));
        var ported = GlslPort.Build(name, patched!, fragment, EngineShaders.Attributes(name), out var error);
        Assert.That(ported, Is.Not.Null, error);
        Assert.That(ported!.Blocks, Does.Contain(Faces));
    }

    // Every vertex and fragment pair the game ships: what the port cannot take yet, by name and reason, so the list only shrinks
    [Test]
    public void TheGamesProgramsPort()
    {
        var failed = new List<string>();
        var names = Directory.GetFiles(EngineShaders.Folder, "*.vsh").Select(Path.GetFileNameWithoutExtension).Order();
        foreach (var name in names)
        {
            if (!File.Exists(Path.Combine(EngineShaders.Folder, name + ".fsh"))) continue;
            var (vertex, fragment) = EngineShaders.Program(name!, true);
            if (GlslPort.Build(name!, vertex, fragment, EngineShaders.Attributes(name!), out var error) is null)
                failed.Add($"{name}: {error.Split('\n')[0]}");
        }

        TestContext.Out.WriteLine(string.Join('\n', failed));
        Assert.That(failed, Is.Empty);
    }
}

internal static class EngineShaders
{
    public static readonly string Folder = Path.Combine(GameInstall.Assets, "game", "shaders");
    private static readonly string Includes = Path.Combine(GameInstall.Assets, "game", "shaderincludes");

    // ShaderRegistry.registerDefaultShaderCodePrefixes, with the settings all off or all on; the programs that include the
    // order-independent transparency are the ones the engine marks Oit
    public static (string Vertex, string Fragment) Program(string name, bool high)
    {
        var (on, level) = (high ? 1 : 0, high ? 2 : 0);
        var shared = $"#define SSAOLEVEL {level}\r\n#define NORMALVIEW 0\r\n#define GODRAYS {level}\r\n" +
                     $"#define FOAMEFFECT {on}\r\n#define SHINYEFFECT {on}\r\n#define SHADOWQUALITY {level}\r\n" +
                     $"#define DYNLIGHTS {4 * on}\r\n";
        var oit = File.ReadAllText(Path.Combine(Folder, name + ".fsh"))
            .Contains("#include oit.fsh", StringComparison.Ordinal);
        var fragment = $"#define FXAA {on}\r\n#define BLOOM {on}\r\n#define USEOIT {(oit ? 1 : 0)}\r\n" + shared;
        var vertex = $"#define USESSBO 1\r\n#define WAVINGSTUFF {on}\r\n#define MINBRIGHT 0\r\n" +
                     "#define MAXANIMATEDELEMENTS 46\r\n" + shared;
        var ssbo = name.StartsWith("chunk", StringComparison.Ordinal) || name == "decals";
        return (Compose(name + ".vsh", vertex, ssbo), Compose(name + ".fsh", fragment, false));
    }

    // The locations the engine binds with glBindAttribLocation, for sources that name none
    public static Dictionary<string, int>? Attributes(string name) => name switch
    {
        _ => null
    };

    private static string Compose(string file, string prefix, bool ssbo)
    {
        var code = Include(File.ReadAllText(Path.Combine(Folder, file)), []);
        if (ssbo) code = Regex.Replace(code, @"#version \d+", "#version 430");
        var at = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
        return code.Insert(at, prefix);
    }

    private static string Include(string code, HashSet<string> seen) =>
        Regex.Replace(code, @"^#include\s+(.*)", m =>
        {
            var name = m.Groups[1].Value.Trim().ToLowerInvariant();
            var path = Path.Combine(Includes, name);
            return seen.Add(name) && File.Exists(path) ? Include(File.ReadAllText(path), seen) : "";
        }, RegexOptions.Multiline);
}
