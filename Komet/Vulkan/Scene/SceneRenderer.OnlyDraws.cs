namespace Komet.Vulkan;

// Vulkan-only draws only mods ask for: indirect (GPU reads the mirror; multi-draw where supported), indexed line loops
// (indices + first copied as a strip), stencil test (dropped without stencil: OpenGL's passes), points (port sizes
// them).
internal sealed unsafe partial class SceneRenderer
{
    private const int ArraysCommand = 16, ElementsCommand = 20, MaxSeparateDraws = 1 << 12;

    private string Unindirect(GlTap.DrawCall call, GlTap.Indirect indirect)
    {
        if (!Assert(indirect.Draws > 0) || !Assert(call.Count >= 0)) return "an indirect draw of no commands";
        if (call.Mode == GlLineLoop) return "an indirect line loop";
        if (indirect.Offset % 4 != 0 || indirect.Stride % 4 != 0) return "an indirect draw of unaligned commands";
        if (indirect.CountBuffer != 0 && (!_terrain.Device.IndirectCount || VkApi.CmdDrawIndirectCount == null))
            return "an indirect draw with a count the GPU wrote, which the device does not draw";
        return "";
    }

    // Commands (and count) from the mirrors of the buffers OpenGL would read
    private bool Indirected(ref VulkanFrame.Op op, GlTap.Indirect indirect)
    {
        var indexed = op.Draw == VulkanFrame.DrawKind.Indexed;
        if (Mirrors.Read(indirect.Buffer) is not { } commands || !Assert(indirect.Draws > 0)) return false;
        var span = ((ulong)(indirect.Draws - 1) * (ulong)indirect.Stride) + (ulong)(indexed ? ElementsCommand : ArraysCommand);
        if ((ulong)indirect.Offset + span > commands.Size) return false;
        var counted = indirect.CountBuffer != 0;
        op.Draw = (indexed, counted) switch
        {
            (true, true) => VulkanFrame.DrawKind.IndexedIndirectCount, (true, false) => VulkanFrame.DrawKind.IndexedIndirect,
            (false, true) => VulkanFrame.DrawKind.IndirectCount, _ => VulkanFrame.DrawKind.Indirect
        };
        (op.Indirect, op.IndirectOffset, op.Count, op.First) =
            (commands.Buffer, commands.Offset + (ulong)indirect.Offset, (uint)indirect.Draws, (uint)indirect.Stride);
        if (!counted) return Assert(op.Count > 0);
        if (Mirrors.Read(indirect.CountBuffer) is not { } count || (ulong)indirect.CountOffset + 4 > count.Size) return false;
        (op.CountBuffer, op.CountOffset) = (count.Buffer, count.Offset + (ulong)indirect.CountOffset);
        return Assert(op.CountBuffer != 0);
    }

    // Several indirect commands without multiDrawIndirect: one draw each
    private void Posted(VulkanFrame frame, VulkanFrame.Op op)
    {
        if (!NotNull(frame) || op.Draw is not (VulkanFrame.DrawKind.Indirect or VulkanFrame.DrawKind.IndexedIndirect) ||
            op.Count <= 1 || _terrain.Device.MultiDraws)
        {
            frame.Post(op);
            return;
        }

        var (count, offset, stride) = (op.Count, op.IndirectOffset, (ulong)op.First);
        for (var i = 0u; i < Math.Min(count, MaxSeparateDraws); i++)
        {
            (op.Count, op.IndirectOffset) = (1, offset + (i * stride));
            frame.Post(op);
        }

        _ = Assert(count > 1);
    }

    // Indices 0..count - 1 plus the first, GPU-copied before the segment's draws (ordered with mirror writes)
    private bool LoopedIndices(HostBuffer uploads, ref VulkanFrame.Op op)
    {
        var size = op.IndexType == Vk.IndexUint32 ? 4 : 2;
        var count = (int)op.Count;
        if (!Assert(count is > 0 and < 1 << 24) || !NotNull(uploads)) return false;
        var at = uploads.Take(size * (count + 1), out _, 16);
        if (at < 0) return false;
        var from = (op.Index, op.IndexOffset);
        if (!Mirrors.CopyAhead(from, (uploads.Buffer, (ulong)at), (ulong)(size * count)) ||
            !Mirrors.CopyAhead(from, (uploads.Buffer, (ulong)(at + (size * count))), (ulong)size)) return false;
        (op.Index, op.IndexOffset, op.Count) = (uploads.Buffer, (ulong)at, (uint)count + 1);
        return Assert(op.Draw == VulkanFrame.DrawKind.Indexed);
    }

    // No stencil in the target: OpenGL's test passes and writes nothing, so none is tested
    private static GlTap.Fixed Stenciled(GlTap.Fixed state, VulkanFrame.Target target)
    {
        if ((state.Caps & (uint)GlTap.Caps.Stencil) == 0 || !NotNull(target)) return state;
        var held = target.Depth is { } depth && (depth.Format.Aspect & Vk.AspectStencil) != 0;
        _ = Assert(state.Stencil < 1u << 24);
        return held ? state : state with { Caps = state.Caps & ~(uint)GlTap.Caps.Stencil, Stencil = 0 };
    }

    private static (((uint, uint, uint) Front, (uint, uint, uint) Back) Values, Action<IntPtr>? Set) _stenciling;

    // Set before stencil-testing draws; the last delegate is reused while values stay (no closure per draw)
    private static Action<IntPtr> Stenciling(
        ((uint Reference, uint Compare, uint Write) Front, (uint Reference, uint Compare, uint Write) Back) values)
    {
        _ = Assert(values.Front.Reference <= 0xFF) && Assert(values.Back.Reference <= 0xFF);
        if (_stenciling.Set is { } same && _stenciling.Values == values) return same;
        _stenciling = (values, Stencil(values));
        return _stenciling.Set;
    }

    private static Action<IntPtr> Stencil(
        ((uint Reference, uint Compare, uint Write) Front, (uint Reference, uint Compare, uint Write) Back) values)
    {
        _ = Assert(values.Front.Reference <= 0xFF);
        return commands =>
        {
            foreach (var (face, (reference, compare, write)) in (ReadOnlySpan<(uint, (uint, uint, uint))>)
                     [(Vk.StencilFront, values.Front), (Vk.StencilBack, values.Back)])
            {
                VkApi.CmdSetStencilReference(commands, face, reference);
                VkApi.CmdSetStencilCompareMask(commands, face, compare);
                VkApi.CmdSetStencilWriteMask(commands, face, write);
            }
        };
    }
}
