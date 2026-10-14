using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// A Vulkan format and the GL internal format it stands for
internal readonly record struct SharedFormat(uint Vulkan, int Gl, uint Aspect)
{
    public static readonly SharedFormat Rgba8 = new(Vk.FormatRgba8, 0x8058, Vk.AspectColor);
    public static readonly SharedFormat Rgba16F = new(Vk.FormatRgba16F, 0x881A, Vk.AspectColor);
    public static readonly SharedFormat Depth32F = new(Vk.FormatD32F, 0x8CAC, Vk.AspectDepth);
    public static readonly SharedFormat R16F = new(Vk.FormatR16F, 0x822D, Vk.AspectColor);
    public static readonly SharedFormat Rgba32F = new(Vk.FormatRgba32F, 0x8814, Vk.AspectColor);
    public static readonly SharedFormat R8 = new(Vk.FormatR8, 0x8229, Vk.AspectColor);
    public static readonly SharedFormat Rg16F = new(Vk.FormatRg16F, 0x822F, Vk.AspectColor);
    public static readonly SharedFormat R32F = new(Vk.FormatR32F, 0x822E, Vk.AspectColor);
    public static readonly SharedFormat Rgba16 = new(Vk.FormatRgba16, 0x805B, Vk.AspectColor);
    public static readonly SharedFormat Rg8 = new(Vk.FormatRg8, 0x822B, Vk.AspectColor);
    public static readonly SharedFormat Srgba8 = new(Vk.FormatSrgba8, 0x8C43, Vk.AspectColor);
    public static readonly SharedFormat R16 = new(Vk.FormatR16, 0x822A, Vk.AspectColor);
    public static readonly SharedFormat Rgb10A2 = new(Vk.FormatA2B10G10R10, 0x8059, Vk.AspectColor);
    public static readonly SharedFormat R11G11B10F = new(Vk.FormatB10G11R11F, 0x8C3A, Vk.AspectColor);

    // How glGetTextureImage gives each format exactly as the image holds it
    public (PixelFormat Format, PixelType Type, int Bytes)? Pack => Vulkan switch
    {
        Vk.FormatRgba8 or Vk.FormatSrgba8 => (PixelFormat.Rgba, PixelType.UnsignedByte, 4),
        Vk.FormatRgba16F => (PixelFormat.Rgba, PixelType.HalfFloat, 8),
        Vk.FormatD32F => (PixelFormat.DepthComponent, PixelType.Float, 4),
        Vk.FormatR16F => (PixelFormat.Red, PixelType.HalfFloat, 2),
        Vk.FormatRgba32F => (PixelFormat.Rgba, PixelType.Float, 16),
        Vk.FormatR8 => (PixelFormat.Red, PixelType.UnsignedByte, 1),
        Vk.FormatRg16F => (PixelFormat.Rg, PixelType.HalfFloat, 4),
        Vk.FormatR32F => (PixelFormat.Red, PixelType.Float, 4),
        Vk.FormatRgba16 => (PixelFormat.Rgba, PixelType.UnsignedShort, 8),
        Vk.FormatRg8 => (PixelFormat.Rg, PixelType.UnsignedByte, 2),
        Vk.FormatR16 => (PixelFormat.Red, PixelType.UnsignedShort, 2),
        Vk.FormatA2B10G10R10 => (PixelFormat.Rgba, (PixelType)0x8368, 4), // GL_UNSIGNED_INT_2_10_10_10_REV
        Vk.FormatB10G11R11F => (PixelFormat.Rgb, (PixelType)0x8C3B, 4), // GL_UNSIGNED_INT_10F_11F_11F_REV
        _ => null
    };

    // The depth and stencil format, as the device has one (VulkanDevice.Formats), null where it has none
    public static SharedFormat? Stencils { get; set; }

    // All formats Of knows and more; Opaque: RGB as RGBA (alpha 1); compressed as RGBA8 (read
    // back decompressed); depth/stencil (or stencil alone) as the device's depth-stencil format
    public static (SharedFormat Format, bool Opaque)? Only(int gl)
    {
        _ = Assert(gl >= 0) && Assert(Rg8.Aspect == Vk.AspectColor);
        if (gl is 0x88F0 or 0x84F9 or 0x8CAD or 0x8D48) return Stencils is { } stencils ? (stencils, false) : null;
        return gl switch
        {
            0x1907 or 0x8051 => (Rgba8, true), // RGB, RGB8
            0x881B => (Rgba16F, true), // RGB16F
            0x8815 => (Rgba32F, true), // RGB32F
            0x8C41 or 0x8C43 => (Srgba8, gl == 0x8C41), // SRGB8, SRGB8_ALPHA8
            0x822B or 0x8227 => (Rg8, false), // RG8, RG
            0x1903 => (R8, false), // RED
            0x822A => (R16, false),
            0x8059 => (Rgb10A2, false),
            0x8C3A => (R11G11B10F, false),
            0x81A5 => (Depth32F, false), // DEPTH_COMPONENT16, held as 32-bit float depth
            >= 0x83F0 and <= 0x83F3 or >= 0x8E8C and <= 0x8E8F or >= 0x8DBB and <= 0x8DBE or 0x84ED or 0x84EE =>
                (Rgba8, false), // S3TC, BPTC, RGTC, generic compressed
            _ => Of(gl) is { } known ? (known, false) : null
        };
    }

    public static SharedFormat? Of(int gl)
    {
        _ = Assert(gl >= 0) && Assert(Rgba8.Gl == 0x8058);
        return gl switch
        {
            0x8058 or 0x1908 => Rgba8, // RGBA8, and the unsized RGBA drivers make RGBA8
            0x881A => Rgba16F,
            0x822D => R16F,
            0x8814 => Rgba32F,
            0x8229 => R8,
            0x822F => Rg16F,
            0x822E => R32F,
            0x805B => Rgba16,
            0x8CAC or 0x81A7 or 0x1902 or 0x81A6 => Depth32F, // DEPTH_COMPONENT32F, 32, unsized, 24
            _ => null
        };
    }
}

