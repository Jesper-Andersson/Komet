namespace Komet.Vulkan;

// The recording thread only
internal sealed unsafe partial class VulkanFrame
{
    private const int MaxEmitted = 32;

    private void Emit(ref Op op)
    {
        if (op.Kind > OpKind.Call)
        {
            Queued(ref op);
            return;
        }

        var commands = _recording;
        if (!Assert(commands != IntPtr.Zero) || !Assert(op.Kind <= OpKind.Call)) return;
        switch (op.Kind)
        {
            case OpKind.EndRendering:
                VkApi.CmdEndRendering(commands);
                break;
            case OpKind.Barrier when op.Ref is Moves moves:
                Moved(commands, moves);
                break;
            case OpKind.BeginRendering when op.Ref is Rendering rendering:
                Begun(commands, rendering);
                break;
            case OpKind.Viewport:
                var viewport = new Vk.Viewport { X = op.X, Y = op.Y, Width = op.Width, Height = op.Height, MaxDepth = 1 };
                VkApi.CmdSetViewport(commands, 0, 1, &viewport);
                break;
            case OpKind.Scissor:
                var rect = new Vk.Rect { X = (int)op.X, Y = (int)op.Y, Width = (uint)op.Width, Height = (uint)op.Height };
                VkApi.CmdSetScissor(commands, 0, 1, &rect);
                break;
            case OpKind.Draw:
                Drew(commands, ref op);
                break;
            case OpKind.Release when op.Ref is Handoff.Image[] images:
                Handoff.Release(commands, images);
                break;
            case OpKind.Clear when op.Ref is Clearing clearing:
                Cleared(commands, clearing);
                break;
            case OpKind.BeginQuery:
                VkApi.CmdBeginQuery(commands, op.Pipeline, op.Count, op.First);
                break;
            case OpKind.EndQuery:
                VkApi.CmdEndQuery(commands, op.Pipeline, op.Count);
                break;
            case OpKind.LineWidth:
                VkApi.CmdSetLineWidth(commands, op.X);
                break;
            case OpKind.Timestamp:
                VkApi.CmdWriteTimestamp(commands, op.First, op.Pipeline, op.Count);
                break;
            case OpKind.Copy or OpKind.Blit or OpKind.Fill:
                Transferred(commands, ref op);
                break;
            case OpKind.Call when op.Ref is Action<IntPtr> record:
                record(commands);
                break;
        }
    }

    private static void Transferred(IntPtr commands, ref Op op)
    {
        if (op.Kind == OpKind.Fill)
        {
            VkApi.CmdFillBuffer(commands, op.Indirect, op.IndirectOffset, op.CountOffset, 0);
            VulkanBackend.Ordered(commands); // ordered before what follows, as a GL buffer clear is
        }
        else if (op.Kind == OpKind.Blit) Blitting(commands, ref op);
        else Copied(commands, (op.Index, op.IndexOffset), (op.Indirect, op.IndirectOffset), op.CountOffset);
        _ = Assert(op.Kind is OpKind.Copy or OpKind.Blit or OpKind.Fill);
    }

    private void Queued(ref Op op)
    {
        _ = Assert(Thread.CurrentThread == _worker);
        switch (op.Kind)
        {
            case OpKind.Begin when op.Ref is Segment segment:
                _recording = segment.Commands;
                if (!VulkanDevice.Begin(segment.Commands) && Fault.Length == 0) Fault = "no command buffer for a segment";
                TerrainDraw.Forget(); // nothing bound in it
                break;
            case OpKind.Queue when op.Ref is Action queued:
                queued();
                break;
            default:
                _ = Assert(op.Kind is OpKind.Begin or OpKind.Queue);
                break;
        }
    }

