namespace Komet.Vulkan;

// Vulkan-only: glClearTex(Sub)Image of whole levels (any layer run) as vkCmdClearColorImage/DepthStencilImage, value
// read as OpenGL does (zeros for none). Boxes smaller than the level have no such clear and are missing.
internal sealed unsafe partial class TerrainRenderer
{
    private const uint GlStencilIndex = 0x1901, GlDepthStencilFormat = 0x84F9, GlUnsignedInt248 = 0x84FA;
    private const uint GlFloat32Uint248 = 0x8DAD, GlByte = 0x1400, GlShort = 0x1402, GlInt = 0x1404;

    public bool TextureCleared(GlTap.TextureClear clear)
    {
        var images = Images;
        if (!Assert(clear.Level >= 0)) return false;
        var name = (int)clear.Name;
        var image = name > 0 ? Targets.Find(name) ?? images.Get(name, out _, stand: true) : null;
        if (image is null || clear.Level >= image.Levels) return ClearMissing("into a texture with no image or no such level");
        var (width, height) = (Math.Max(image.Width >> clear.Level, 1), Math.Max(image.Height >> clear.Level, 1));
        var box = clear.Box.Width < 0 ? (0, 0, 0, width, height, image.Layers) : clear.Box;
        if (box.Item1 != 0 || box.Item2 != 0 || box.Item4 != width || box.Item5 != height || box.Item3 < 0 || box.Item6 <= 0 ||
            box.Item3 + box.Item6 > image.Layers) return ClearMissing("of part of a level");
        var aspect = ClearedAspect(image.Format.Aspect, clear.Data.Format);
        if (aspect == 0) return ClearMissing($"of 0x{clear.Data.Format:X} into a {image.Format.Vulkan} image");
        var (color, depth) = ClearValue(clear.Data, clear.Value);
        var range = new Vk.ColorRange
        {
            Aspect = aspect, BaseLevel = (uint)clear.Level, Levels = 1, BaseLayer = (uint)box.Item3, Layers = (uint)box.Item6
        };
        if (!Frame.Open && !Frame.Begin(out _)) return ClearMissing("with no segment for it");
        Hold(image);
        if (!Frame.ClearImage(image, range, color, depth)) return ClearMissing("the segment holds no image of");
        images.Wrote(name);
        return Assert(Frame.Open);
    }

    private bool ClearMissing(string why)
    {
        Miss($"a texture clear {why}");
        return !Assert(why.Length > 0) && !Assert(Missed > 0);
    }

    // color for color, depth (and stencil if given and held) for depth; 0 when it does not fit
    private static uint ClearedAspect(uint aspect, uint format)
    {
        _ = Assert(aspect != 0) && Assert(format != uint.MaxValue);
        if ((aspect & Vk.AspectColor) != 0) return format is GlDepthComponent or GlDepthStencilFormat or GlStencilIndex ? 0 : Vk.AspectColor;
        return format switch
        {
            GlDepthComponent => Vk.AspectDepth,
            GlDepthStencilFormat => aspect & Vk.AspectDepthStencil,
            GlStencilIndex => aspect & Vk.AspectStencil,
            _ => 0
        };
    }

    // As OpenGL reads it (none: zeros; missing color components: 0, 0, 0, 1)
    private static ((float R, float G, float B, float A) Color, Vk.DepthStencilValue Depth) ClearValue((uint Format, uint Type) data,
        nint value)
    {
        if (value == 0 || !Assert(data.Format != uint.MaxValue) || !Assert(data.Type != uint.MaxValue)) return (default, default);
        var at = (byte*)value;
        switch (data.Format)
        {
            case GlDepthStencilFormat when data.Type == GlUnsignedInt248:
                return (default, new Vk.DepthStencilValue { Depth = (*(uint*)at >> 8) / (float)0xFFFFFF, Stencil = *(uint*)at & 0xFF });
            case GlDepthStencilFormat when data.Type == GlFloat32Uint248:
                return (default, new Vk.DepthStencilValue { Depth = *(float*)at, Stencil = ((uint*)at)[1] & 0xFF });
            case GlDepthComponent:
                return (default, new Vk.DepthStencilValue { Depth = Component(at, 0, data.Type) });
            case GlStencilIndex:
                var index = data.Type switch
                {
                    GlUnsignedInt or GlInt => *(uint*)at, GlUnsignedShort or GlShort => *(ushort*)at, _ => at[0]
                };
                return (default, new Vk.DepthStencilValue { Stencil = index & 0xFF });
        }

        var count = Components(data.Format);
        Span<float> rgba = [0, 0, 0, 1];
        for (var i = 0; i < Math.Min(count, 4); i++) rgba[i] = Component(at, i, data.Type);
        if (data.Format is GlBgra or GlBgr) (rgba[0], rgba[2]) = (rgba[2], rgba[0]);
        return ((rgba[0], rgba[1], rgba[2], rgba[3]), default);
    }

    private static float Component(byte* at, int index, uint type)
    {
        if (!Assert(at != null) || !Assert(index is >= 0 and < 4)) return 0;
        return type switch
        {
            GlFloat => ((float*)at)[index],
            GlHalfFloat => (float)((Half*)at)[index],
            GlUnsignedShort => ((ushort*)at)[index] / 65535f,
            GlShort => Math.Max(((short*)at)[index] / 32767f, -1),
            GlUnsignedInt => (float)(((uint*)at)[index] / (double)uint.MaxValue),
            GlInt => (float)Math.Max(((int*)at)[index] / (double)int.MaxValue, -1),
            GlByte => Math.Max(((sbyte*)at)[index] / 127f, -1),
            _ => at[index] / 255f
        };
    }
}