// Vulkan's own image of a GL texture (Texture names it, never deleted with it): no GL texture of its own, memory no
// submission names, neither exportable nor dedicated (only such memory RADV keeps local)
internal sealed unsafe class SharedImage : IDisposable
{
    private readonly IntPtr _device;
    private ulong _image, _memory, _view;

    private readonly ulong[] _layerViews = new ulong[MaxLayers];

    private const int MaxLayers = 16;

    private SharedImage(IntPtr device, (int Width, int Height, int Levels, int Layers) size, SharedFormat format)
    {
        _ = Assert(device != IntPtr.Zero) && Assert(size.Levels is > 0 and <= 16);
        (_device, Width, Height, Levels, Format) = (device, size.Width, size.Height, size.Levels, format);
        Layers = Math.Clamp(size.Layers, 1, MaxLayers);
    }

    public ulong Image => _image;
    public int Texture { get; private set; }

    public ulong View
    {
        get
        {
            if (_view == 0 && Assert(_image != 0)) _view = MadeView(0, Whole);
            return _view;
        }
    }

    // Sampled with alpha 1, as GL samples the RGB texture this image stands in for (its GL texture swizzles the same)
    public bool OpaqueAlpha { get; set; }

    // Depth-stencil is sampled via depth alone (one aspect per view); Window: only through the levels it names
    public ulong Sampled
    {
        get
        {
            if (Window is { } window && (window.First > 0 || window.Count < Levels)) return Ranged(window);
            if (!OpaqueAlpha && Format.Aspect == SampledAspect) return View;
            if (_sampled == 0 && Assert(_image != 0))
                _sampled = MadeView(OpaqueAlpha ? SwizzleOne : 0, Whole with { Aspect = SampledAspect });
            return _sampled;
        }
    }

    private const uint SwizzleOne = 2;
    private ulong _sampled;

    private uint SampledAspect => (Format.Aspect & Vk.AspectDepth) != 0 ? Vk.AspectDepth : Format.Aspect;

    // Vulkan-only: sampled through these levels only (a snapshot's: its texture's base to max level), null for all
    public (int First, int Count)? Window { get; set; }

    private ulong Ranged((int First, int Count) window)
    {
        var first = Math.Clamp(window.First, 0, Levels - 1);
        var count = Math.Clamp(window.Count, 1, Levels - first);
        var view = Cached(OpaqueAlpha ? SwizzleOne : 0, Whole with { Aspect = SampledAspect, BaseLevel = (uint)first, Levels = (uint)count });
        return Assert(count > 0) && view != 0 ? view : View;
    }

