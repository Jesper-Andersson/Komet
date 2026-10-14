using System.Numerics;

namespace Komet.Vulkan;

// Vulkan-only's far cache laid into the frame (TerrainRenderer.Far): one full-target triangle at the opaque terrain's end,
// depth-tested against what the camera's pass drew. Each pixel looks up where its direction lay in the cached view (the
// cache was drawn from nearly here: a turn of the camera is exact, a step of up to FarMoved is corrected by one
// parallax step); what lies past the cut is written as the engine's opaque pass writes it: colour and glow as drawn, the normal
// and the position turned and moved into this frame's view, the depth from that position.
internal sealed unsafe partial class SceneRenderer
{
    private const int FarProgram = -2;
    private const string FarVertex = """
        #version 450 core
        void main()
        {
            vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    // The m rows turn this frame's view directions into the cached view's, the n rows turn back (positions with the shift
    // added). The lens holds P0, P5, P8 and P9, the depth P10, P14 and the cut's nearest distance, the size one over the
    // target's width and height.
    private const string FarFragment = """
        #version 450 core
        uniform sampler2D komet_farColor;
        uniform sampler2D komet_farGlow;
        uniform sampler2D komet_farNormal;
        uniform sampler2D komet_farPosition;
        uniform sampler2D komet_farDepth;
        uniform vec4 komet_m0, komet_m1, komet_m2, komet_n0, komet_n1, komet_n2, komet_lens, komet_depth, komet_shift, komet_size;
        layout(location = 0) out vec4 c0;
        layout(location = 1) out vec4 c1;
        layout(location = 2) out vec4 c2;
        layout(location = 3) out vec4 c3;

        vec3 turned(vec4 r0, vec4 r1, vec4 r2, vec3 v) { return vec3(dot(r0.xyz, v), dot(r1.xyz, v), dot(r2.xyz, v)); }

        // the cached view's position to its NDC, and an NDC with the cached depth there back to a position
        vec2 projected(vec3 p) { return vec2(komet_lens.x * p.x + komet_lens.z * p.z, komet_lens.y * p.y + komet_lens.w * p.z) / -p.z; }

        vec3 unprojected(vec2 at, float depth)
        {
            float z = -komet_depth.y / (depth * 2.0 - 1.0 + komet_depth.x);
            return vec3((at.x + komet_lens.z) / komet_lens.x * -z, (at.y + komet_lens.w) / komet_lens.y * -z, z);
        }

        bool inside(vec2 at) { return all(greaterThanEqual(at, vec2(-1.0))) && all(lessThanEqual(at, vec2(1.0))); }

