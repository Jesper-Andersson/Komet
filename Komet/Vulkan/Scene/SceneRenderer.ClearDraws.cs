namespace Komet.Vulkan;

// Vulkan-only: a masked clear (glColorMask/glStencilMask dropping held bits) is drawn as one full-target triangle:
// color through each attachment's mask, depth always-pass where cleared, stencil always-replace through the write mask,
// scissored like OpenGL's clear. Komet's own program, ported once.
internal sealed unsafe partial class SceneRenderer
{
    private const int ClearProgram = -1;
    private const string ClearVertex = """
        #version 450 core
        uniform float komet_depth;
        void main()
        {
            vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(corner * 2.0 - 1.0, komet_depth * 2.0 - 1.0, 1.0);
        }
        """;

    private const string ClearFragment = """
        #version 450 core
        uniform vec4 komet_color;
        layout(location = 0) out vec4 c0;
        layout(location = 1) out vec4 c1;
        layout(location = 2) out vec4 c2;
        layout(location = 3) out vec4 c3;
        layout(location = 4) out vec4 c4;
        layout(location = 5) out vec4 c5;
        layout(location = 6) out vec4 c6;
        layout(location = 7) out vec4 c7;
        void main()
        {
            c0 = komet_color; c1 = komet_color; c2 = komet_color; c3 = komet_color;
            c4 = komet_color; c5 = komet_color; c6 = komet_color; c7 = komet_color;
        }
        """;

    private GlslPort.Ported? _clearPort;
    private string _clearRefused = "";

    private bool ClearDrawn(VulkanFrame.Target target, GlTap.ClearCall call, (bool Colors, bool Depth, bool Stencil) clears,
        GlTap.Fixed gl)
    {
        if (!NotNull(target) || !Assert(_terrain.Frame.Number >= 0)) return false;
        if (ClearPort() is not { } port) return Leave($"a clear through a mask: no program to draw it ({_clearRefused})");
        var state = ClearState(target, call, clears, gl);
        var into = _terrain.Formats(target, ref state);
        var key = new PipelineKey(ClearProgram, 0, "clear", into.Key, state);
        var device = _terrain.Device;
        var pipeline = _pipelines.Get(key, ("Komet's clear", port),
            p => TerrainPipeline.Started(device, p, [], (state, into.Target, Vk.TopologyTriangles)), true, out _);
        if (pipeline is null) return Leave("a clear through a mask: no pipeline to draw it (see the log)");
        _sampled.Clear();
        if (!_terrain.Segment(target, _sampled, out var why)) return Leave(why);
        var frame = _terrain.Frame;
        frame.Viewport((0, 0, target.Width, target.Height));
        frame.Scissor((gl.Caps & (uint)GlTap.Caps.Scissor) != 0 ? GlTap.Scissor : null);
        var words = port.HotUniforms.Size / 4;
        var (_, _, push) = frame.Room(0, 0, words);
        Pushed(frame.PushWords(push, words), port.HotUniforms, call);
        if ((state.Caps & (uint)GlTap.Caps.Stencil) != 0) frame.Post(Stenciling(Replacing(call)));
        frame.Post(new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.Draw, Draw = VulkanFrame.DrawKind.Direct, Pipeline = pipeline.Pipeline,
            Layout = pipeline.Layout, Count = 3, Instances = 1, Push = push, PushBytes = words * 4
        });
        _terrain.Drawn(target);
        _terrain.Unprepared();
        Clears++;
        return Assert(words > 0);
    }

    private GlslPort.Ported? ClearPort()
    {
        if (_clearPort is not null || _clearRefused.Length > 0 || !Assert(_terrain.Frame.Number >= 0)) return _clearPort;
        _clearPort = GlslPort.Build("komet-clear", ClearVertex, ClearFragment, null, out var error,
            new HashSet<string>(["komet_color", "komet_depth"], StringComparer.Ordinal));
        if (_clearPort is null) _clearRefused = error.Length > 0 ? error : "?";
        return Assert(_clearPort is not null || _clearRefused.Length > 0) ? _clearPort : null;
    }

    // Color through each mask, no blending; depth always-pass, written where cleared; stencil replaced on both faces
    private static GlTap.Fixed ClearState(VulkanFrame.Target target, GlTap.ClearCall call,
        (bool Colors, bool Depth, bool Stencil) clears, GlTap.Fixed gl)
    {
        Span<uint> words = stackalloc uint[GlTap.MaxBuffers];
        words.Clear();
        for (var i = 0; clears.Colors && i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
            if (target.Colors[i] is not null && (call.Output < 0 || call.Output == i))
                words[i] = gl.Blend[i] & (FullMask << 22); // the write mask alone: blending off
        var stencil = clears.Stencil && target.Depth is { } depth && (depth.Format.Aspect & Vk.AspectStencil) != 0;
        const uint always = 7, replace = 2;
        var face = always | (replace << 3) | (replace << 6) | (replace << 9);
        var caps = (clears.Depth || stencil ? (uint)GlTap.Caps.DepthTest : 0) | (stencil ? (uint)GlTap.Caps.Stencil : 0);
        _ = Assert(face < 1u << 12) && Assert(words.Length == GlTap.MaxBuffers);
        return new GlTap.Fixed(caps, always | (clears.Depth ? 8u : 0), 0, 0,
            new GlTap.BlendState(words[0], words[1], words[2], words[3], words[4], words[5], words[6], words[7]),
            stencil ? face | (face << 12) : 0);
    }

    private static void Pushed(Span<uint> into, GlslPort.Block hot, GlTap.ClearCall call)
    {
        if (!NotNull(hot) || !Assert(into.Length * 4 >= hot.Size)) return;
        into.Clear();
        foreach (var member in hot.Members.Bounded(4))
        {
            var at = member.Offset / 4;
            if (member.Name == "komet_depth" && Index(at, into.Length))
                into[at] = BitConverter.SingleToUInt32Bits((float)Math.Clamp(call.Depth, 0, 1));
            else if (member.Name == "komet_color" && at + 3 < into.Length && at >= 0)
                (into[at], into[at + 1], into[at + 2], into[at + 3]) = (BitConverter.SingleToUInt32Bits(call.Color.R),
                    BitConverter.SingleToUInt32Bits(call.Color.G), BitConverter.SingleToUInt32Bits(call.Color.B),
                    BitConverter.SingleToUInt32Bits(call.Color.A));
        }
    }

    // Stencil clear value as both faces' reference, written through the front write mask (as OpenGL's clear)
    private static ((uint, uint, uint), (uint, uint, uint)) Replacing(GlTap.ClearCall call)
    {
        var write = GlTap.StencilValues.Front.Write;
        var reference = (uint)call.Stencil & 0xFF;
        _ = Assert(write <= 0xFF) && Assert(reference <= 0xFF);
        return ((reference, 0xFF, write), (reference, 0xFF, write));
    }
}