    private const int MaxViews = (MaxLayers + 1) * 16 + 64;

    // Views made on demand (Vulkan-only's levels, layered and windowed views), one for each range, swizzle and kind
    private Dictionary<(uint Aspect, uint BaseLevel, uint Levels, uint BaseLayer, uint Layers, uint Alpha, bool Layered), ulong>? _views;

    private ulong Cached(uint alpha, Vk.ColorRange range, bool layered = false)
    {
        _views ??= [];
        var key = (range.Aspect, range.BaseLevel, range.Levels, range.BaseLayer, range.Layers, alpha, layered);
        if (_views.TryGetValue(key, out var view)) return view;
        if (!Assert(_image != 0) || !Assert(_views.Count <= MaxViews) || _views.Count == MaxViews) return 0;
        _views[key] = view = MadeView(alpha, range, layered);
        return view;
    }

    private Vk.ColorRange Whole => new() { Aspect = Format.Aspect, Levels = (uint)Levels, Layers = (uint)Layers };

    // Level 0 of one layer, as a framebuffer attaches it (FramebufferTextureLayer); the whole view for a plain image
    public ulong LayerView(int layer)
    {
        if (Layers == 1 || !Index(layer, Layers)) return View;
        if (_layerViews[layer] == 0 && Assert(_image != 0))
            _layerViews[layer] = MadeView(0,
                new Vk.ColorRange { Aspect = Format.Aspect, Levels = 1, BaseLayer = (uint)layer, Layers = 1 });
        return _layerViews[layer];
    }

    // One level of one layer, as FramebufferTexture* with a level attaches it (Vulkan-only)
    public ulong LevelView(int level, int layer)
    {
        if (!Index(level, Levels) || !Index(layer, Layers)) return 0;
        if (Levels == 1 || (level == 0 && Layers > 1)) return LayerView(layer);
        return Cached(0, new Vk.ColorRange { Aspect = Format.Aspect, BaseLevel = (uint)level, Levels = 1, BaseLayer = (uint)layer, Layers = 1 });
    }

    // Vulkan-only: one level, all layers, for layered framebuffers; gl_Layer picks (default 0)
    public ulong LayeredView(int level)
    {
        if (!Index(level, Levels) || !Assert(_image != 0)) return 0;
        return Cached(0, new Vk.ColorRange { Aspect = Format.Aspect, BaseLevel = (uint)level, Levels = 1, Layers = (uint)Layers },
            layered: true);
    }

    private ulong MadeView(uint alpha, Vk.ColorRange range, bool layered = false)
    {
        if (!Assert(_image != 0) || !Assert(range.Layers > 0)) return 0;
        var type = range.Layers > 1 || (Arrayed && range.Layers == (uint)Layers) ? Vk.ViewType2DArray : Vk.ViewType2D;
        var info = new Vk.ViewInfo
        {
            SType = Vk.ImageViewCreateInfo, Image = _image,
            ViewType = Cube && range.Layers == 6 && !layered ? Vk.ViewTypeCube : type, Format = Format.Vulkan, A = alpha,
            Range = range
        };
        ulong view;
        return VkApi.CreateImageView(_device, &info, null, &view) == Vk.Success ? view : 0;
    }

    public Handoff.Image In(int layout) =>
        Assert(layout >= 0) ? new Handoff.Image(_image, layout, Format.Aspect, Levels) { Layers = Layers } : default;

    // Six layers sampled as a cube (a GL cube map's copy); a layer view stays 2D
    public bool Cube { get; private init; }

    // Sampled as an array even with one layer (a GL 2D array texture's image, Vulkan-only)
    public bool Arrayed { get; private init; }

    // The last frame (VulkanFrame.Number) whose draws sampled it: the texture copy unused longest makes room first
    public long Used { get; set; } = -1;
    public int Layers { get; }
    public int Width { get; }
    public int Height { get; }
    public int Levels { get; }
    public SharedFormat Format { get; }

