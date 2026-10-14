using Komet.Diagnostics;

namespace Komet.Vulkan;

// Every GL draw, clear and blit outside the terrain passes; what it cannot take is missing from the picture.
internal sealed partial class SceneRenderer : IGlWrites, IDisposable
{
    private const int MaxUnported = 1024, MaxScenePrograms = 256;

    private readonly TerrainRenderer _terrain;
    private readonly ILogger? _logger;
    private readonly Dictionary<int, Drawn> _programs = [];
    private readonly Dictionary<int, string> _unported = [];
    private readonly Dictionary<int, Build<GlslPort.Ported>> _porting = [];
    private int _ports; // ports drawn with so far, each a number of its own

    private sealed class Drawn(TerrainRenderer.Program program, int[] points)
    {
        public TerrainRenderer.Program Program { get; } = program;
        public int[] Points { get; } = points;
        public string Name => Program.Name;
        public (Formats Inputs, uint Topology, VulkanFrame.Target Target, GlTap.Fixed State, TerrainPipeline? Made)? Last { get; set; }
        public (GlTap.VertexArray Array, long Changes, Layout? Layout)? Laid { get; set; }

        public TerrainDraw.Texture[] Resolved { get; set; } = [];
        public TerrainDraw.Program? Recorded { get; set; }

        // Hot uniforms settled (watched over first draws, or came with them)
        public bool Settled { get; set; }

        // Since settling: draws and mirror packs at the last count, and re-watch rounds
        public int Draws { get; set; }
        public long PacksAt { get; set; }
        public long FrameAt { get; set; }
        public int Rounds { get; set; }

        // Port with the settled hot uniforms being made, and their names
        public (Build<GlslPort.Ported> Port, string Hot)? Again { get; set; }

        // Port in use (PipelineKey), and the next one, made, with its pipelines being made
        public int Port { get; init; }

        // Vulkan-only: ShaderCache key of its pipelines (SceneRenderer.Warm), "" for none
        public string Sources { get; init; } = "";
        public (GlslPort.Ported Ported, int Port, PipelineKey[] Pipelines)? Next { get; set; }

        // Last texture resolve key and its images: reused while nothing changed
        public (long Textures, int Samplers, long Images, long Targets, VulkanFrame.Target? Into) Key { get; set; }
        public SharedImage[] Images { get; set; } = [];

        // Reused across other binds while units hold the same textures and samplers (Bound) and nothing was deleted,
        // set or changed (Held)
        public (long Deleted, int Samplers, long Images, long Targets, VulkanFrame.Target? Into) Held { get; set; }
        public (int Unit, uint Kind, uint Texture, int Sampler)[] Bound { get; set; } = [];

        // Texture kind per sampler, and resolves by unit contents while Held stays SeenHeld (units cycle among a few
        // textures)
        public uint[] Kinds { get; } = [.. program.Ported.Samplers.Select(s => TargetOf(s.Type))];
        public Dictionary<Units, Seen> Seen { get; } = [];
        public (long Deleted, int Samplers, long Images, long Targets, VulkanFrame.Target? Into) SeenHeld { get; set; }
    }

    private SceneRenderer(TerrainRenderer terrain, ILogger? logger, HostBuffer constants)
    {
        _ = NotNull(terrain) && NotNull(constants);
        (_terrain, _logger, Constants) = (terrain, logger, constants);
        Mirrors = new BufferMirrors(terrain.Device, terrain.Frame)
        {
            Mapped = terrain.Maps.Read, Early = terrain.Images, Pooled = terrain.Pools.Range
        };
        terrain.Pools.Held = Mirrors.Taken;
        _pipelines = new PipelineSet<PipelineKey>(terrain.Device, terrain.Frame, logger, MaxPipelines);
    }

