namespace Komet.Vulkan;

// What a Vulkan-only device offers mods' raw GL: a depth-stencil format (DEPTH24_STENCIL8 as D24S8 if drawable and
// sampleable, else D32S8), 8-bit indices, instance divisors past 1, multi-draw indirect, and extra blit formats
internal sealed unsafe partial class VulkanDevice
{
    private static readonly uint[] Asked =
        [Vk.FormatD24S8, Vk.FormatD32S8, Vk.FormatA2B10G10R10, Vk.FormatB10G11R11F, Vk.FormatR16, Vk.FormatRgba16];

    // The last device's optimal-tiling features of the formats Asked (one device runs at a time)
    private static readonly Dictionary<uint, uint> Optimal = [];

    // The Vulkan format a GL depth and stencil format gets, 0 for none
    public uint DepthStencil { get; private set; }

    public bool IndexUint8 { get; private set; }
    public bool Divisors { get; private set; }

    // multiDrawIndirect: an indirect draw takes several commands at once
    public bool MultiDraws { get; private set; }

    private void Formats()
    {
        if (!Assert(_physical != IntPtr.Zero) || VkApi.GetFormatProperties == null) return;
        Optimal.Clear();
        foreach (var format in Asked.Bounded(Asked.Length))
        {
            Vk.FormatProperties properties;
            VkApi.GetFormatProperties(_physical, format, &properties);
            Optimal[format] = properties.Optimal;
        }

        const uint wanted = Vk.FeatureDepthStencilAttachment | Vk.FeatureSampled;
        DepthStencil = (Optimal[Vk.FormatD32S8] & wanted) == wanted ? Vk.FormatD32S8 : 0;
        if ((Optimal[Vk.FormatD24S8] & wanted) == wanted) DepthStencil = Vk.FormatD24S8;
        var gl = DepthStencil == Vk.FormatD24S8 ? 0x88F0 : 0x8CAD; // GL_DEPTH24_STENCIL8, GL_DEPTH32F_STENCIL8
        SharedFormat.Stencils = DepthStencil != 0 ? new SharedFormat(DepthStencil, gl, Vk.AspectDepthStencil) : null;
        _ = Assert(Optimal.Count == Asked.Length);
    }

    // The extensions wanted beside those every device is made with
    private string[] Extras(string[] wanted)
    {
        if (DepthClipControl) wanted = [.. wanted, ClipControl];
        if (BresenhamLines) wanted = [.. wanted, LineRasterization];
        if (Swapchains) wanted = [.. wanted, Vk.SwapchainExtension];
        if (IndexUint8) wanted = [.. wanted, Vk.IndexUint8Extension];
        if (Divisors) wanted = [.. wanted, Vk.DivisorExtension];
        return Assert(wanted.Length <= MaxWanted) && Assert(_physical != IntPtr.Zero) ? wanted : [];
    }

    // Whether the device blits from (into: to) the format, of those Asked; false for any other
    public static bool Blits(uint format, bool into) =>
        Assert(format != uint.MaxValue) && Optimal.TryGetValue(format, out var features) && Assert(Optimal.Count <= 16) &&
        (features & (into ? Vk.FeatureBlitTarget : Vk.FeatureBlitSource)) != 0;
}