    // stands: the GL texture whose contents it holds, named by Texture, never deleted with it
    public static SharedImage? Private(VulkanDevice device, (int Width, int Height, int Levels, int Layers) size,
        SharedFormat format, uint usage, out string why, bool cube = false, int stands = 0, bool arrayed = false)
    {
        why = "";
        if (!NotNull(device) || !Assert(size.Width > 0 && size.Height > 0) || !Assert(size.Levels is > 0 and <= 16) ||
            !Assert(size.Layers is > 0 and <= MaxLayers) || !Assert(!cube || size.Layers == 6) || !Assert(stands >= 0))
            return null;
        var image = new SharedImage(device.Handle, size, format) { Cube = cube, Texture = stands, Arrayed = arrayed };
        why = image.Allocate(device, usage) ?? "";
        if (why.Length == 0) return image;
        image.Dispose();
        return null;
    }

    private string? Allocate(VulkanDevice device, uint usage)
    {
        if (!Assert(_image == 0) || !Assert(usage != 0)) return "allocated already";
        var info = new Vk.ImageInfo
        {
            SType = Vk.ImageCreateInfo, Flags = Cube ? Vk.CubeCompatible : 0,
            ImageType = 1, Format = Format.Vulkan,
            Width = (uint)Width,
            Height = (uint)Height, Depth = 1, MipLevels = (uint)Levels, ArrayLayers = (uint)Layers, Samples = 1,
            Usage = usage,
            InitialLayout = Vk.LayoutUndefined
        };
        ulong image, memory;
        if (VkApi.CreateImage(_device, &info, null, &image) != Vk.Success) return "vkCreateImage failed";
        _image = image;
        Vk.MemoryRequirements needs;
        VkApi.GetImageMemoryRequirements(_device, image, &needs);
        var type = device.MemoryType(needs.TypeBits, Vk.DeviceLocal);
        if (type < 0) return "no device-local memory for the image";
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        if (VkApi.AllocateMemory(_device, &allocate, null, &memory) != Vk.Success) return "vkAllocateMemory failed";
        _memory = memory;
        return VkApi.BindImageMemory(_device, image, memory, 0) == Vk.Success ? null : "vkBindImageMemory failed";
    }

    // A level's width and height, at least 1 each
    public (int Width, int Height) LevelSize(int level) =>
        Assert(level is >= 0 and < 32) && Assert(Width > 0) ? (Math.Max(Width >> level, 1), Math.Max(Height >> level, 1)) : (1, 1);

    // A barrier over every level and layer between two layouts, all memory access on both sides
    public Vk.ImageBarrier Barrier((int From, int To) layouts)
    {
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        _ = Assert(Image != 0) && Assert(layouts.To != layouts.From);
        return new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = layouts.From, NewLayout = layouts.To,
            SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = Image,
            Range = new Vk.ColorRange { Aspect = Format.Aspect, Levels = (uint)Levels, Layers = (uint)Layers }
        };
    }

    // Recorded: the whole image moved between two layouts, after and before all work
    public void Moved(IntPtr commands, (int From, int To) layouts)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(layouts.From != layouts.To)) return;
        var barrier = Barrier(layouts);
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }

    // On the device at once: current layout (Undefined: new) to resting one, before any segment takes it
    public bool Rest(VulkanDevice device, int from, int layout)
    {
        if (!NotNull(device) || !Assert(layout > 0) || from == layout) return from == layout;
        var barrier = Barrier((from, layout));
        if (from == Vk.LayoutUndefined) barrier.SrcAccess = 0;
        var stage = from == Vk.LayoutUndefined ? Vk.StageTop : Vk.StageAll;
        return device.Run(commands =>
        {
            var b = barrier;
            VkApi.CmdPipelineBarrier(commands, stage, Vk.StageAll, 0, 0, null, 0, null, 1, &b);
        }, []);
    }

    // The GL texture it stands for stays: its owner deletes it
    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Texture >= 0)) return;
        if (_view != 0) VkApi.DestroyImageView(_device, _view, null);
        if (_sampled != 0) VkApi.DestroyImageView(_device, _sampled, null);
        _sampled = 0;
        foreach (var view in _layerViews.Bounded(MaxLayers))
            if (view != 0)
                VkApi.DestroyImageView(_device, view, null);
        Array.Clear(_layerViews);
        foreach (var view in (_views?.Values.ToArray() ?? []).Bounded(MaxViews))
            if (view != 0)
                VkApi.DestroyImageView(_device, view, null);
        _views = null;
        if (_image != 0) VkApi.DestroyImage(_device, _image, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        (Texture, _image, _memory, _view) = (0, 0, 0, 0);
    }
}