    public static SceneRenderer? Create(TerrainRenderer terrain, ILogger? logger)
    {
        if (!NotNull(terrain) || HostBuffer.Create(terrain.Device, 64) is not { } constants) return null;
        var at = constants.Take(32, out var into, 16);
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(into);
        (words[3], words[7]) = (BitConverter.SingleToUInt32Bits(1), 1); // OpenGL's (0, 0, 0, 1), as floats, then as ints
        _ = Assert(at == 0) && Assert(words.Length == 8);
        return new SceneRenderer(terrain, logger, constants);
    }

    public BufferMirrors Mirrors { get; }

    // (0, 0, 0, 1) as floats at 0 and integers at 16: what a disabled attribute reads
    private HostBuffer Constants { get; }

    public System.Func<int, string, bool> Takes { get; set; } = (_, _) => false;

    // Draws and clears are taken only between frame start and end
    public bool Framing { get; set; }

    public string Unported => string.Join("; ", ((IEnumerable<string>)_unported.Values).Bounded(16));

    public long Draws { get; private set; }
    public long Clears { get; private set; }
    public long Left { get; private set; }

    // Draws that waited for their port (pipelines: PipelineWaits)
    public long PortWaits { get; private set; }
    public long PipelineWaits => _pipelines.Waits;

    public int Building => _porting.Count + _pipelines.Building;
    public long Ticks { get; private set; }

    private Drawn? ProgramFor(int id, out string why)
    {
        why = "";
        if (id <= 0) return Refused(out why, "no program in use");
        if (_programs.TryGetValue(id, out var known)) return known;
        if (_unported.TryGetValue(id, out var failed)) return Refused(out why, failed);
        var name = ProgramPorts.Name(id);
        var program = _terrain.Find(id);
        if (program is null)
        {
            var ported = Ported(id, name, out var waiting, out var error);
            if (waiting) return null;
            if (ported is null || _programs.Count >= MaxScenePrograms)
            {
                var unported = $"{name} (no Vulkan port: {error})";
                if (_unported.Count < MaxUnported) _unported[id] = unported;
                return Refused(out why, unported);
            }

            // hot uniforms settle over the first draws unless the port has an earlier start's
            program = _terrain.Watch(name, id, ported, measuring: ported.HotUniforms.Size == 0);
        }

        var drawn = new Drawn(program, ProgramPorts.Points(id, program.Ported))
        {
            Settled = program.Ported.HotUniforms.Size > 0, // the terrain's own, ported with its hot uniforms, or cached ones
            Port = ++_ports, Sources = SourcesOf(id)
        };
        _programs[id] = drawn;
        Warm(drawn);
        return drawn;
    }

    // Read back here, ported on a builder: waiting until done
    private GlslPort.Ported? Ported(int id, string name, out bool waiting, out string error)
    {
        (waiting, error) = (false, "");
        if (!Assert(id > 0) || !Assert(_porting.Count <= MaxScenePrograms)) return null;
        if (!_porting.TryGetValue(id, out var porting))
        {
            var start = Hitches.Now;
            porting = ProgramPorts.Started(id, name, out error);
            Hitches.Since(Hitches.Kind.Port, start);
            if (porting is null) return null;
            _porting[id] = porting;
        }

        if (!porting.Done && !porting.Awaited(Hitches.Kind.Port)) // waits: the draw would be missing
        {
            (waiting, PortWaits) = (true, PortWaits + 1);
            return null;
        }

        _ = _porting.Remove(id);
        var ported = porting.Take(out error);
        _logger?.Notification("Komet: Vulkan port of {0}: {1}", name,
            ported is null ? error : $"{FrameClock.ToMs(porting.Ticks):0} ms off the engine's thread");
        return ported;
    }

    private static Drawn? Refused(out string why, string reason)
    {
        why = reason;
        _ = Assert(reason.Length > 0) && Assert(why.Length > 0);
        return null;
    }

    // The GL name may return for another program. keep: a port whose pipelines stay
    public void Forget(int id, int keep = 0)
    {
        if (!Assert(id >= 0) || !Assert(_programs.Count <= MaxScenePrograms)) return;
        _ = _unported.Remove(id);
        if (_porting.Remove(id, out var porting)) porting.Drop();
        if (!_programs.Remove(id, out var gone)) return;
        gone.Again?.Port.Drop();
        ForgetPipelines(id, keep);
        _terrain.Forget(id);
    }

