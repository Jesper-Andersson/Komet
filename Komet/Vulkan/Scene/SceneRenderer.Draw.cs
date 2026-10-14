using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// A scene draw recorded in the open segment; what it cannot take is missing from the picture (Leave).
internal sealed partial class SceneRenderer
{
    private const int MaxLayouts = 4096, MaxInputs = 16, MaxSamplers = 32, MaxPipelines = 4096, MaxBlocks = 32;
    private const uint UnsignedInt = 0x1405, UnsignedShort = 0x1403, UnsignedByte = 0x1401, GlFloat = 0x1406, GlInt = 0x1404;
    private const uint GlPoints = 0;
    private const int LayoutDepthReadOnly = 4;
    private const uint GlLineLoop = 2, GlLine = 0x1B01, FirstLogicOp = 0x1500;

    private readonly Dictionary<(GlTap.VertexArray Array, long Changes, int Program), Layout?> _layouts = [];
    private readonly Dictionary<string, Formats> _formats = new(StringComparer.Ordinal);
    private readonly PipelineSet<PipelineKey> _pipelines;
    private readonly Dictionary<QuickKey, TerrainPipeline?> _quick = [];
    private readonly List<SharedImage> _sampled = [];
    private readonly List<(int Unit, uint Kind, uint Texture, int Sampler)> _bound = [];
    private readonly List<TerrainDraw.BlockBuffer> _blocks = [];
    private readonly List<(uint Location, ulong Buffer, ulong Offset)> _vertices = [];
    private readonly TerrainDraw.Texture[] _resolved = new TerrainDraw.Texture[MaxSamplers];

    private sealed record Layout(Formats Formats, (int Location, uint Buffer, long Offset)[] Sources);

    // One object per distinct format set, compared by reference
    private sealed record Formats(string Key, TerrainPools.Attribute[] Attributes);

    // Port (Drawn.Port): the next port's pipelines are made beside those in use
    private readonly record struct PipelineKey(int Program, int Port, string Inputs, string Target, GlTap.Fixed State);

    // Program, formats and target by reference (each made once), topology and state by value
    private readonly record struct QuickKey(Drawn Used, Formats Inputs, uint Mode, VulkanFrame.Target Into,
        GlTap.Fixed Held)
    {
        public bool Equals(QuickKey other) =>
            Assert(Used is not null) && Assert(other.Used is not null) && ReferenceEquals(Used, other.Used) && ReferenceEquals(Inputs, other.Inputs) &&
            Mode == other.Mode && ReferenceEquals(Into, other.Into) && Held.Equals(other.Held);

        public override int GetHashCode() =>
            Assert(Inputs is not null) && Assert(Used is not null)
                ? HashCode.Combine(RuntimeHelpers.GetHashCode(Used), RuntimeHelpers.GetHashCode(Inputs), Mode,
                    RuntimeHelpers.GetHashCode(Into), Held.Caps, Held.Depth, Held.Blend.B0)
                : 0;
    }

    public bool Draw(GlTap.DrawCall call)
    {
        if (!Framing || !Assert(call.Instances >= 0) || !Assert(call.Count >= 0)) return false;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var id = GlTap.Program;
        var known = id > 0 ? _programs.GetValueOrDefault(id) : null; // looked up once a draw
        _ = Assert(known is null || known.Program.Id == id);
        var taken = Drawable(call, (id, known), out var why) && Taken(call, known, out why);
        if (!taken && why.Length > 0) _ = Leave(why);
        else if (!taken) Waited(); // its port or pipeline still being made
        Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        return taken;
    }

    private bool Drawable(GlTap.DrawCall call, (int Id, Drawn? Known) program, out string why)
    {
        why = "";
        var id = program.Id;
        // not the scene's: no reason to report
        if (id <= 0 || !Takes(id, program.Known?.Name ?? ProgramPorts.Name(id))) return false;
        // points (their size written by the port, GlslPort.PointSized), 8-bit indices, indirect draws
        if (Topology(call.Mode) > Vk.TopologyTriangleFan) return Refuse(out why, $"draw mode 0x{call.Mode:X}");
        if (call.IndexType is not (0 or UnsignedInt or UnsignedShort) &&
            (call.IndexType != UnsignedByte || !_terrain.Device.IndexUint8))
            return Refuse(out why, $"index type 0x{call.IndexType:X}");
        if (call.Mode == GlLineLoop && call.IndexType == UnsignedByte) return Refuse(out why, "an indexed line loop");
        if (call.Indirect is { } indirect && Unindirect(call, indirect) is { Length: > 0 } refused) return Refuse(out why, refused);
        return Assert(call.Count >= 0) && Assert(why.Length == 0);
    }

