namespace Komet.Vulkan;

// An empty unit samples incomplete texture 0: (0, 0, 0, 1) (GL 4.6 core 11.1.3.5). Mesa samples it as 1x1 with the
// unit's sampling, border included, so a clamp-to-border sampler mixes the border in. Without a sampler object a 1x1
// (0, 0, 0, 1) image matches and untextured GUI draws stay Vulkan's; with one, OpenGL draws.
internal sealed unsafe partial class TerrainRenderer
{
    private static readonly Samplers.State BlankSampling = new(false, false, 0, true, false);

    private SharedImage? _blank;
    private bool _blankless; // making it failed: OpenGL draws those

    public TerrainDraw.Texture? Unbound(string type, int samplerObject)
    {
        if (type != "sampler2D" || samplerObject != 0 || GlTap.DefaultTextureTouched || _blankless) return null;
        _blank ??= Blank(Device);
        _blankless = _blank is null;
        if (_blank is not { } blank) return null;
        return Assert(blank.Levels == 1) ? new TerrainDraw.Texture(blank.View, Sampling.Get(BlankSampling), Vk.LayoutShaderRead)
            : null;
    }

    private static SharedImage? Blank(VulkanDevice device)
    {
        var image = SharedImage.Private(device, (1, 1, 1, 1), SharedFormat.Rgba8, Vk.Sampled | Vk.TransferDst, out _);
        if (image is null) return null;
        var handle = image.Image;
        if (device.Run(commands => Blanked(commands, handle), []) && Assert(handle != 0)) return image;
        image.Dispose();
        return null;
    }

    private static void Blanked(IntPtr commands, ulong image)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(image != 0)) return;
        var range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = 1, Layers = 1 };
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, DstAccess = Vk.AccessTransferWrite, OldLayout = Vk.LayoutUndefined,
            NewLayout = Vk.LayoutTransferDst, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored,
            Image = image, Range = range
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.StageTransfer, 0, 0, null, 0, null, 1, &barrier);
        var black = stackalloc float[] { 0, 0, 0, 1 };
        VkApi.CmdClearColorImage(commands, image, Vk.LayoutTransferDst, black, 1, &range);
        (barrier.SrcAccess, barrier.DstAccess) = (Vk.AccessTransferWrite, Vk.AccessMemoryRead);
        (barrier.OldLayout, barrier.NewLayout) = (Vk.LayoutTransferDst, Vk.LayoutShaderRead);
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }
}
