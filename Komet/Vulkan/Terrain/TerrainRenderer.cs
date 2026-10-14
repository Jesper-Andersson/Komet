using System.Diagnostics;
using Komet.Chunks;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Every resource is Vulkan's own (TerrainRenderer.Only): adoption happens in Prepare or as pools are born. Dispose hands
// everything back: the engine keeps every GL name.
internal sealed partial class TerrainRenderer : IDisposable
{
    private const string AdoptMark = "komet-adopt";
    private const int MaxPipelines = 4096, MaxTargets = 64, MaxPrograms = 256, MaxShared = 32, MaxPools = 4096;

    private readonly PipelineSet<PipelineKey> _pipelines;
    private readonly Dictionary<(int Fbo, ulong Buffers, bool ReadOnly), VulkanFrame.Target> _targets = [];
    private readonly Dictionary<int, Program> _programs = []; // by GL program
    private readonly ILogger? _logger;
    private readonly HashSet<int> _refusedSwaps = [];

    private readonly record struct PipelineKey(int Program, string Layout, string Formats, GlTap.Fixed State);

    public sealed class Program(string name, int id, GlslPort.Ported ported)
    {
        public string Name { get; } = name;
        public int Id { get; } = id;
        public GlslPort.Ported Ported { get; } = ported;
        public UniformMirror Mirror { get; } = new();

        // Vulkan-only: ShaderCache key of its pipelines (PipelineRecipes), "" for none
        public string Sources { get; set; } = "";

        private readonly Dictionary<TextureSet, TerrainDraw.Program> _sets = [];
        private UniformMirror.Entry?[]? _units;

        // As Mirror.Int without a name lookup: Lay made the entries, the mirror never replaces one
        public int Unit(int at)
        {
            _units ??= [.. Ported.Samplers.Select(s => Mirror.EntryOf(s.Name))];
            return Index(at, _units.Length) && _units[at] is { Held.Length: > 0 } entry ? (int)entry.Held[0] : 0;
        }

        // Cached per texture set: textures are re-resolved on any OpenGL state change, nearly every pool draw
        public TerrainDraw.Program Recorded(ReadOnlySpan<TerrainDraw.Texture> textures)
        {
            _ = Assert(textures.Length <= Ported.Samplers.Length) && Assert(_sets.Count <= MaxSets);
            var set = textures.Length <= TextureSet.Max ? TextureSet.From(textures) : (TextureSet?)null;
            if (set is { } known && _sets.TryGetValue(known, out var seen)) return seen;
            var named = new Dictionary<string, TerrainDraw.Texture>(StringComparer.Ordinal);
            for (var i = 0; i < Math.Min(textures.Length, MaxSamplers); i++) named[Ported.Samplers[i].Name] = textures[i];
            var made = new TerrainDraw.Program(Ported, Mirror, named);
            if (set is not { } fresh) return made;
            if (_sets.Count >= MaxSets) _sets.Clear();
            _sets[fresh] = made;
            return made;
        }
    }

    // Four fields were too few for the chunk shaders: every draw built a dictionary and descriptors (~2 MB/s while
    // flying)
    public readonly struct TextureSet : IEquatable<TextureSet>
    {
        public const int Max = 8;
        private readonly int _count;
        private readonly Textures _items;

        private TextureSet(ReadOnlySpan<TerrainDraw.Texture> textures)
        {
            var items = new Textures();
            for (var i = 0; i < Math.Min(textures.Length, Max); i++) items[i] = textures[i];
            (_count, _items) = (textures.Length, items);
        }

        public static TextureSet From(ReadOnlySpan<TerrainDraw.Texture> textures) =>
            Assert(textures.Length <= Max) && Assert(Max > 0) ? new TextureSet(textures) : default;

        public bool Equals(TextureSet other)
        {
            if (_count != other._count || !Assert(_count <= Max)) return false;
            for (var i = 0; i < Math.Min(_count, Max); i++)
                if (_items[i] != other._items[i]) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is TextureSet other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_count);
            for (var i = 0; i < Math.Min(_count, Max); i++) hash.Add(_items[i]);
            return Assert(_count <= Max) ? hash.ToHashCode() : 0;
        }