    private bool Taken(GlTap.DrawCall call, Drawn? known, out string why)
    {
        why = "";
        if (call.Count == 0 || call.Instances == 0) return Assert(call.Count >= 0); // OpenGL would draw nothing either
        var program = Current(known ?? ProgramFor(GlTap.Program, out why));
        if (program is null) return false;
        if (GlTap.BoundArray is not { } array) return Refuse(out why, "no vertex array bound");
        var gl = GlTap.State;
        if ((gl.Caps & (uint)GlTap.Caps.Discard) != 0) return true; // rasterizer discard: OpenGL draws nothing
        if (Rasterized(call.Mode, gl, out why) is not { } rasterized) return false;
        var stencils = (rasterized.Caps & (uint)GlTap.Caps.Stencil) != 0; // may write the stencil, never read-only
        var target = _terrain.Target(rasterized, out var refused, depthReadOnly: stencils ? false : null, foreign: true);
        if (target is null) return Refuse(out why, $"{program.Name} {refused}");
        var state = Stenciled(rasterized, target);
        var recorded = Sampled(program, target, out why);
        if (recorded is not null) recorded = Unfed(program, recorded, target, out why);
        if (recorded is null) return false;
        var layout = LayoutFor(array, program);
        if (layout is null) return Refuse(out why, $"{program.Name}'s vertex inputs have no Vulkan format here");
        var topology = Topology(call.Mode);
        var pipeline = Pipeline(program, layout, (topology, target, state), out var waiting);
        if (pipeline is null)
            return Refuse(out why, waiting ? "" : $"no pipeline for {program.Name} in this state (see the log)");
        if (!_terrain.Segment(target, _sampled, out why)) return false;
        if (!Buffers(program, (array, layout), call, out var indices, out why)) return false;
        if (!Recorded(call, (pipeline, recorded, indices), (target, state), out why)) return false;
        Settle(program);
        return true;
    }

    // Alpha to coverage dropped (one sample, OpenGL makes none either),
    // as is polygon line smoothing; polygon mode and logic op as own caps where the device has them
    private GlTap.Fixed? Rasterized(uint mode, GlTap.Fixed state, out string why)
    {
        why = "";
        var device = _terrain.Device;
        var caps = state.Caps & ~(uint)GlTap.Caps.AlphaToCoverage;
        var wire = GlTap.PolygonMode != GlTap.Fill && Topology(mode) >= Vk.TopologyTriangles;
        if (wire && !device.NonSolidFill) return Unrasterized(out why, "wireframe, which the device does not draw");
        if (wire) caps |= (uint)(GlTap.PolygonMode == GlLine ? GlTap.Caps.WireLines : GlTap.Caps.WirePoints);
        var lined = Lines(mode) || (caps & (uint)GlTap.Caps.WireLines) != 0;
        if (!lined) caps &= ~(uint)GlTap.Caps.LineSmooth;
        if ((caps & (uint)GlTap.Caps.LineSmooth) != 0 && !device.SmoothLines)
            caps &= ~(uint)GlTap.Caps.LineSmooth; // plain lines rather than none (OpenGL's smoothing is a hint too)
        if ((caps & (uint)GlTap.Caps.LogicOp) != 0 && !device.LogicOps)
            return Unrasterized(out why, "a logic operation, which the device does not do");
        if ((caps & (uint)GlTap.Caps.LogicOp) != 0) caps |= ((GlTap.LogicOperation - FirstLogicOp) & 15) << 12;
        return state with { Caps = caps };
    }

    private static GlTap.Fixed? Unrasterized(out string why, string reason)
    {
        why = reason;
        _ = Assert(reason.Length > 0) && Assert(why.Length > 0);
        return null;
    }

