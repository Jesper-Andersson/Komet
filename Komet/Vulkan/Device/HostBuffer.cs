namespace Komet.Vulkan;

// Mapped for life, device-local where CPU-visible (resizable BAR). A bulk buffer is only a copy source: cached system
// memory, so CPU writes (and GL readbacks) run at memory speed, not across the bus.
internal sealed unsafe class HostBuffer : IDisposable
{
    private const uint Usage = Vk.UsageUniform | Vk.UsageStorage | Vk.UsageIndirect | Vk.UsageIndex | Vk.UsageVertex |
                               Vk.TransferSrc | Vk.TransferDst;

    private readonly IntPtr _device;
    private ulong _buffer, _memory, _used;
    private byte* _mapped;

    private HostBuffer(IntPtr device, ulong size)
    {
        _ = Assert(device != IntPtr.Zero) && Assert(size > 0);
        (_device, Size) = (device, size);
    }

    public ulong Buffer => _buffer;
    public ulong Size { get; }
    public long Epoch { get; private set; } // Resets so far: an offset taken in one epoch means nothing in the next

    public static HostBuffer? Create(VulkanDevice device, ulong size, bool bulk = false)
    {
        if (!NotNull(device) || !Assert(size is > 0 and <= 1UL << 31)) return null;
        var host = new HostBuffer(device.Handle, size);
        if (host.Allocate(device, bulk)) return host;
        host.Dispose();
        return null;
    }

    private bool Allocate(VulkanDevice device, bool bulk)
    {
        if (!Assert(_buffer == 0) || !NotNull(device)) return false;
        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Size = Size, Usage = Usage };
        ulong buffer, memory;
        if (VkApi.CreateBuffer(_device, &info, null, &buffer) != Vk.Success) return false;
        _buffer = buffer;
        Vk.MemoryRequirements needs;
        VkApi.GetBufferRequirements(_device, buffer, &needs);
        const uint host = Vk.HostVisible | Vk.HostCoherent;
        var type = bulk
            ? device.MemoryType(needs.TypeBits, host | Vk.HostCached, Vk.DeviceLocal)
            : device.MemoryType(needs.TypeBits, host | Vk.DeviceLocal);
        if (type < 0 && bulk) type = device.MemoryType(needs.TypeBits, host, Vk.DeviceLocal);
        if (type < 0) type = device.MemoryType(needs.TypeBits, host);
        _ = Assert(type < 32) && Assert(needs.TypeBits != 0);
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        if (type < 0 || VkApi.AllocateMemory(_device, &allocate, null, &memory) != Vk.Success) return false;
        _memory = memory;
        void* mapped = null;
        if (VkApi.BindBufferMemory(_device, buffer, memory, 0) != Vk.Success ||
            VkApi.MapMemory(_device, memory, 0, needs.Size, 0, &mapped) != Vk.Success) return false;
        _mapped = (byte*)mapped;
        return Assert(needs.Size >= Size);
    }

    public long Take(int bytes, out Span<byte> into, int align = 256)
    {
        into = [];
        if (!Assert(bytes > 0 && align is > 0 and <= 4096) || !Assert(_mapped != null)) return -1;
        var at = (_used + (ulong)align - 1) / (ulong)align * (ulong)align;
        if (at + (ulong)bytes > Size) return -1;
        _used = at + (ulong)bytes;
        into = new Span<byte>(_mapped + at, bytes);
        return (long)at;
    }

    public byte* Pointer(long at) => Assert(at >= 0 && (ulong)at < Size) && Assert(_mapped != null) ? _mapped + at : null;

    public void Reset()
    {
        if (!Assert(_used <= Size) || !Assert(_buffer != 0)) return;
        _used = 0;
        Epoch++;
    }

    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(_used <= Size)) return;
        if (_buffer != 0) VkApi.DestroyBuffer(_device, _buffer, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        (_buffer, _memory) = (0, 0);
        _mapped = null;
    }
}
