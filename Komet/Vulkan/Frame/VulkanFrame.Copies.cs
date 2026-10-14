namespace Komet.Vulkan;

// Vulkan-only's exact copies: texels between same-format images at any level/layer (glCopyImageSubData, a draw's
// snapshot), a rectangle into host memory for an OpenGL read, and whole level/layer clears (glClearTexImage). In the
// open segment outside rendering; the next rendering moves the images back.
internal sealed unsafe partial class VulkanFrame
{
    // Regions (disjoint: a snapshot's levels) in one copy behind one barrier
    public bool CopyImage(SharedImage from, SharedImage to, Vk.ImageCopy[] regions)
    {
        if (!NotNull(regions) || regions.Length == 0) return regions is { Length: 0 };
        if (_open is null || !Holds(from) || !Holds(to) || from == to || !Assert(from.Format.Aspect == to.Format.Aspect))
            return false;
        ForTransfer((from, Vk.LayoutTransferSrc), (to, Vk.LayoutTransferDst));
        var (source, target) = (from.Image, to.Image);
        Post(commands => ImageCopied(commands, (source, target), regions));
        return Assert(_rendering is null);
    }

    private static void ImageCopied(IntPtr commands, (ulong From, ulong To) images, Vk.ImageCopy[] regions)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(images.From != 0 && images.To != 0)) return;
        fixed (Vk.ImageCopy* at = regions)
            VkApi.CmdCopyImage(commands, images.From, Vk.LayoutTransferSrc, images.To, Vk.LayoutTransferDst, (uint)regions.Length, at);
    }

    // What drew into the image comes first; the host reads the buffer once the segment's submission is done
    public bool CopyOut(SharedImage image, Vk.BufferImageCopy region, ulong buffer)
    {
        if (_open is null || !Holds(image) || !Assert(buffer != 0)) return false;
        ForTransfer((image, Vk.LayoutTransferSrc));
        var source = image.Image;
        Post(commands => CopiedOut(commands, source, region, buffer));
        return Assert(_rendering is null);
    }

    // Outside rendering, images in transfer layouts behind one barrier (even with no move): earlier writes come first
    private void ForTransfer((SharedImage Image, int Layout) first, (SharedImage Image, int Layout)? second = null)
    {
        EndRendering();
        _moves.Clear();
        Want(first.Image, first.Layout);
        if (second is { } other) Want(other.Image, other.Layout);
        Barrier();
        _ = NotNull(first.Image) && Assert(_rendering is null);
    }

    private static void CopiedOut(IntPtr commands, ulong image, Vk.BufferImageCopy region, ulong buffer)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(image != 0 && buffer != 0)) return;
        VkApi.CmdCopyImageToBuffer(commands, image, Vk.LayoutTransferSrc, buffer, 1, &region);
        HostReadable(commands);
    }

    // Level 0 of from, whole, into an image of FrameBridge's (same size and format, outside the frame's layouts: it rests
    // in GENERAL before and after); posted in the open segment
    public bool CopyRaw(SharedImage from, ulong into)
    {
        if (_open is null || !Holds(from) || !Assert(into != 0) || !Assert(from.Image != into)) return false;
        ForTransfer((from, Vk.LayoutTransferSrc));
        var (source, aspect, size) = (from.Image, from.Format.Aspect & ~Vk.AspectStencil, (from.Width, from.Height));
        Post(commands => RawCopied(commands, (source, into), aspect, size));
        return Assert(_rendering is null);
    }

    private static void RawCopied(IntPtr commands, (ulong From, ulong Into) images, uint aspect, (int Width, int Height) size)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(size.Width > 0 && size.Height > 0)) return;
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var range = new Vk.ColorRange { Aspect = aspect, Levels = 1, Layers = 1 };
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = Vk.LayoutGeneral,
            NewLayout = Vk.LayoutTransferDst, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored,
            Image = images.Into, Range = range
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
        var layers = new Vk.Layers { Aspect = aspect, Count = 1 };
        var region = new Vk.ImageCopy { Source = layers, Target = layers, Width = (uint)size.Width, Height = (uint)size.Height, Depth = 1 };
        VkApi.CmdCopyImage(commands, images.From, Vk.LayoutTransferSrc, images.Into, Vk.LayoutTransferDst, 1, &region);
        (barrier.OldLayout, barrier.NewLayout) = (Vk.LayoutTransferDst, Vk.LayoutGeneral);
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }

    // What the transfers before wrote, visible to the host once the fence is signalled
    internal static void HostReadable(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero)) return;
        var host = new Vk.GlobalBarrier { SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite, DstAccess = Vk.AccessHostRead };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageHost, 0, 1, &host, 0, null, 0, null);
    }

    // The levels and layers of range (its aspect the image's) set to the color, or to the depth and stencil
    public bool ClearImage(SharedImage image, Vk.ColorRange range, (float R, float G, float B, float A) color,
        Vk.DepthStencilValue depth)
    {
        if (_open is null || !Holds(image) || !Assert(range.Levels > 0 && range.Layers > 0)) return false;
        ForTransfer((image, Vk.LayoutTransferDst));
        var (handle, colored) = (image.Image, (image.Format.Aspect & Vk.AspectColor) != 0);
        Post(commands => ImageCleared(commands, (handle, colored), range, (color, depth)));
        return Assert(_rendering is null);
    }

    private static void ImageCleared(IntPtr commands, (ulong Image, bool Color) image, Vk.ColorRange range,
        ((float R, float G, float B, float A) Color, Vk.DepthStencilValue Depth) value)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(image.Image != 0)) return;
        if (!image.Color)
        {
            var depth = value.Depth;
            VkApi.CmdClearDepthStencilImage(commands, image.Image, Vk.LayoutTransferDst, &depth, 1, &range);
            return;
        }

        var color = stackalloc float[] { value.Color.R, value.Color.G, value.Color.B, value.Color.A };
        VkApi.CmdClearColorImage(commands, image.Image, Vk.LayoutTransferDst, color, 1, &range);
    }
}
