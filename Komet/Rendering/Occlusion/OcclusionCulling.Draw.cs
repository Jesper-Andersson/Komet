using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Late surface draws follow topsoil: each surface call records its program and bound textures to switch back and rebind
internal static partial class OcclusionCulling
{
    private const int MaxRecords = 64, MaxBindings = 256, MaxSnapshots = 1024;

    private static readonly List<Binding> Bindings = [];
    private static readonly Binding[] Snapshots = new Binding[MaxSnapshots];
    private static readonly Record[] Records = new Record[MaxRecords];
    private static int _snapshots, _records;
    private static bool _replaying;

    // Vulkan pool draw from backend command/count buffers (byte offsets), at most max; false if refused; null while OpenGL
    // draws the terrain (then nothing is culled)
    internal delegate bool CountedDraw(VAO vao, bool ssbo, (int Buffer, int Offset) commands, (int Buffer, int Offset) count,
        int max);

    internal static CountedDraw? Drawer { get; set; }

    // Draws Vulkan refused (terrain missing from that frame)
    public static long Refused { get; private set; }

    private static void Bound(ShaderProgramBase __instance, string samplerName, int textureId, int textureNumber)
    {
        if (!_opaque || _replaying || !NotNull(__instance) || !NotNull(samplerName)) return;
        var at = -1;
        for (var i = 0; i < Math.Min(Bindings.Count, MaxBindings) && at < 0; i++) // every bind: no closure
            if (ReferenceEquals(Bindings[i].Program, __instance) && Bindings[i].Sampler == samplerName)
                at = i;
        var binding = new Binding(__instance, samplerName, textureId, textureNumber);
        if (at >= 0) Bindings[at] = binding;
        else if (Bindings.Count < MaxBindings) Bindings.Add(binding);
    }

    private static void ClearBindings()
    {
        Bindings.Clear();
        (_snapshots, _records, _replaying) = (0, 0, false);
        _ = Assert(Bindings.Count == 0);
    }

    private static void Remember((int First, int Count) draws)
    {
        if (!Index(_records, MaxRecords) || !Assert(draws.Count > 0)) return;
        var program = ShaderProgramBase.CurrentShaderProgram;
        var first = _snapshots;
        for (var i = 0; i < Math.Min(Bindings.Count, MaxBindings) && _snapshots < MaxSnapshots; i++)
            if (ReferenceEquals(Bindings[i].Program, program))
                Snapshots[_snapshots++] = Bindings[i];
        Records[_records++] = new Record(program, first, _snapshots - first, draws.First, draws.Count);
    }

    private static void IssueLate(int counters)
    {
        if (!Assert(_records <= MaxRecords) || !Assert(counters != 0)) return;
        var before = ShaderProgramBase.CurrentShaderProgram;
        _replaying = true;
        try
        {
            for (var r = 0; r < Math.Min(_records, MaxRecords); r++)
            {
                var record = Records[r];
                Switch(record.Program);
                for (var s = record.First; s < Math.Min(record.First + record.Textures, MaxSnapshots); s++)
                    Snapshots[s].Program.BindTexture2D(Snapshots[s].Sampler, Snapshots[s].Texture, Snapshots[s].Unit);
                Issue((record.DrawFirst, record.DrawCount), (MaxRanges, StatUints + MaxDraws), counters);
            }

            Switch(before);
            for (var b = 0; b < Math.Min(Bindings.Count, MaxBindings); b++)
                if (before is not null && ReferenceEquals(Bindings[b].Program, before))
                    before.BindTexture2D(Bindings[b].Sampler, Bindings[b].Texture, Bindings[b].Unit);
        }
        finally
        {
            _replaying = false;
        }
    }

    // Stop, then Use: Use refuses while another program is in use
    private static void Switch(ShaderProgramBase? program)
    {
        var active = ShaderProgramBase.CurrentShaderProgram;
        if (ReferenceEquals(active, program) || !Assert(program is null || !program.Disposed)) return;
        active?.Stop();
        program?.Use();
    }

    // Pass 0 at (0, StatUints), pass 1 at (MaxRanges, StatUints + MaxDraws); sorted: from the Sorted buffer
    private static void Issue((int First, int Count) draws, (int Commands, int Counts) at, int counters, bool sorted = false)
    {
        if (_api?.Render.CurrentActiveShader is not { } shader || !Assert(counters != 0) ||
            !Assert(draws.First + draws.Count <= 2 * MaxDraws)) return;
        var (commands, counts) = at;
        var buffer = sorted ? _sorted : _commands;
        if (Drawer is { } drawer && Assert(buffer != 0)) Handed(drawer, shader, draws, (commands, counts), (buffer, counters));
    }

    // To the Vulkan frame, origin uniform set as the engine's (the frame mirrors uniforms)
    private static void Handed(CountedDraw drawer, IShaderProgram shader, (int First, int Count) draws,
        (int Commands, int Counts) at, (int Commands, int Counters) buffers)
    {
        if (!NotNull(drawer) || !Assert(draws.First + draws.Count <= 2 * MaxDraws)) return;
        for (var d = draws.First; d < Math.Min(draws.First + draws.Count, 2 * MaxDraws); d++)
        {
            var draw = Draws[d];
            if (draw.Count == 0) continue;
            shader.Uniform(_origin, draw.Offset);
            if (!drawer(draw.Vao, _useSsbos, (buffers.Commands, (at.Commands + draw.Start) * CommandBytes),
                    (buffers.Counters, 4 * (at.Counts + d)), draw.Count)) Refused++;
            RuntimeStats.drawCallsCount++;
        }

        _ = Assert(Refused >= 0);
    }

    private readonly record struct Binding(ShaderProgramBase Program, string Sampler, int Texture, int Unit);

    private readonly record struct Record(
        ShaderProgramBase? Program, int First, int Textures, int DrawFirst, int DrawCount);
}
