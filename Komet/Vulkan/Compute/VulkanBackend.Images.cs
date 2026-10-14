namespace Komet.Vulkan;

// Images are kept in the general layout from their creation on
internal sealed unsafe partial class VulkanBackend
{
    private const uint ImageUsage = Vk.Storage | Vk.Sampled | Vk.TransferDst | Vk.TransferSrc;

    public int Pyramid(int width, int height, out int levels)
    {
        levels = 0;
        if (!Assert(width > 0 && height > 0) || !Assert(_handle != IntPtr.Zero)) return 0;
        levels = 1 + (int)Math.Floor(Math.Log2(Math.Max(width, height)));
        var at = Free(_images);
        if (at == 0 || Create(width, height, levels) is not { } image) return 0;
        _images[at] = image;
        return at;
    }

    // A one-level image of the given size filled with value: depth for tests and callers without a GL depth buffer
    internal int Filled(int width, int height, float value)
    {
        if (!Assert(width > 0 && height > 0) || !Finite(value)) return 0;
        var at = Free(_images);
        if (at == 0 || Create(width, height, 1) is not { } image) return 0;
        _images[at] = image;
        var staging = Buffer();
        var pixels = new float[width * height];
        Array.Fill(pixels, value);
        Upload<float>(staging, pixels);
        var commands = Recording();
        var copy = new Vk.BufferImageCopy
        {
            Offset = _buffers[staging]!.View.Offset, Aspect = Vk.AspectColor, Layers = 1, Width = (uint)width,
            Height = (uint)height, Depth = 1
        };
        VkApi.CmdCopyBufferToImage(commands, _buffers[staging]!.View.Buffer, image.Handle, Vk.LayoutGeneral, 1, &copy);
        Barrier(Gpu.GpuBarrier.Textures);
        Flush();
        DeleteBuffer(ref staging);
        return at;
    }

    private ImageSlot? Create(int width, int height, int levels)
    {
        if (!Assert(levels is > 0 and <= 16) || !Assert(width > 0 && height > 0)) return null;
        var info = new Vk.ImageInfo
        {
            SType = Vk.ImageCreateInfo, ImageType = 1, Format = Vk.FormatR32F, Width = (uint)width,
            Height = (uint)height, Depth = 1, MipLevels = (uint)levels, ArrayLayers = 1, Samples = 1,
            Usage = ImageUsage,
            InitialLayout = Vk.LayoutUndefined
        };
        ulong handle, memory = 0;
        if (VkApi.CreateImage(_handle, &info, null, &handle) != Vk.Success) return null;
        Vk.MemoryRequirements needs;
        VkApi.GetImageMemoryRequirements(_handle, handle, &needs);
        var type = _device.MemoryType(needs.TypeBits, Vk.DeviceLocal);
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        if (type < 0 || VkApi.AllocateMemory(_handle, &allocate, null, &memory) != Vk.Success ||
            VkApi.BindImageMemory(_handle, handle, memory, 0) != Vk.Success)
        {
            VkApi.DestroyImage(_handle, handle, null);
            VkApi.FreeMemory(_handle, memory, null); // 0 when the allocation failed
            return null;
        }

        var image = new ImageSlot(handle, memory, View(handle, 0, levels), new ulong[levels]);
        for (var level = 0; level < Math.Min(levels, 16); level++) image.Levels[level] = View(handle, level, 1);
        General(handle, levels);
        return image;
    }

    private ulong View(ulong image, int level, int count)
    {
        if (!Assert(image != 0) || !Assert(count > 0)) return 0;
        var info = new Vk.ViewInfo
        {
            SType = Vk.ImageViewCreateInfo, Image = image, ViewType = Vk.ViewType2D, Format = Vk.FormatR32F,
            Range = new Vk.ColorRange
            {
                Aspect = Vk.AspectColor, BaseLevel = (uint)level, Levels = (uint)count, Layers = 1
            }
        };
        ulong view;
        return VkApi.CreateImageView(_handle, &info, null, &view) == Vk.Success ? view : 0;
    }

    private void General(ulong image, int levels)
    {
        if (!Assert(image != 0) || !Assert(levels > 0)) return;
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier,
            DstAccess = Vk.AccessShaderRead | Vk.AccessShaderWrite | Vk.AccessTransferWrite,
            OldLayout = Vk.LayoutUndefined, NewLayout = Vk.LayoutGeneral, SrcFamily = Vk.QueueFamilyIgnored,
            DstFamily = Vk.QueueFamilyIgnored, Image = image,
            Range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = (uint)levels, Layers = 1 }
        };
        Record(commands =>
        {
            var moved = barrier;
            VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.PipelineCompute | Vk.StageTransfer, 0, 0, null, 0, null, 1,
                &moved);
        });
    }

    public void DeleteTexture(ref int texture)
    {
        if (!Index(texture, MaxHandles) || texture == 0 || !Assert(texture != _placeholderImage || _disposed)) return;
        Idle();
        if (_images[texture] is { Shared: null } image) DestroyImage(image);
        _images[texture] = null;
        texture = 0;
    }

    private void DestroyImage(ImageSlot image)
    {
        if (!NotNull(image) || !Assert(_handle != IntPtr.Zero)) return;
        foreach (var view in image.Levels.Bounded(16)) VkApi.DestroyImageView(_handle, view, null);
        VkApi.DestroyImageView(_handle, image.All, null);
        VkApi.DestroyImage(_handle, image.Handle, null);
        VkApi.FreeMemory(_handle, image.Memory, null);
    }

    // Shared: an engine's image the frame owns (Engine), not this backend's to destroy
    private sealed record ImageSlot(ulong Handle, ulong Memory, ulong All, ulong[] Levels)
    {
        public SharedImage? Shared { get; init; }
    }
}
