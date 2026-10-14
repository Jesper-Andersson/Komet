using Komet.Diagnostics;

namespace Komet.Vulkan;

// Uniforms changing nearly every draw go into push constants so the large blocks stay uploaded. Found over the first
// draws, then the program is ported once more; ShaderCache keeps them for the next start.
internal sealed partial class SceneRenderer
{
    private const int HotBudget = 224, MaxHot = 64, RecheckDraws = 1024, RecheckFrames = 16, MaxRounds = 2;
    private const double HotShare = 0.34;

    // Draws watched before hot uniforms settle
    public static int Watched { get; set; } = 96;

    // Settled but still repacking in >= 1/4 of draws: watched over atypical draws (GUI: hotbar first, icons later), so
    // re-watched up to MaxRounds. Only for programs drawn many times a frame; once-a-frame passes repack every frame
    // anyway.
    private void Recheck(Drawn program)
    {
        if (!NotNull(program) || !Assert(program.Rounds >= 0) || program.Rounds >= MaxRounds || program.Next is not null || ++program.Draws < RecheckDraws) return;
        var mirror = program.Program.Mirror;
        var (packs, frames) = (mirror.Packs - program.PacksAt, _terrain.Frame.Number - program.FrameAt);
        (program.Draws, program.PacksAt, program.FrameAt) = (0, mirror.Packs, _terrain.Frame.Number);
        if (packs < RecheckDraws / 4 || frames > RecheckFrames || !Assert(packs >= 0)) return;
        (program.Rounds, program.Settled) = (program.Rounds + 1, false);
        mirror.Remeasure();
        _logger?.Notification("Komet: Vulkan watches the uniforms of {0} again: {1} block packs in {2} draws", program.Name, packs,
            RecheckDraws);
    }

    // New port on a builder, then its pipelines again (Warm); the old port draws until all are made (Current), so the
    // switch leaves no draw to OpenGL
    private void Settle(Drawn program)
    {
        if (!NotNull(program)) return;
        if (program.Settled && program.Again is null)
        {
            Recheck(program);
            return;
        }

        if (program.Again is var (again, names))
        {
            if (again.Done) Warmed(program, again, names);
            return;
        }

        var mirror = program.Program.Mirror;
        mirror.Drawn();
        if (mirror.Draws < Watched) return;
        program.Settled = true;
        // already pushed uniforms stay pushed, ahead of the ones changing now
        var pushed = program.Program.Ported.HotUniforms.Members.Select(static u => u.Name);
        var hot = Budgeted(program.Program, [.. pushed.Concat(mirror.Hot(HotShare)).Distinct(StringComparer.Ordinal)]);
        if (hot.Count == 0) return;
        var start = Hitches.Now;
        var port = ProgramPorts.Started(program.Program.Id, program.Name, out var error, hot, settled: true);
        Hitches.Since(Hitches.Kind.Port, start);
        if (port is not null) program.Again = (port, string.Join(", ", hot));
        else
            _logger?.Notification("Komet: Vulkan port of {0} with {1} pushed: {2}", program.Name, string.Join(", ", hot), error);
    }

    private void Warmed(Drawn program, Build<GlslPort.Ported> again, string hot)
    {
        program.Again = null;
        var ported = again.Take(out var error);
        var (id, name, port) = (program.Program.Id, program.Name, ++_ports);
        _logger?.Notification("Komet: Vulkan port of {0} with {1} pushed: {2}", name, hot,
            ported is null ? error : $"{ported.HotUniforms.Size} bytes, {FrameClock.ToMs(again.Ticks):0} ms off the engine's thread");
        if (ported is null || !Assert(id > 0) || !Assert(program.Next is null)) return;
        var old = program.Port;
        program.Next = (ported, port,
            _pipelines.Warm(key => key.Program == id && key.Port == old, key => key with { Port = port }, (name, ported)));
    }

    // Switched to the next port once its pipelines are made, as this frame counts them
    private Drawn? Current(Drawn? program)
    {
        if (program?.Next is not var (ported, port, keys) || !_pipelines.Made(keys)) return program;
        var (id, name) = (program.Program.Id, program.Name);
        Forget(id, keep: port);
        var next = new Drawn(_terrain.Watch(name, id, ported), ProgramPorts.Points(id, ported))
        {
            Settled = true, Port = port, Rounds = program.Rounds, Sources = program.Sources
        };
        _programs[id] = next;
        _ = Assert(_programs.Count <= MaxScenePrograms) && Assert(next.Port > program.Port);
        return next;
    }

    private static HashSet<string> Budgeted(TerrainRenderer.Program program, IReadOnlyList<string> changed)
    {
        var hot = new HashSet<string>(StringComparer.Ordinal);
        if (!NotNull(program) || !NotNull(changed)) return hot;
        var members = program.Ported.VertexUniforms.Members.Concat(program.Ported.FragmentUniforms.Members)
            .Concat(program.Ported.HotUniforms.Members).GroupBy(u => u.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bytes = 0;
        foreach (var name in changed.Take(MaxHot).ToArray().Bounded(MaxHot))
        {
            if (!members.TryGetValue(name, out var u) || u.Count > 4) continue;
            var (size, align) = Std430(u);
            var at = (bytes + align - 1) / align * align;
            if (size == 0 || at + size > HotBudget) continue;
            bytes = at + size;
            _ = hot.Add(name);
        }

        _ = Assert(bytes <= HotBudget);
        return hot;
    }

    // std430 size/alignment: scalar 4, vec2 8, vec3 and vec4 16, a matrix its columns as vec4s
    private static (int Size, int Align) Std430(GlslPort.Uniform u)
    {
        var (columns, rows) = GlslPort.Shape(u.Type);
        if (!NotNull(u) || !Assert(columns >= 0 && rows >= 0) || columns * rows == 0) return (0, 4);
        var align = rows switch { 1 => 4, 2 => 8, _ => 16 };
        var element = columns > 1 ? columns * align : 4 * rows;
        var stride = (element + align - 1) / align * align;
        return (u.Count > 1 ? stride * u.Count : element, align);
    }
}
