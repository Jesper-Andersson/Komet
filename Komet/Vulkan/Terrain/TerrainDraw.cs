using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// The quad index pattern (Quads) is shared, not the recorder's to free.
internal sealed unsafe class TerrainDraw(HostBuffer quads)
{
    private const int MaxResources = 32, MaxRanges = 1 << 18, CommandBytes = 20; // a counted draw: 4 runs a location

    public readonly record struct Texture(ulong View, ulong Sampler, int Layout);

    public sealed record Program(GlslPort.Ported Ported, UniformMirror Uniforms,
        IReadOnlyDictionary<string, Texture> Textures)
    {
        public Vk.ImageDescriptor[]? Descriptors { get; set; }
    }

    public static Vk.ImageDescriptor[] Images(TerrainPipeline pipeline, Program program)
    {
        if (!NotNull(pipeline) || !NotNull(program)) return [];
        var images = new Vk.ImageDescriptor[pipeline.Resources.Length];
        for (var i = 0; i < Math.Min(images.Length, MaxResources); i++)
            if (pipeline.Resources[i].Type == Vk.CombinedSampler)
                images[i] = Sampled(program, pipeline.Resources[i].Binding);
        _ = Assert(images.Length <= MaxResources);
        return images;
    }

    // ClientPlatformWindows.AllocateEmptySSBOMesh's pattern: 0 1 2 0 2 3, then +4
    public static HostBuffer? Quads(VulkanDevice device, int quads)
    {
        if (!NotNull(device) || !Assert(quads is > 0 and <= 1 << 26)) return null;
        var indices = HostBuffer.Create(device, (ulong)quads * 6 * 4);
        if (indices is null) return null;
        var at = indices.Take(quads * 6 * 4, out var into, 4);
        var pattern = MemoryMarshal.Cast<byte, uint>(into);
        _ = Assert(at == 0) && Assert(pattern.Length == quads * 6);
        ReadOnlySpan<uint> corners = [0, 1, 2, 0, 2, 3];
        for (var i = 0; i < Math.Min(pattern.Length, 6 << 26); i++) pattern[i] = (uint)(i / 6 * 4) + corners[i % 6];
        return indices;
    }

    public static long PackTicks { get; private set; } // probe