    // OpenGL's picture is not shown, so the draw is missing
    private bool Leave(string why)
    {
        Left++;
        _asked = !GlTap.LaterRange; // a later range of a multi-draw: no Touched comes to take it
        if (NotNull(why)) _terrain.Miss(why);
        _ = Assert(Left > 0);
        return false;
    }

    public void BufferData(uint buffer, long size, IntPtr data, bool persistent)
    {
        if (Assert(size >= 0) && NotNull(Mirrors)) Mirrors.Data(buffer, size, data, persistent);
        if (persistent) _terrain.Maps.Specified(buffer, data != IntPtr.Zero);
    }

    public void BufferSubData(uint buffer, long offset, long size, IntPtr data)
    {
        if (Assert(offset >= 0) && Assert(size >= 0)) Mirrors.SubData(buffer, offset, size, data);
        _ = _terrain.PoolWritten(buffer, offset, size, data);
    }

    public void BufferCopy(uint from, uint to, long fromOffset, long toOffset, long size)
    {
        if (Assert(size >= 0) && Assert(fromOffset >= 0)) Mirrors.Copied(from, to, fromOffset, toOffset, size);
    }

    public void BufferUnseen(uint buffer, string why)
    {
        if (buffer > 0 && NotNull(Mirrors) && Assert(Mirrors.Count >= 0)) Mirrors.Unseen(buffer, why);
    }

    public void BuffersGone(ReadOnlySpan<uint> buffers)
    {
        if (Assert(buffers.Length < 1 << 20) && NotNull(Mirrors)) Mirrors.Gone(buffers);
        _terrain.BuffersGone(buffers);
    }

    public void TextureTuned(uint texture, uint name, float value)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain)) _terrain.Tuned((int)texture, name, value);
    }

    public bool TextureMipmapped(uint texture) =>
        texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain) && _terrain.Mipmapped((int)texture);

    public void TextureChanged(uint texture)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain)) _terrain.Changed((int)texture);
    }

    public void TextureImaged(uint texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, IntPtr pixels)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && Assert(levels.Level >= 0))
            _terrain.Imaged((int)texture, target, levels, spec, data, pixels);
    }

    public bool TextureSubImaged(uint texture, uint target, int level,
        (int X, int Y, int Z, int Width, int Height, int Depth) box, (uint Format, uint Type) data, IntPtr pixels) =>
        texture > 0 && Assert(texture < int.MaxValue) && Assert(target > 0) &&
        _terrain.SubImaged((int)texture, level, box, data, pixels);

    public bool TextureCopied((uint Texture, int Level, int X, int Y, int Z) to,
        (uint Texture, int Level, int X, int Y, int Z) from, (int Width, int Height, int Depth) size) =>
        to.Texture is > 0 and < int.MaxValue && Assert(from.Texture < int.MaxValue) && _terrain.Copied(to, from, size);

    public bool TextureRead(uint texture, int level, (int X, int Y, int Z) at, (int Fbo, int Read) from,
        (int X, int Y, int Width, int Height) rect) =>
        texture is > 0 and < int.MaxValue && Assert(level >= 0) && _terrain.FramebufferRead((int)texture, level, at, from, rect);

    public void Dispose()
    {
        _ = Assert(_programs.Count <= MaxScenePrograms) && _terrain.Device.WaitIdle();
        QueriesGone();
        _pipelines.Dispose();
        foreach (var porting in _porting.Values.ToArray().Bounded(MaxScenePrograms)) porting.Drop();
        foreach (var drawn in _programs.Values.ToArray().Bounded(MaxScenePrograms)) drawn.Again?.Port.Drop();
        _porting.Clear();
        _programs.Clear();
        Mirrors.Dispose();
        Constants.Dispose();
    }
}