        void main()
        {
            vec2 ndc = gl_FragCoord.xy * komet_size.xy * 2.0 - 1.0;
            vec3 ray = vec3((ndc.x + komet_lens.z) / komet_lens.x, (ndc.y + komet_lens.w) / komet_lens.y, -1.0);
            vec3 look = turned(komet_m0, komet_m1, komet_m2, ray);
            if (look.z > -1e-4) discard;
            vec2 at = projected(look);
            if (!inside(at)) discard;
            // one step against the parallax of the eye's move: what the turned lookup found, put on this pixel's ray at
            // its depth now, looked up again
            float depth = texture(komet_farDepth, at * 0.5 + 0.5).r;
            if (depth >= 1.0) discard; // nothing drawn there
            vec3 now = turned(komet_n0, komet_n1, komet_n2, unprojected(at, depth)) + komet_shift.xyz;
            vec3 back = turned(komet_m0, komet_m1, komet_m2, ray * -now.z - komet_shift.xyz);
            if (back.z > -1e-3) discard;
            at = projected(back);
            if (!inside(at)) discard;
            vec2 from = at * 0.5 + 0.5;
            depth = texture(komet_farDepth, from).r;
            if (depth >= 1.0) discard;
            vec3 seen = unprojected(at, depth);
            if (length(seen) < komet_depth.z) discard; // nearer than the cut: the camera's own pass draws it
            now = turned(komet_n0, komet_n1, komet_n2, seen) + komet_shift.xyz;
            if (now.z > -1e-3) discard;
            gl_FragDepth = (komet_depth.x * now.z + komet_depth.y) / -now.z * 0.5 + 0.5;
            c0 = texture(komet_farColor, from);
            c1 = texture(komet_farGlow, from);
            vec4 normal = texture(komet_farNormal, from);
            c2 = vec4(turned(komet_n0, komet_n1, komet_n2, normal.xyz), normal.w);
            c3 = vec4(now, texture(komet_farPosition, from).w);
        }
        """;

    private static readonly string[] FarHot =
        ["komet_m0", "komet_m1", "komet_m2", "komet_n0", "komet_n1", "komet_n2", "komet_lens", "komet_depth", "komet_shift", "komet_size"];

    private static readonly string[] FarSamplers =
        ["komet_farColor", "komet_farGlow", "komet_farNormal", "komet_farPosition", "komet_farDepth"];

    // The warp's terms, in FarHot's order
    public readonly record struct FarWarp(Matrix4x4 Into, Matrix4x4 Back, Vector4 Lens, Vector4 Depth, Vector3 Shift);

    private GlslPort.Ported? _farPort;
    private readonly SharedImage?[] _farImages = new SharedImage?[5];

    // FarBegin's and FarEnd's callbacks, made once: a method group handed over per frame is a new delegate
    public System.Func<VulkanFrame.Target, bool> FarReadied => field ??= FarReady;
    public System.Func<VulkanFrame.Target, VulkanFrame.Target, FarWarp, bool> FarLay => field ??= FarLaid;
    private string _farRefused = "";

    // The program and pipeline for the target made ahead: a frame that culls its far rows must be able to lay the cache in
    public bool FarReady(VulkanFrame.Target target)
    {
        if (!NotNull(target) || !Assert(_terrain.Frame.Number >= 0) || FarPort() is not { } port) return false;
        var state = FarState(target);
        var into = _terrain.Formats(target, ref state);
        return FarPipeline(port, state, into) is not null;
    }

    private TerrainPipeline? FarPipeline(GlslPort.Ported port, GlTap.Fixed state, (TerrainPipeline.Target Target, string Key) into)
    {
        var device = _terrain.Device;
        _ = NotNull(port) && Assert(into.Key.Length > 0);
        return _pipelines.Get(new PipelineKey(FarProgram, 0, "far", into.Key, state), ("Komet's far cache", port),
            p => TerrainPipeline.Started(device, p, [], (state, into.Target, Vk.TopologyTriangles)), true, out _);
    }

    // far: the cache's target (colours as the primary's, its depth); false when nothing could be laid in
    public bool FarLaid(VulkanFrame.Target primary, VulkanFrame.Target far, FarWarp warp)
    {
        if (!NotNull(primary) || !NotNull(far) || far.Depth is not { } farDepth || !Assert(_terrain.Frame.Number >= 0)) return false;
        if (FarPort() is not { } port) return Leave($"the far cache: no program ({_farRefused})");
        var state = FarState(primary);
        var into = _terrain.Formats(primary, ref state);
        if (FarPipeline(port, state, into) is not { } pipeline) return Leave("the far cache: no pipeline (see the log)");
        _sampled.Clear();
        var colors = far.Colors;
        var images = _farImages;
        for (var i = 0; i < 4; i++) images[i] = i < colors.Length ? colors[i] ?? colors[0] : colors[0]; // without SSAO: a stand-in
        images[4] = farDepth;
        foreach (var image in images.Bounded(5))
            if (image is not null && !_sampled.Contains(image))
                _sampled.Add(image);
        if (images[0] is null) return Leave("the far cache: a layer without colours");
        if (!_terrain.Segment(primary, _sampled, out var why)) return Leave(why);
        var frame = _terrain.Frame;
        frame.Viewport((0, 0, primary.Width, primary.Height));
        frame.Scissor(null);
        var resources = pipeline.Resources;
        var words = port.HotUniforms.Size / 4;
        var (at, _, push) = frame.Room(resources.Length, 0, words);
        var descriptors = frame.Descriptors(at, resources.Length);
        var sampler = _terrain.Sampling.Get(new Samplers.State(false, false, 0, true, false));
        for (var i = 0; i < Math.Min(resources.Length, 8); i++)
        {
            var r = resources[i];
            var name = port.Samplers.FirstOrDefault(s => s.Binding == r.Binding)?.Name;
            var image = name is null ? null : images[Math.Clamp(Array.IndexOf(FarSamplers, name), 0, 4)];
            if (r.Type != Vk.CombinedSampler || image is null) return Leave("the far cache: a resource its program did not ask for");
            descriptors[i] = new VulkanFrame.Descriptor
            {
                Binding = (uint)r.Binding, Type = r.Type,
                Image = new Vk.ImageDescriptor { Sampler = sampler, View = image.Sampled, Layout = Vk.LayoutShaderRead }
            };
        }

        FarPushed(frame.PushWords(push, words), port.HotUniforms, warp, (1f / primary.Width, 1f / primary.Height));
        frame.Post(new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.Draw, Draw = VulkanFrame.DrawKind.Direct, Pipeline = pipeline.Pipeline, Layout = pipeline.Layout,
            Count = 3, Instances = 1, Descriptors = at, DescriptorCount = resources.Length, Push = push, PushBytes = words * 4
        });
        _terrain.Drawn(primary);
        _terrain.Unprepared();
        return Assert(words > 0) && Assert(resources.Length == 5);
    }

    private GlslPort.Ported? FarPort()
    {
        if (_farPort is not null || _farRefused.Length > 0 || !Assert(_terrain.Frame.Number >= 0)) return _farPort;
        _farPort = GlslPort.Build("komet-far-cache", FarVertex, FarFragment, null, out var error,
            new HashSet<string>(FarHot, StringComparer.Ordinal));
        if (_farPort is null) _farRefused = error.Length > 0 ? error : "?";
        if (_farPort is null) _logger?.Warning("Komet: the far cache's program does not port: {0}", _farRefused);
        return Assert(_farPort is not null || _farRefused.Length > 0) ? _farPort : null;
    }

    // Every colour written whole, no blending; depth tested LESS and written
    private static GlTap.Fixed FarState(VulkanFrame.Target target)
    {
        const uint less = 1, write = 8;
        Span<uint> words = stackalloc uint[GlTap.MaxBuffers];
        words.Clear();
        for (var i = 0; i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
            if (target.Colors[i] is not null)
                words[i] = FullMask << 22;
        _ = Assert(target.Depth is not null) && Assert(words.Length == GlTap.MaxBuffers);
        return new GlTap.Fixed((uint)GlTap.Caps.DepthTest, less | write, 0, 0,
            new GlTap.BlendState(words[0], words[1], words[2], words[3], words[4], words[5], words[6], words[7]), 0);
    }

    private static void FarPushed(Span<uint> into, GlslPort.Block hot, FarWarp warp, (float X, float Y) size)
    {
        if (!NotNull(hot) || !Assert(into.Length * 4 >= hot.Size)) return;
        into.Clear();
        Span<Vector4> values =
        [
            Row(warp.Into, 0), Row(warp.Into, 1), Row(warp.Into, 2), Row(warp.Back, 0), Row(warp.Back, 1), Row(warp.Back, 2),
            warp.Lens, warp.Depth, new Vector4(warp.Shift, 0), new Vector4(size.X, size.Y, 0, 0)
        ];
        foreach (var member in hot.Members.Bounded(16))
        {
            var (at, which) = (member.Offset / 4, Array.IndexOf(FarHot, member.Name));
            if (!Index(which, values.Length) || at < 0 || at + 3 >= into.Length) continue;
            var v = values[which];
            (into[at], into[at + 1], into[at + 2], into[at + 3]) = (BitConverter.SingleToUInt32Bits(v.X),
                BitConverter.SingleToUInt32Bits(v.Y), BitConverter.SingleToUInt32Bits(v.Z), BitConverter.SingleToUInt32Bits(v.W));
        }
    }

    private static Vector4 Row(Matrix4x4 m, int row) => row switch
    {
        0 => new Vector4(m.M11, m.M12, m.M13, 0),
        1 => new Vector4(m.M21, m.M22, m.M23, 0),
        _ => Assert(row == 2) ? new Vector4(m.M31, m.M32, m.M33, 0) : Vector4.Zero
    };
}
