using Komet.Rendering;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class VulkanRendererGpuTests
{
    private const string Renderer = "Komet.Vulkan.VulkanRenderer, Komet";
    private static readonly string[] Hot = ["origin"];

    private static GlslPort.Ported? Port(string name) =>
        Ports(null)[Math.Max(Array.FindIndex(Programs(null), p => p.Name == name), 0)];

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Programs")]
    private static extern ref (string Name, VulkanRenderer.Pass Pass, VulkanRenderer.Pass Also)[] Programs(
        [UnsafeAccessorType(Renderer)] object? renderer);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Ports")]
    private static extern ref GlslPort.Ported?[] Ports([UnsafeAccessorType(Renderer)] object? renderer);

    // The first report comes after one frame, before OcclusionCulling read any statistics back (Stat: NaN): no line about the
    // terrain drawn, and no assertion
    [Test]
    public void AReportBeforeAnyCullingStatisticsLeavesThemOut()
    {
        if (!double.IsNaN(OcclusionCulling.Stat(0))) Assert.Ignore("OcclusionCulling counts already in this run");
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        try
        {
            Assert.That(VulkanRenderer.DrawnTerrain(), Is.Empty);
            Assert.That(logger.Lines, Is.Empty, "no assertion failed");
        }
        finally
        {
            Contracts.Attach(null);
        }
    }

    [Test]
    public void TheProgramsTheDriverHoldsArePortedOncePerBuildOfAPassSwitchedOn()
    {
        GameInstall.RequireAssets();
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        if (!Shaderc.Load()) Assert.Ignore("libshaderc is not installed");
        // in the renderer's order: the liquid depth, the shadow map, the two opaque, the two transparent programs
        ShaderProgramBase?[] programs = [null, null, Program("chunkopaque"), Program("chunktopsoil"), null, null];
        try
        {
            Updated(programs, [true, true, true, true, true]);
            var first = Port("chunkopaque");
            Assert.That(first?.Name, Is.EqualTo("chunkopaque"));
            Assert.That(Port("chunktopsoil")?.Name, Is.EqualTo("chunktopsoil"));
            Assert.That(first!.Vertex, Is.Not.Empty);
            Assert.That(first.HotUniforms.Members.Select(m => m.Name), Is.EqualTo(Hot), "pushed per pool");
            var opaque = programs[2]!.ProgramId;
            Assert.Multiple(() =>
            {
                Assert.That(VulkanRenderer.PassOf(opaque, EnumRenderStage.Opaque), Is.EqualTo(VulkanRenderer.Pass.Opaque));
                Assert.That(VulkanRenderer.PassOf(opaque, EnumRenderStage.AfterOIT),
                    Is.EqualTo(VulkanRenderer.Pass.WaterPlants), "after the transparent pass it draws the water plants");
                Assert.That(VulkanRenderer.PassOf(programs[3]!.ProgramId, EnumRenderStage.AfterOIT),
                    Is.EqualTo(VulkanRenderer.Pass.None), "only the opaque program draws them");
                Assert.That(VulkanRenderer.PassOf(opaque + 1000, EnumRenderStage.Opaque), Is.EqualTo(VulkanRenderer.Pass.None));
            });
            Updated(programs, [true, true, true, true, true]);
            Assert.That(Port("chunkopaque"), Is.SameAs(first), "the same build: no new port");
            GL.DeleteProgram(programs[2]!.ProgramId);
            programs[2] = Program("chunkopaque");
            Updated(programs, [true, true, false, true, false]);
            Assert.That(Port("chunkopaque"), Is.SameAs(first), "its pass switched off: not ported");
            var started = Builds.Started;
            VulkanRenderer.Update(programs, null, [true, true, false, true, true]);
            Assert.That(Builds.Started, Is.EqualTo(started + 1), "ported on a builder");
            Assert.That(VulkanRenderer.PassOf(programs[2]!.ProgramId, EnumRenderStage.Opaque),
                Is.EqualTo(VulkanRenderer.Pass.Opaque), "awaited: a pass without its port would be missing from the picture");
            Updated(programs, [true, true, false, true, true]);
            Assert.That(Port("chunkopaque"), Is.Not.SameAs(first).And.Not.Null,
                "the engine rebuilt it, and the water plants need it without the opaque pass");
        }
        finally
        {
            VulkanRenderer.Stop();
            foreach (var program in programs) Delete(program);
        }
    }

    // The ports are made on builders: once they are done, the next update takes them
    private static void Updated(ShaderProgramBase?[] programs, bool[] passes)
    {
        VulkanRenderer.Update(programs, null, passes);
        Assert.That(Builds.Wait(TimeSpan.FromSeconds(60)), Is.True, "the ports are made");
        VulkanRenderer.Update(programs, null, passes);
    }

    // A program as ShaderRegistry leaves it: both shaders compiled from the composed source, linked
    private static ShaderProgramBase Program(string name)
    {
        var (vertex, fragment) = EngineShaders.Program(name, true);
        var program = (ShaderProgramBase)RuntimeHelpers.GetUninitializedObject(typeof(ShaderProgramChunkopaque));
        program.VertexShader = Compiled(EnumShaderType.VertexShader, vertex, name + ".vsh");
        program.FragmentShader = Compiled(EnumShaderType.FragmentShader, fragment, name + ".fsh");
        program.ProgramId = GL.CreateProgram();
        GL.AttachShader(program.ProgramId, program.VertexShader.ShaderId);
        GL.AttachShader(program.ProgramId, program.FragmentShader.ShaderId);
        GL.LinkProgram(program.ProgramId);
        GL.GetProgram(program.ProgramId, GetProgramParameterName.LinkStatus, out var linked);
        Assert.That(linked, Is.EqualTo(1), GL.GetProgramInfoLog(program.ProgramId));
        return program;
    }

    private static Shader Compiled(EnumShaderType type, string source, string file)
    {
        var shader = new Shader(type, source, file) { ShaderId = GL.CreateShader((ShaderType)type) };
        GL.ShaderSource(shader.ShaderId, source);
        GL.CompileShader(shader.ShaderId);
        GL.GetShader(shader.ShaderId, ShaderParameter.CompileStatus, out var compiled);
        Assert.That(compiled, Is.EqualTo(1), file + ": " + GL.GetShaderInfoLog(shader.ShaderId));
        return shader;
    }

    private static void Delete(ShaderProgramBase? program)
    {
        if (program is null) return;
        GL.DeleteShader(program.VertexShader.ShaderId);
        GL.DeleteShader(program.FragmentShader.ShaderId);
        GL.DeleteProgram(program.ProgramId);
    }
}