        public static bool operator ==(TextureSet left, TextureSet right) => left.Equals(right);

        public static bool operator !=(TextureSet left, TextureSet right) => !left.Equals(right);

        [System.Runtime.CompilerServices.InlineArray(Max)]
        private struct Textures
        {
            private TerrainDraw.Texture _first;
        }
    }

    private TerrainRenderer(VulkanDevice device, (HostBuffer Quads, VulkanFrame Frame) made, ILogger? logger)
    {
        var (quads, frame) = made;
        _ = Assert(quads.Size > 0) && NotNull(frame);
        (Device, Quads, Frame, Recorder, _logger) = (device, quads, frame, new TerrainDraw(quads), logger);
        _pipelines = new PipelineSet<PipelineKey>(device, frame, logger, MaxPipelines);
        (Pools, Targets) = (new TerrainPools(device), new TerrainTargets(device));
        CompactFaces.Active = Pools.Compact;
        Sampling = new Samplers(device.Handle);
        frame.Images = Handed;
        frame.Filling = Filled;
        GlTap.Deleting = Deleted;
        GlTap.FramebufferGone = Unattached;
        (Images, Maps) = (new TextureImages(device, frame), new PersistentMaps(device));
        (Targets.Claimed, Pools.Mapped) = (Images.Forget, Maps.Of);
        (GlTap.Mapping, GlTap.FramebufferChanged, GlTap.Unmapping) = (Maps.Map, Unattached, Maps.Unmapped);
    }

    // Map tile framebuffers are made and deleted per tile; a reused name must not keep the old tile's state
    private void Unattached(uint framebuffer)
    {
        var fbo = (int)framebuffer;
        if (!Assert(fbo > 0) || !_attached.Remove(fbo)) return;
        var targeted = _lastTarget.Fbo == fbo;
        using var targets = _targets.Keys.GetEnumerator();
        for (var i = 0; i < MaxTargets && !targeted && targets.MoveNext(); i++) targeted = targets.Current.Fbo == fbo;
        if (targeted) ForgetTargets();
    }

    // The targets' images, held by every segment from its start
    private List<VulkanFrame.Shared> Handed()
    {
        if (Targets.Version == _handedAt && _handed is { } known) return known;
        _handed = [.. Targets.Resting];
        _handedAt = Targets.Version;
        _ = Assert(_handed.Count <= VulkanFrame.MaxImages) && Assert(_handedAt >= 0);
        return _handed;
    }

    private List<VulkanFrame.Shared>? _handed;
    private long _handedAt = -1;

    // Into the open segment's setup
    private void Filled()
    {
        if (Frame.Open) Images.Filled(Frame.Setup);
        _ = Assert(Frame.Number >= 0) && Assert(Frame.Open || Frame.Setup == IntPtr.Zero);
    }

    private void Deleted(uint texture)
    {
        Images.Forget((int)texture);
        if (Assert(texture > 0) && Targets.Find((int)texture) is not null) Unshared((int)texture);
    }

    public VulkanDevice Device { get; }
    public HostBuffer Quads { get; private set; }
    public VulkanFrame Frame { get; }
    public TerrainDraw Recorder { get; private set; }
    public TerrainPools Pools { get; }
    public TerrainTargets Targets { get; }
    public Samplers Sampling { get; }
    public IReadOnlyCollection<Program> Programs => _programs.Values;

    public VulkanBackend Compute => _compute ??= new VulkanBackend(Device, Frame, SampledFind);

    private VulkanBackend? _compute;

    public long Draws { get; private set; }

    // quads: index pattern size
    public static TerrainRenderer? Create(VulkanDevice device, int quads, ILogger? logger, out string why)
    {
        why = "";
        if (!NotNull(device) || !Assert(quads > 0)) return null;
        var pattern = TerrainDraw.Quads(device, quads);
        if (pattern is null)
        {
            why = "no memory for the quad index pattern";
            return null;
        }

        var frame = VulkanFrame.Create(device, out why);
        if (frame is not null) return new TerrainRenderer(device, (pattern, frame), logger);
        pattern.Dispose();
        return null;
    }

    // measuring: count uniforms changing between draws (scene programs not yet settled)
    public Program Watch(string name, int program, GlslPort.Ported ported, bool measuring = false)
    {
        _ = Assert(program > 0) && NotNull(ported);
        Forget(program);
        var made = new Program(name, program, ported);
        made.Mirror.Measuring = measuring;
        made.Mirror.Lay(ported.VertexUniforms, ported.FragmentUniforms, ported.HotUniforms,
            ported.Samplers.Select(s => s.Name));
        if (_programs.Count < MaxPrograms) _programs[program] = made;
        GlTap.Watch(program, ported, made.Mirror);
        return made;
    }

    public bool Ours(int fbo) =>
        Assert(fbo >= 0) && fbo > 0 && Targets.Of(fbo) is { } framebuffer && Targets.Swapped(framebuffer);

    // Engine framebuffers on Vulkan's images, plus (while the scene hears every OpenGL call) unlisted ones such as
    // Komet's distant shadow map via their textures' images; never an engine framebuffer left out
    public bool Takes(int fbo) =>
        Ours(fbo) || (fbo > 0 && GlTap.Touching is not null && Targets.Of(fbo) is null && Assert(GlTap.Scene));

    public Program? Find(int program) => Assert(program >= 0) ? _programs.GetValueOrDefault(program) : null;

    public void Forget(int program)
    {
        if (!Assert(program >= 0) || !_programs.Remove(program)) return;
        GlTap.Unwatch(program);
        _context = null;
        _pipelines.Forget(key => key.Program == program); // once no frame in flight uses them: no handoff, no wait
    }

    // Unheard, the engine may have deleted a texture and reused its name
    public void Rewatch()
    {
        foreach (var image in Targets.Images.ToArray().Bounded(MaxShared * 4))
            if (Stale(image.Texture, image))
                Unshared(image.Texture);
        ForgetTargets();
        foreach (var program in _programs.Values.ToArray().Bounded(MaxPrograms))
            GlTap.Watch(program.Id, program.Ported, program.Mirror);
        _ = Assert(GlTap.Tapped) && Assert(_programs.Count <= MaxPrograms);
    }

    public bool Start(IReadOnlyList<FrameBufferRef> framebuffers, ReadOnlySpan<int> shared, out string why)
    {
        why = "";
        if (!NotNull(framebuffers)) return false;
        Images.Started(); // a world's uploads before its first frame
        Frame.Fresh();
        CollectSnapshots(false);
        Maps.Collect(Frame.Done);
        using var quiet = GlTap.Quietly(); // asking framebuffers' textures is Komet's own OpenGL work
        _farRetired.Collect(Frame.Done);
        Pools.Collect(Frame.Done); // released pools' buffers (their GL buffers too: Komet's own)
        Targets.Framebuffers = framebuffers;
        for (var i = 0; i < Math.Min(shared.Length, MaxShared); i++)
        {
            var at = shared[i];
            // Size from its textures (the engine leaves Width/Height 0 in some, e.g. BlurVerticalLowRes)
            if (at >= framebuffers.Count || framebuffers[at] is not { FboId: > 0 } framebuffer ||
                Targets.Swapped(framebuffer) || (framebuffer.ColorTextureIds is not { Length: > 0 } &&
                                                 framebuffer.DepthTextureId <= 0)) continue;
            ForgetTargets();
            if (Targets.Swap(framebuffer, out var refused)) continue;
            if (i == 0)
            {
                why = refused;
                return false;
            }

            if (_refusedSwaps.Add(framebuffer.FboId))
                _logger?.Notification("Komet: Vulkan leaves framebuffer {0} to OpenGL: {1}", framebuffer.FboId, refused);
        }

        if (Targets.Replaced.Count > 0)
            _logger?.Notification("Komet: Vulkan stands for {0}", string.Join("; ", Targets.Replaced));
        Targets.Replaced.Clear();
        return Assert(Frame.Number > 0);
    }

    // Adoption is Before-stage chunk work (ChunkBudget.Spent)
    public void Prepare(List<(VAO Vao, bool Classic)> pools)
    {
        if (!NotNull(pools)) return;
        using var quiet = GlTap.Quietly();
        var start = Stopwatch.GetTimestamp();
        _ = Assert(start > 0);
        if (Pools.Adopt(pools, out var why) > 0) ChunkBudget.Spent(Stopwatch.GetTimestamp() - start);
        Vintagestory.Client.ScreenManager.FrameProfiler?.Mark(AdoptMark);
        if (why.Length > 0) _ = Leave(why);
        _ = Assert(Pools.Count <= MaxPools);
    }

    // Before its first write; false leaves it to Prepare
    public bool Born(VAO vao, bool classic)
    {
        if (!NotNull(vao) || !Assert(vao.VaoId > 0)) return false;
        using var quiet = GlTap.Quietly();
        return Pools.Adopt(vao, classic, out _, fresh: true) is not null;
    }

    private static bool Stale(int texture, SharedImage image)
    {
        if (!Assert(texture > 0) || !NotNull(image)) return true;
        if (!GlTexture.Exists(texture)) return true;
        var now = GlTexture.Of(texture);
        return now.Width != image.Width || now.Height != image.Height;
    }

    public void Unshared(int texture)
    {
        if (Targets.Find(texture) is null || !Assert(texture > 0)) return;
        Frame.Close("a shared texture deleted");
        ForgetTargets();
        _ = Device.WaitIdle(); // a frame in flight may still use it
        using var quiet = GlTap.Quietly();
        Targets.Forget(texture);
    }

    public void Release(VAO vao)
    {
        if (!NotNull(vao) || Pools.Find(vao) is not { } pool || !Assert(_programs.Count <= MaxPrograms)) return;
        _ = _poolCopies.RemoveAll(c => ReferenceEquals(c.To, pool.Faces) || ReferenceEquals(c.To, pool.Indices) ||
                                       Array.IndexOf(pool.Vertices, c.To) >= 0); // nothing more is copied into them
        _context = null;
        using var quiet = GlTap.Quietly();
        Pools.Release(vao, Frame.Number); // what this frame recorded still reads them: they go once it is done
    }

    public void Release(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer) || !Assert(_targets.Count <= MaxTargets)) return;
        Frame.Close("a framebuffer disposed");
        ForgetTargets();
        using var quiet = GlTap.Quietly();
        Targets.Release(framebuffer);
    }

    public void ForgetTargets()
    {
        _targets.Clear();
        _attached.Clear();
        _formats.Clear();
        (_context, _lastTarget) = (null, default);
        _ = Assert(_targets.Count == 0);
    }

    public void Dispose()
    {
        CompactFaces.Active = false;
        if (GlTap.Deleting == Deleted) GlTap.Deleting = null;
        if (GlTap.FramebufferGone == Unattached) GlTap.FramebufferGone = null;
        (GlTap.Mapping, GlTap.FramebufferChanged, GlTap.Unmapping) = (null, null, null);
        Unwindowed(); // the real window again before anything else goes
        Frame.Close("Vulkan let go");
        using var quiet = GlTap.Quietly(); // handing the pools and framebuffers back is Komet's own OpenGL work
        _ = Assert(_pipelines.Count <= MaxPipelines) && Device.WaitIdle();
        Unfar(true);
        _compute?.Dispose(); // what the passes still hold goes with it; their handles name nothing any more
        _compute = null;
        foreach (var program in _programs.Keys.ToArray().Bounded(MaxPrograms)) GlTap.Unwatch(program);
        _programs.Clear();
        Frame.Dispose();
        Targets.Dispose();
        Pools.Dispose();
        Maps.Dispose(); // after the pools: what OpenGL draws from again, the engine's pointers pointed back
        Images.Dispose();
        CollectSnapshots(true);
        _poolStaging?.Dispose();
        Sampling.Dispose();
        _blank?.Dispose();
        _pipelines.Dispose();
        Quads.Dispose();
    }
}