    private static void Moved(IntPtr commands, Moves moves)
    {
        var (images, count) = (moves.Images, moves.Count);
        if (!Assert(count <= MaxImages && count <= images.Length) || !Assert(commands != IntPtr.Zero)) return;
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var barriers = stackalloc Vk.ImageBarrier[Math.Max(count, 1)];
        for (var i = 0; i < Math.Min(count, MaxImages); i++)
        {
            var (image, aspect, levels, layers, from, to) = images[i];
            barriers[i] = new Vk.ImageBarrier
            {
                SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = from, NewLayout = to,
                SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = image,
                Range = new Vk.ColorRange { Aspect = aspect, Levels = (uint)levels, Layers = (uint)layers }
            };
        }

        var memory = new Vk.GlobalBarrier { SType = Vk.MemoryBarrier, SrcAccess = all, DstAccess = all };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 1, &memory, 0, null, (uint)count, barriers);
    }

    private static void Cleared(IntPtr commands, Clearing clearing)
    {
        if (!Assert(clearing.Attachments.Length is > 0 and <= MaxColors + 1) || !Assert(commands != IntPtr.Zero)) return;
        var rect = new Vk.ClearRect { Rect = clearing.Rect, Layers = Math.Max(clearing.Layers, 1) };
        fixed (Vk.ClearAttachment* attachments = clearing.Attachments)
            VkApi.CmdClearAttachments(commands, (uint)clearing.Attachments.Length, attachments, 1, &rect);
    }

    private static void Begun(IntPtr commands, Rendering rendering)
    {
        if (!Assert(rendering.Count <= MaxColors) || !Assert(rendering.Width > 0)) return;
        var colors = stackalloc Vk.Attachment[MaxColors];
        for (var i = 0; i < Math.Min(rendering.Count, MaxColors); i++)
            colors[i] = rendering.Colors[i] != 0
                ? Kept(rendering.Colors[i], Vk.LayoutColorAttachment)
                : new Vk.Attachment { SType = Vk.RenderingAttachmentInfo }; // no view: the output writes nothing
        var depth = rendering.Depth != 0 ? Kept(rendering.Depth, rendering.DepthLayout) : default;
        var area = new Vk.Rect { Width = rendering.Width, Height = rendering.Height };
        var info = new Vk.RenderingInfo
        {
            SType = Vk.RenderingInfoType, Area = area, Layers = Math.Max(rendering.Layers, 1), ColorCount = (uint)rendering.Count,
            Colors = colors, Depth = rendering.Depth == 0 ? null : &depth,
            Stencil = rendering.Depth != 0 && rendering.Stencil ? &depth : null // the same view and layout
        };
        VkApi.CmdBeginRendering(commands, &info);
        VkApi.CmdSetScissor(commands, 0, 1, &area);
    }

    [System.Runtime.CompilerServices.SkipLocalsInit]
    private void Drew(IntPtr commands, ref Op op)
    {
        if (!Assert(op.DescriptorCount is >= 0 and <= MaxEmitted) || !Assert(op.VertexCount is >= 0 and <= 16)) return;
        var writes = stackalloc Vk.WriteInfo[Math.Max(op.DescriptorCount, 1)];
        var buffers = stackalloc Vk.BufferDescriptor[Math.Max(op.DescriptorCount, 1)];
        var images = stackalloc Vk.ImageDescriptor[Math.Max(op.DescriptorCount, 1)];
        for (var i = 0; i < Math.Min(op.DescriptorCount, MaxEmitted); i++)
        {
            ref var d = ref _descriptors[op.Descriptors + i];
            (buffers[i], images[i]) = (d.Buffer, d.Image);
            writes[i] = new Vk.WriteInfo
            {
                SType = Vk.WriteDescriptorSet, Binding = d.Binding, Count = 1, Type = (int)d.Type, Buffers = buffers + i,
                Images = images + i
            };
        }

        if (op.DescriptorCount > 0) TerrainDraw.Pushed(commands, op.Layout, writes, op.DescriptorCount); // Komet's clear has none
        TerrainDraw.Bind(commands, op.Pipeline);
        if (op.PushBytes > 0)
            fixed (uint* words = &_push[op.Push])
                VkApi.CmdPushConstants(commands, op.Layout, Vk.StageVertex | Vk.StageFragment, 0, (uint)op.PushBytes,
                    words);
        TerrainDraw.BindVertices(commands, _vertexBindings.AsSpan(op.Vertices, op.VertexCount));
        if (op.Draw is not (DrawKind.Direct or DrawKind.Indirect or DrawKind.IndirectCount))
            TerrainDraw.BindIndex(commands, (op.Index, op.IndexOffset, op.IndexType));
        switch (op.Draw)
        {
            case DrawKind.Indirect:
                VkApi.CmdDrawIndirect(commands, op.Indirect, op.IndirectOffset, op.Count, op.First);
                break;
            case DrawKind.IndirectCount:
                VkApi.CmdDrawIndirectCount(commands, op.Indirect, op.IndirectOffset, op.CountBuffer, op.CountOffset, op.Count,
                    op.First);
                break;
            case DrawKind.IndexedIndirect:
                VkApi.CmdDrawIndexedIndirect(commands, op.Indirect, op.IndirectOffset, op.Count, op.First);
                break;
            case DrawKind.IndexedIndirectCount:
                VkApi.CmdDrawIndexedIndirectCount(commands, op.Indirect, op.IndirectOffset, op.CountBuffer,
                    op.CountOffset, op.Count, op.First);
                break;
            case DrawKind.Indexed:
                VkApi.CmdDrawIndexed(commands, op.Count, op.Instances, 0, op.BaseVertex, op.BaseInstance);
                break;
            default:
                VkApi.CmdDraw(commands, op.Count, op.Instances, op.First, op.BaseInstance);
                break;
        }
    }
}