    // SSBO path (quad pattern, FaceData), else classic (the pool's own indices)
    public bool Draw(VulkanFrame frame, HostBuffer uploads, (TerrainPipeline Pipeline, TerrainPools.Pool Pool) with,
        Program program, (int[] Starts, int[] Sizes, int Count, bool Faces) ranges)
    {
        var (pipeline, pool) = with;
        if (!NotNull(pipeline) || !NotNull(pool) || !Assert(ranges.Count is >= 0 and <= MaxRanges)) return false;
        if (ranges.Count == 0) return true;
        var indices = ranges.Faces ? (quads.Buffer, quads.Size) : (pool.Indices?.Buffer ?? 0, pool.Indices?.Size ?? 0);
        if (indices.Item1 == 0) return false;
        var draws = Commands(uploads, ranges, indices.Item2);
        if (draws < 0) return false;
        var op = new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.Draw, Draw = VulkanFrame.DrawKind.IndexedIndirect, Pipeline = pipeline.Pipeline,
            Layout = pipeline.Layout, Index = indices.Item1, IndexType = Vk.IndexUint32, Indirect = uploads.Buffer,
            IndirectOffset = (ulong)draws, Count = (uint)ranges.Count, First = CommandBytes
        };
        return Posted(frame, (uploads, pipeline, program), pool, op);
    }

    // Commands and count from OcclusionCulling's cull.comp, starts as index offsets
    public bool Counted(VulkanFrame frame, HostBuffer uploads, (TerrainPipeline Pipeline, TerrainPools.Pool Pool) with,
        Program program, (bool Faces, (ulong Buffer, ulong Offset) Commands, (ulong Buffer, ulong Offset) Count, int Max) draw)
    {
        var (pipeline, pool) = with;
        if (!NotNull(pipeline) || !NotNull(pool) || !Assert(draw.Max is >= 0 and <= MaxRanges)) return false;
        if (draw.Max == 0) return true;
        var index = draw.Faces ? quads.Buffer : pool.Indices?.Buffer ?? 0;
        if (index == 0 || draw.Commands.Buffer == 0 || draw.Count.Buffer == 0 ||
            VkApi.CmdDrawIndexedIndirectCount == null) return false;
        if (!Assert(draw.Commands.Offset % 4 == 0) || !Assert(draw.Count.Offset % 4 == 0)) return false;
        var op = new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.Draw, Draw = VulkanFrame.DrawKind.IndexedIndirectCount, Pipeline = pipeline.Pipeline,
            Layout = pipeline.Layout, Index = index, IndexType = Vk.IndexUint32, Indirect = draw.Commands.Buffer,
            IndirectOffset = draw.Commands.Offset, CountBuffer = draw.Count.Buffer, CountOffset = draw.Count.Offset,
            Count = (uint)draw.Max, First = CommandBytes
        };
        return Posted(frame, (uploads, pipeline, program), pool, op);
    }

    private static bool Posted(VulkanFrame frame, (HostBuffer Uploads, TerrainPipeline Pipeline, Program Program) draw,
        TerrainPools.Pool pool, VulkanFrame.Op op)
    {
        var bound = pool.Bindings(draw.Pipeline.Locations);
        if (!Filled(frame, draw, ref op, pool.Faces)) return false;
        Bound(frame, bound, ref op);
        frame.Post(op);
        return Assert(op.DescriptorCount > 0) && Assert(op.VertexCount >= 0);
    }

    public static bool Filled(VulkanFrame frame, (HostBuffer Uploads, TerrainPipeline Pipeline, Program Program) draw,
        ref VulkanFrame.Op op, SharedBuffer? faces = null, ReadOnlySpan<BlockBuffer> blocks = default)
    {
        var (uploads, pipeline, program) = draw;
        var resources = pipeline.Resources;
        if (!Assert(resources.Length <= MaxResources) || !NotNull(program.Ported) || !NotNull(frame)) return false;
        var sampled = program.Descriptors ??= Images(pipeline, program);
        var hot = program.Ported.HotUniforms.Size / 4;
        var (at, _, push) = frame.Room(resources.Length, 0, hot);
        var into = frame.Descriptors(at, resources.Length);
        for (var i = 0; i < Math.Min(resources.Length, MaxResources); i++)
        {
            var r = resources[i];
            var d = new VulkanFrame.Descriptor { Binding = (uint)r.Binding, Type = r.Type };
            if (r.Type == Vk.CombinedSampler) d.Image = sampled[i];
            else d.Buffer = r.Binding switch
            {
                GlslPort.VertexBlock => Block(uploads, program, program.Ported.VertexUniforms),
                GlslPort.FragmentBlock => Block(uploads, program, program.Ported.FragmentUniforms),
                GlslPort.FirstStorage + 3 when faces is not null => new Vk.BufferDescriptor
                {
                    Buffer = faces.Buffer, Range = faces.Size
                },
                _ => Of(blocks, r.Binding)
            };
            if (d.Buffer.Buffer == 0 && d.Image.View == 0) return false;
            into[i] = d;
        }

        if (hot > 0) program.Uniforms.Image(2)[..hot].CopyTo(frame.PushWords(push, hot));
        (op.Descriptors, op.DescriptorCount, op.Push, op.PushBytes) = (at, resources.Length, push, hot * 4);
        return true;
    }

    public readonly record struct BlockBuffer(int Binding, Vk.BufferDescriptor Buffer);

    private static Vk.BufferDescriptor Of(ReadOnlySpan<BlockBuffer> blocks, int binding)
    {
        _ = Assert(blocks.Length <= MaxResources) && Assert(binding >= 0);
        foreach (var block in blocks.Bounded(MaxResources))
            if (block.Binding == binding)
                return block.Buffer;
        return default;
    }

    public static void Bound(VulkanFrame frame, ReadOnlySpan<(uint Location, ulong Buffer, ulong Offset)> bound,
        ref VulkanFrame.Op op)
    {
        if (!NotNull(frame) || !Assert(bound.Length <= 16)) return;
        var (_, at, _) = frame.Room(0, bound.Length, 0);
        bound.CopyTo(frame.VertexBindings(at, bound.Length));
        (op.Vertices, op.VertexCount) = (at, bound.Length);
    }

    // All recorders bind here, pipeline first: skips what is already bound (nothing else binds these, and neither
    // rendering nor pipeline changes unbind them)
    public static void Bind(IntPtr commands, ulong pipeline)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(pipeline != 0)) return;
        if (commands != _commands) (_commands, _bound, _index, _vertexCount) = (commands, 0, default, -1);
        if (_bound != pipeline) VkApi.CmdBindPipeline(commands, (int)Vk.BindGraphics, pipeline);
        _bound = pipeline;
    }

    public static void BindIndex(IntPtr commands, (ulong Buffer, ulong Offset, uint Type) index)
    {
        if (!Assert(commands == _commands) || !Assert(index.Buffer != 0) || index != _index)
            VkApi.CmdBindIndexBuffer(commands, index.Buffer, index.Offset, index.Type);
        _index = commands == _commands ? index : default;
    }

    public static void Forget()
    {
        _ = Assert(_bound == 0 || _commands != IntPtr.Zero) && Assert(Frames++ >= 0);
        (_commands, _bound, _pushedLayout, _index, _vertexCount) = (IntPtr.Zero, 0, 0, default, -1);
    }

    private static IntPtr _commands;
    private static ulong _bound;
    private static (ulong Buffer, ulong Offset, uint Type) _index;
    private static readonly (uint Location, ulong Buffer, ulong Offset)[] LastVertices = new (uint, ulong, ulong)[16];
    private static int _vertexCount = -1; // of LastVertices, -1 when nothing is known bound
    private static long Frames { get; set; } // command buffers begun

    private static long Commands(HostBuffer uploads, (int[] Starts, int[] Sizes, int Count, bool Faces) ranges,
        ulong indexBytes)
    {
        if (!Assert(ranges.Starts.Length >= ranges.Count && ranges.Sizes.Length >= ranges.Count)) return -1;
        for (var i = 0; i < Math.Min(ranges.Count, MaxRanges); i++)
            if (ranges.Starts[i] < 0 || (ulong)ranges.Starts[i] + 4UL * (ulong)ranges.Sizes[i] > indexBytes)
                return -1; // past the index buffer's end
        var at = uploads.Take(ranges.Count * CommandBytes, out var into, 16);
        if (at < 0 || !Assert(at % 16 == 0)) return -1;
        var draws = MemoryMarshal.Cast<byte, Vk.DrawIndexed>(into);
        for (var i = 0; i < Math.Min(ranges.Count, MaxRanges); i++)
            draws[i] = new Vk.DrawIndexed
            {
                IndexCount = (uint)ranges.Sizes[i], InstanceCount = 1, FirstIndex = (uint)(ranges.Starts[i] / 4)
            };
        return at;
    }

    // Skipped when identical to the last push for the layout in this command buffer
    public static void Pushed(IntPtr commands, ulong layout, Vk.WriteInfo* writes, int count)
    {
        if (!Assert(count is >= 0 and <= MaxResources) || !Assert(writes != null)) return;
        var same = commands == _commands && layout == _pushedLayout && count == _pushedCount;
        for (var i = 0; i < Math.Min(count, MaxResources); i++)
        {
            var (buffer, image) = (*writes[i].Buffers, *writes[i].Images);
            var (b, m) = (LastBuffers[i], LastImages[i]); // fields compared: ValueType.Equals would box
            same = same && b.Buffer == buffer.Buffer && b.Offset == buffer.Offset && b.Range == buffer.Range &&
                   m.View == image.View && m.Sampler == image.Sampler && m.Layout == image.Layout;
            (LastBuffers[i], LastImages[i]) = (buffer, image);
        }

        if (same) return;
        (_pushedLayout, _pushedCount) = (layout, count);
        VkApi.CmdPushDescriptors(commands, (int)Vk.BindGraphics, layout, 0, (uint)count, writes);
    }

    private static readonly Vk.BufferDescriptor[] LastBuffers = new Vk.BufferDescriptor[MaxResources];
    private static readonly Vk.ImageDescriptor[] LastImages = new Vk.ImageDescriptor[MaxResources];
    private static ulong _pushedLayout;
    private static int _pushedCount;

    // Reused from the program's last draw while unchanged
    internal static Vk.BufferDescriptor Block(HostBuffer uploads, Program program, GlslPort.Block block)
    {
        var mirror = program.Uniforms;
        if (!Assert(block.Size > 0) || !NotNull(mirror)) return default;
        var index = block.Binding == GlslPort.VertexBlock ? 0 : 1;
        var descriptor = new Vk.BufferDescriptor { Buffer = uploads.Buffer, Range = (ulong)block.Size };
        if (mirror.Uploaded.Length == 3 && mirror.Uploaded[index] is var (last, epoch, was, changes) &&
            ReferenceEquals(last, uploads) && epoch == uploads.Epoch && changes == mirror.Changes[index])
            return descriptor with { Offset = (ulong)was };
        var at = uploads.Take(block.Size, out var into);
        if (at < 0) return default;
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        mirror.Pack(block, into);
        mirror.Packs++;
        PackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        if (mirror.Uploaded.Length == 3) mirror.Uploaded[index] = (uploads, uploads.Epoch, at, mirror.Changes[index]);
        return descriptor with { Offset = (ulong)at };
    }

    private static Vk.ImageDescriptor Sampled(Program program, int binding)
    {
        var sampler = program.Ported.Samplers.FirstOrDefault(s => s.Binding == binding);
        if (!NotNull(program.Textures) || sampler is null || !program.Textures.TryGetValue(sampler.Name, out var t))
            return default;
        return Assert(t.View != 0)
            ? new Vk.ImageDescriptor { Sampler = t.Sampler, View = t.View, Layout = t.Layout }
            : default;
    }

    [System.Runtime.CompilerServices.SkipLocalsInit]
    public static void BindVertices(IntPtr commands, ReadOnlySpan<(uint Location, ulong Buffer, ulong Offset)> bound)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(bound.Length <= 16)) return;
        if (commands == _commands && Bound(bound)) return;
        var buffers = stackalloc ulong[16];
        var offsets = stackalloc ulong[16];
        var (first, count) = (0u, 0u);
        for (var i = 0; i < Math.Min(bound.Length, 16); i++)
        {
            if (count > 0 && bound[i].Location != first + count)
            {
                VkApi.CmdBindVertexBuffers(commands, first, count, buffers, offsets);
                count = 0;
            }

            if (count == 0) first = bound[i].Location;
            (buffers[count], offsets[count]) = (bound[i].Buffer, bound[i].Offset);
            count++;
        }

        if (count > 0) VkApi.CmdBindVertexBuffers(commands, first, count, buffers, offsets);
        _ = Assert(count <= 16);
        _vertexCount = commands == _commands ? bound.Length : -1;
        bound.CopyTo(LastVertices);
    }

    private static bool Bound(ReadOnlySpan<(uint Location, ulong Buffer, ulong Offset)> bound)
    {
        if (bound.Length != _vertexCount || !Assert(_vertexCount <= LastVertices.Length)) return false;
        for (var i = 0; i < Math.Min(bound.Length, 16); i++)
            if (bound[i].Location != LastVertices[i].Location || bound[i].Buffer != LastVertices[i].Buffer ||
                bound[i].Offset != LastVertices[i].Offset)
                return false;
        return Assert(bound.Length <= 16);
    }
}
