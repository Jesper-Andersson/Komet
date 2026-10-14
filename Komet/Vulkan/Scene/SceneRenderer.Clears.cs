namespace Komet.Vulkan;

// Clears become vkCmdClearAttachments in the segment's rendering (all layers, like OpenGL); through partial color masks
// they are drawn (ClearDrawn), clears of the window itself are missing.
internal sealed unsafe partial class SceneRenderer
{
    private const uint FullMask = 0xF;

    public System.Func<bool> Clearing { get; set; } = () => false;

    public bool Clear(GlTap.ClearCall call)
    {
        if (!Framing) return Kept(call);
        if (!Clearing() || !Assert(call.Into >= -1)) return false;
        var fbo = call.Into >= 0 ? call.Into : GlTap.DrawFramebuffer;
        var state = GlTap.State;
        var depth = (call.Mask & GlTap.ClearDepthMask) != 0 && state.DepthWrite;
        var colors = (call.Mask & GlTap.ClearColorMask) != 0;
        // the stencil too, through its write mask
        var stencils = (call.Mask & GlTap.ClearStencilMask) != 0 && GlTap.StencilValues.Front.Write != 0;
        if (Unclearable(fbo, (depth || stencils, colors)) is { } unclearable) return unclearable;
        var target = _terrain.Target(state, out var why, fbo, !depth && !stencils, foreign: true);
        if (target is null) return Leave(why);
        Span<Vk.ClearAttachment> attachments = stackalloc Vk.ClearAttachment[GlTap.MaxBuffers + 1];
        var count = Attachments(attachments, target, call, (colors, depth, stencils), state);
        if (count < 0) return ClearDrawn(target, call, (colors, depth, stencils), state); // through masks
        if (count == 0) return true; // nothing attached where OpenGL would clear
        _sampled.Clear();
        var scissor = (state.Caps & (uint)GlTap.Caps.Scissor) != 0;
        if (!_terrain.Segment(target, _sampled, out why)) return Leave(why);
        Cleared(target, attachments[..count], scissor);
        return true;
    }

    // Stencil bit dropped (images have none), empty clears done, window clears without stand-in missing; null: cleared
    private bool? Unclearable(int fbo, (bool Depth, bool Colors) clears)
    {
        if (!Assert(fbo >= 0)) return null;
        if (!clears.Depth && !clears.Colors) return true; // nothing written, or the stencil alone
        return fbo == 0 ? Leave("a clear of the window itself (no stand-in for it)") : null;
    }

    // Masks dropping only absent channels are fine, fully masked attachments skipped; -1 if the mask drops held channels
    // or stencil bits (ClearDrawn's job); masks null: as the engine called it, everything written
    private static int Attachments(Span<Vk.ClearAttachment> into, VulkanFrame.Target target, GlTap.ClearCall call,
        (bool Colors, bool Depth, bool Stencil) clears, GlTap.Fixed? masks)
    {
        if (!NotNull(target) || !Assert(into.Length > GlTap.MaxBuffers)) return 0;
        var count = 0;
        for (var i = 0; clears.Colors && i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
        {
            if (target.Colors[i] is not { } color || (call.Output >= 0 && call.Output != i)) continue;
            var mask = masks is { } state ? (state.Blend[i] >> 22) & FullMask : FullMask;
            if (mask == 0) continue;
            if (mask != FullMask && (mask & Components(color)) != Components(color)) return -1;
            into[count++] = new Vk.ClearAttachment
            {
                Aspect = Vk.AspectColor, Attachment = (uint)i, Red = call.Color.R, Green = call.Color.G,
                Blue = call.Color.B, Alpha = call.Color.A
            };
        }

        var stencil = clears.Stencil && target.Depth is { } held && (held.Format.Aspect & Vk.AspectStencil) != 0;
        if (stencil && (GlTap.StencilValues.Front.Write & 0xFF) != 0xFF) return -1;
        if ((clears.Depth || stencil) && target.Depth is not null)
            into[count++] = new Vk.ClearAttachment
            {
                Aspect = (clears.Depth ? Vk.AspectDepth : 0) | (stencil ? Vk.AspectStencil : 0),
                Red = (float)Math.Clamp(call.Depth, 0, 1), // VkClearDepthStencilValue's depth, then its stencil as a uint
                Green = stencil ? BitConverter.UInt32BitsToSingle((uint)call.Stencil & 0xFF) : 0
            };
        return count;
    }

    // R, G, B, A as bits 1, 2, 4, 8
    private static uint Components(SharedImage image)
    {
        _ = Assert(image.Format.Aspect == Vk.AspectColor) && NotNull(image);
        if (image.OpaqueAlpha) return 0x7;
        return image.Format.Vulkan switch
        {
            Vk.FormatR8 or Vk.FormatR16F or Vk.FormatR32F or Vk.FormatR16 => 0x1,
            Vk.FormatRg8 or Vk.FormatRg16F => 0x3,
            Vk.FormatB10G11R11F => 0x7,
            _ => FullMask
        };
    }

    private void Cleared(VulkanFrame.Target target, ReadOnlySpan<Vk.ClearAttachment> attachments, bool scissor)
    {
        if (!NotNull(target) || !Assert(attachments.Length is > 0 and <= GlTap.MaxBuffers + 1)) return;
        var frame = _terrain.Frame;
        (int X, int Y, int Width, int Height) box = scissor ? GlTap.Scissor : (0, 0, target.Width, target.Height);
        var (x, y) = (Math.Clamp(box.X, 0, target.Width), Math.Clamp(box.Y, 0, target.Height));
        var (right, top) = (Math.Clamp(box.X + box.Width, x, target.Width),
            Math.Clamp(box.Y + box.Height, y, target.Height));
        if (right > x && top > y)
        {
            var rect = new Vk.Rect { X = x, Y = y, Width = (uint)(right - x), Height = (uint)(top - y) };
            frame.Post(new VulkanFrame.Op
            {
                Kind = VulkanFrame.OpKind.Clear, Ref = new VulkanFrame.Clearing(attachments.ToArray(), rect, (uint)target.Layers)
            });
        }

        _terrain.Drawn(target);
        _terrain.Unprepared();
        Clears++;
    }
}