    private bool Recorded(GlTap.DrawCall call,
        (TerrainPipeline Pipeline, TerrainDraw.Program Program, (ulong Buffer, ulong Offset, uint Type)? Indices) with,
        (VulkanFrame.Target Target, GlTap.Fixed State) into, out string why)
    {
        why = "";
        var frame = _terrain.Frame;
        if (frame.Uploads is not { } uploads || !NotNull(with.Pipeline) || !Assert(with.Pipeline.Layout != 0))
            return Refuse(out why, "no uploads");
        frame.Viewport(GlTap.Viewport);
        frame.Scissor((into.State.Caps & (uint)GlTap.Caps.Scissor) != 0 ? GlTap.Scissor : null);
        if (Lines(call.Mode) || (into.State.Caps & (uint)GlTap.Caps.WireLines) != 0)
            frame.Post(new VulkanFrame.Op
            {
                Kind = VulkanFrame.OpKind.LineWidth, X = _terrain.Device.WideLines ? Math.Clamp(GlTap.LineWidth, 1, 8) : 1
            });
        var op = new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.Draw, Draw = VulkanFrame.DrawKind.Direct, Pipeline = with.Pipeline.Pipeline,
            Layout = with.Pipeline.Layout, Count = (uint)call.Count, Instances = (uint)call.Instances,
            First = (uint)call.First, BaseInstance = call.BaseInstance, BaseVertex = call.BaseVertex
        };
        if (with.Indices is { } indices)
            (op.Draw, op.Index, op.IndexOffset, op.IndexType) =
                (VulkanFrame.DrawKind.Indexed, indices.Buffer, indices.Offset, indices.Type);
        if (call.Mode == GlLineLoop && !(op.Draw == VulkanFrame.DrawKind.Indexed ? LoopedIndices(uploads, ref op) : Looped(uploads, ref op)))
            return Refuse(out why, "no room for a line loop's indices");
        if (call.Indirect is { } indirect && !Indirected(ref op, indirect)) return Refuse(out why, "an indirect draw's commands past their buffer");
        if (!TerrainDraw.Filled(frame, (uploads, with.Pipeline, with.Program), ref op, null,
                CollectionsMarshal.AsSpan(_blocks)))
            return Refuse(out why, "the recorder refused the draw (a descriptor without its resource)");
        TerrainDraw.Bound(frame, CollectionsMarshal.AsSpan(_vertices), ref op);
        _terrain.Drawn(into.Target);
        _terrain.Unprepared();
        if (into.State.Stencil != 0) frame.Post(Stenciling(GlTap.StencilValues));
        var query = QueryBegin();
        Posted(frame, op);
        QueryEnd(query);
        Draws++;
        return true;
    }

    private static bool Refuse(out string why, string reason)
    {
        why = reason;
        _ = Assert(reason is not null) && Assert(why is not null);
        return false;
    }

    // Line loop as line strip: indices 0..count - 1 then 0
    private static bool Looped(HostBuffer uploads, ref VulkanFrame.Op op)
    {
        var count = (int)op.Count;
        if (!Assert(count is > 0 and < 1 << 24) || uploads.Take(4 * (count + 1), out var into, 16) is var at && at < 0)
            return false;
        var indices = MemoryMarshal.Cast<byte, uint>(into);
        for (var i = 0; i < Math.Min(count, 1 << 24); i++) indices[i] = (uint)i;
        indices[count] = 0;
        (op.Draw, op.Index, op.IndexOffset, op.IndexType) = (VulkanFrame.DrawKind.Indexed, uploads.Buffer, (ulong)at, Vk.IndexUint32);
        (op.Count, op.BaseVertex) = ((uint)count + 1, (int)op.First);
        return true;
    }

    private static uint Topology(uint mode)
    {
        _ = Assert(mode < 0x10000) && Assert(mode != uint.MaxValue);
        return mode switch
        {
            GlPoints => Vk.TopologyPoints, // Vulkan-only's (Drawable)
            1 => Vk.TopologyLines,
            2 or 3 => Vk.TopologyLineStrip, // a loop through indices that close it (Looped)
            4 => Vk.TopologyTriangles,
            5 => Vk.TopologyTriangleStrip,
            6 => Vk.TopologyTriangleFan,
            _ => uint.MaxValue // the rest stay with OpenGL
        };
    }

    // GL_LINES, GL_LINE_LOOP and GL_LINE_STRIP
    private static bool Lines(uint mode) => Assert(mode < 0x10000) && mode is 1 or 2 or 3;

    private readonly List<SharedImage> _fed = [];
    private readonly TerrainDraw.Texture[] _unfed = new TerrainDraw.Texture[MaxSamplers];

    // Vulkan-only: images both sampled and drawn into are sampled from snapshots made just before
    private TerrainDraw.Program? Unfed(Drawn program, TerrainDraw.Program recorded, VulkanFrame.Target target, out string why)
    {
        why = "";
        if (!TerrainRenderer.Fed(target, _sampled)) return recorded;
        _fed.Clear();
        _fed.AddRange(_sampled);
        if (!_terrain.Unfed(target, _sampled, out why)) return null;
        var count = Math.Min(program.Resolved.Length, MaxSamplers);
        program.Resolved.AsSpan(0, count).CopyTo(_unfed);
        for (var j = 0; j < Math.Min(_fed.Count, MaxSamplers); j++)
        {
            if (ReferenceEquals(_fed[j], _sampled[j])) continue;
            for (var i = 0; i < Math.Min(count, MaxSamplers); i++)
                if (_unfed[i].View == _fed[j].Sampled || _unfed[i].View == _fed[j].View)
                    _unfed[i] = new TerrainDraw.Texture(_sampled[j].Sampled, _unfed[i].Sampler, Vk.LayoutShaderRead);
        }

        return Assert(count <= MaxSamplers) && Assert(_fed.Count == _sampled.Count)
            ? program.Program.Recorded(_unfed.AsSpan(0, count))
            : null;
    }
}
