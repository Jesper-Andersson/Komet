namespace Komet.Vulkan;

// Vulkan's own buffer standing for an engine buffer (Gl names it; OpenGL keeps its own), in memory of its own or a
// buffer and mapping someone else made and frees (Wrapping)
internal sealed unsafe class SharedBuffer : IDisposable
{
    private const uint Usage = Vk.TransferSrc | Vk.TransferDst | Vk.UsageStorage | Vk.UsageVertex | Vk.UsageIndex |
                               Vk.UsageIndirect;

    private readonly IntPtr _device;
    private ulong _buffer, _memory;
    private bool _mapped, _wrapped;

    private SharedBuffer(IntPtr device, ulong size)
    {
        _ = Assert(device != IntPtr.Zero) && Assert(size > 0);
        (_device, Size) = (device, size);
    }

    public ulong Buffer => _buffer;
    public int Gl { get; private set; }

    // A terrain pool's face data in CompactFaces' 48-byte quads; the engine's buffer is 64/48 of Size
    public bool Packed { get; init; }
    public ulong Size { get; }

    public nint Pointer { get; private set; }

    // Engine buffer gl's contents in Vulkan's memory (mapped: CPU writes too); GL keeps its own buffer
    public static SharedBuffer? Private(VulkanDevice device, ulong size, int gl, bool mapped, out string why, bool packed = false)
    {
        why = "";
        if (!NotNull(device) || !Assert(size is > 0 and <= int.MaxValue) || !Assert(gl > 0)) return null;
        var made = new SharedBuffer(device.Handle, size) { _mapped = mapped, Gl = gl, Packed = packed };
        why = made.Allocate(device) ?? "";
        if (why.Length == 0 && mapped && made.Pointer == 0) why = "no memory the CPU can write for the buffer";
        if (why.Length == 0) return made;
        made.Dispose();
        return null;
    }

    // A buffer and mapping someone else made and frees, standing for the engine buffer gl
    public static SharedBuffer Wrapping(VulkanDevice device, (ulong Buffer, nint Pointer, long Size) made, int gl)
    {
        _ = Assert(made.Buffer != 0) && Assert(made.Size > 0) && Assert(gl > 0);
        return new SharedBuffer(device.Handle, (ulong)made.Size)
        {
            _buffer = made.Buffer, Pointer = made.Pointer, Gl = gl, _wrapped = true, _mapped = true
        };
    }

    private string? Created(VulkanDevice device, out Vk.MemoryRequirements needs, out int type)
    {
        (needs, type) = (default, -1);
        if (!Assert(_buffer == 0) || !NotNull(device)) return "allocated already";
        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Size = Size, Usage = Usage };
        ulong buffer;
        if (VkApi.CreateBuffer(_device, &info, null, &buffer) != Vk.Success) return "vkCreateBuffer failed";
        _buffer = buffer;
        Vk.MemoryRequirements requirements;
        VkApi.GetBufferRequirements(_device, buffer, &requirements);
        var bits = requirements.TypeBits;
        type = device.MemoryType(bits, _mapped ? Vk.DeviceLocal | Vk.HostVisible | Vk.HostCoherent : Vk.DeviceLocal);
        if (type < 0 && _mapped) type = device.MemoryType(bits, Vk.HostVisible | Vk.HostCoherent);
        needs = requirements;
        return Assert(bits != 0) && type >= 0 ? null : "no device-local memory for the buffer";
    }

    private string? Allocate(VulkanDevice device)
    {
        if (Created(device, out var needs, out var type) is { } failed) return failed;
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        ulong memory;
        if (VkApi.AllocateMemory(_device, &allocate, null, &memory) != Vk.Success) return "vkAllocateMemory failed";
        _memory = memory;
        if (VkApi.BindBufferMemory(_device, _buffer, memory, 0) != Vk.Success) return "vkBindBufferMemory failed";
        void* mapped = null;
        if (_mapped && VkApi.MapMemory(_device, memory, 0, needs.Size, 0, &mapped) == Vk.Success) Pointer = (nint)mapped;
        return null;
    }

    // The engine's buffer stays: OpenGL deletes it
    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Gl >= 0)) return;
        if (_buffer != 0 && !_wrapped) VkApi.DestroyBuffer(_device, _buffer, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        (Gl, _buffer, _memory, Pointer) = (0, 0, 0, 0);
    }
}
