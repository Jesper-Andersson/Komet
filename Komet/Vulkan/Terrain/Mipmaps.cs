namespace Komet.Vulkan;

// glGenerateMipmap in a copy: each level linearly blitted from the one above, as Mesa does (same texels on the same
// sampler hardware, MipmapsGpuTests). The image stays in TRANSFER_DST; levels: base and those below.
internal static unsafe class Mipmaps
{
    private const int MaxLevels = 16;

    public static void Record(IntPtr commands, SharedImage image, int levels)
    {
        if (!Assert(commands != IntPtr.Zero) || !NotNull(image) || !Assert(levels is > 1 and <= MaxLevels)) return;
        var handle = image.Image;
        for (var level = 1; level < Math.Min(levels, MaxLevels); level++)
        {
            Moved(commands, (handle, image.Layers), (level - 1, 1), (Vk.LayoutTransferDst, Vk.LayoutTransferSrc));
            var layer = new Vk.Layers { Aspect = Vk.AspectColor, Level = (uint)(level - 1), Count = (uint)image.Layers };
            var blit = new Vk.ImageBlit
            {
                Source = layer, SourceX1 = Side(image.Width, level - 1), SourceY1 = Side(image.Height, level - 1),
                SourceZ1 = 1, Target = layer with { Level = (uint)level }, TargetX1 = Side(image.Width, level),
                TargetY1 = Side(image.Height, level), TargetZ1 = 1
            };
            VkApi.CmdBlitImage(commands, handle, Vk.LayoutTransferSrc, handle, Vk.LayoutTransferDst, 1, &blit, Vk.FilterLinear);
        }

        Moved(commands, (handle, image.Layers), (0, levels - 1), (Vk.LayoutTransferSrc, Vk.LayoutTransferDst));
    }

    private static int Side(int size, int level) => Assert(size > 0) ? Math.Max(size >> level, 1) : 1;

    // Each layer (cube face, array layer) makes its own levels
    private static void Moved(IntPtr commands, (ulong Handle, int Layers) image, (int First, int Count) levels,
        (int From, int To) layouts)
    {
        if (!Assert(image.Handle != 0) || !Assert(levels.Count > 0)) return;
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = Vk.AccessTransferWrite | Vk.AccessTransferRead,
            DstAccess = Vk.AccessTransferRead | Vk.AccessTransferWrite, OldLayout = layouts.From, NewLayout = layouts.To,
            SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = image.Handle,
            Range = new Vk.ColorRange
            {
                Aspect = Vk.AspectColor, BaseLevel = (uint)levels.First, Levels = (uint)levels.Count,
                Layers = (uint)Math.Max(image.Layers, 1)
            }
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageTransfer, 0, 0, null, 0, null, 1, &barrier);
    }
}
