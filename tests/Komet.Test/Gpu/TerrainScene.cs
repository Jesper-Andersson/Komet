using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// An engine chunk program, linked and ported as the game ports it: read back from the driver, the linker's attribute
// locations, origin pushed per pool
internal sealed class TerrainScene : IDisposable
{
    public TerrainScene(VulkanDevice device, bool high, string program = "chunkopaque")
    {
        Device = device;
        Name = program;
        var (vertex, fragment) = EngineShaders.Program(program, high);
        Program = Parity.Linked(vertex, fragment);
        var ported = ProgramPorts.Port(Program, program, out var why, new HashSet<string>(["origin"], StringComparer.Ordinal));
        Ported = ported ?? throw new InvalidOperationException(why);
    }

    public VulkanDevice Device { get; }
    public string Name { get; }
    public int Program { get; }
    public GlslPort.Ported Ported { get; }

    public void Dispose() => GL.DeleteProgram(Program);
}
