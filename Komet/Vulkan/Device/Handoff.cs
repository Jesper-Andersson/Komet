namespace Komet.Vulkan;

// The images a segment held: each goes back from the layout a barrier left it in to its resting one (To) as the segment
// ends, so the next segment finds every image at rest
internal static unsafe class Handoff
{
    private const int MaxImages = VulkanFrame.MaxHeld;
    private const uint ReadWrite = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;

    public readonly record struct Image(ulong Handle, int Layout, uint Aspect, int Levels)
    {
        public int To { get; init; } = -1; // the layout it leaves in, when not Layout
        public int Layers { get; init; } = 1;
    }

    public static void Release(IntPtr commands, ReadOnlySpan<Image> images)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(images.Length <= MaxImages)) return;
        var barriers = stackalloc Vk.ImageBarrier[Math.Max(images.Length, 1)]; // not MaxImages: stackalloc zeroes it
        var count = 0u;
        for (var i = 0; i < Math.Min(images.Length, MaxImages); i++)
            if (images[i].To >= 0 && images[i].To != images[i].Layout)
                barriers[count++] = Barrier(images[i]);
        if (count > 0) VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, count, barriers);
    }

    private static Vk.ImageBarrier Barrier(Image image)
    {
        if (!Assert(image.Handle != 0) || !Assert(image.Levels > 0)) return default;
        return new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = ReadWrite, DstAccess = ReadWrite, OldLayout = image.Layout,
            NewLayout = image.To, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored,
            Image = image.Handle,
            Range = new Vk.ColorRange
            {
                Aspect = image.Aspect, Levels = (uint)image.Levels, Layers = (uint)Math.Max(image.Layers, 1)
            }
        };
    }
}
