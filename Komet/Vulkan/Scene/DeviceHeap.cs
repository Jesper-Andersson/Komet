namespace Komet.Vulkan;

// Returned ranges wait for their last frame (Retire). Plain, non-exportable allocations: free to submit.
internal sealed unsafe class DeviceHeap(VulkanDevice device) : IDisposable
{
    public const ulong BlockBytes = 64UL << 20;
    private const int MaxBlocks = 256, MaxRanges = 1 << 14, MaxRetired = 1 << 16;
    private const uint Usage = Vk.UsageVertex | Vk.UsageIndex | Vk.UsageUniform | Vk.UsageStorage | Vk.UsageIndirect |
                               Vk.TransferSrc | Vk.TransferDst;

    private readonly List<Block> _blocks = [];
    private Retired<Range>? _retiredList;
    private Retired<Range> Retiring => _retiredList ??= new Retired<Range>(MaxRetired, Give);

    public sealed class Block(ulong buffer, ulong memory, ulong size)
    {
        public ulong Buffer { get; } = buffer;
        public ulong Memory { get; } = memory;
        public ulong Size { get; } = size;
        public List<(ulong At, ulong Size)> Free { get; } = [(0, size)];
        public int Taken { get; set; }
    }

    public readonly record struct Range(Block Block, ulong Offset, ulong Size)
    {
        public ulong Buffer => Block.Buffer;
    }

    public ulong Bytes => (ulong)_blocks.Sum(b => (decimal)b.Size);

    public Range? Take(ulong size)
    {
        if (!Assert(size > 0) || !Assert(_blocks.Count <= MaxBlocks)) return null;
        size = (size + 255) / 256 * 256;
        foreach (var block in _blocks.Bounded(MaxBlocks))
            if (Carve(block, size) is { } at)
                return new Range(block, at, size);
        var made = Made(Math.Max(size, BlockBytes));
        if (made is null) return null;
        _blocks.Add(made);
        return Carve(made, size) is { } first ? new Range(made, first, size) : null;
    }

    public void Retire(Range range, long frame)
    {
        if (!NotNull(range.Block) || !Assert(frame >= 0)) return;
        if (!Retiring.Add(range, frame)) Give(range); // should not happen: nothing collects
    }

    public void Collect(long done)
    {
        if (!Assert(Retiring.Count <= MaxRetired) || !Assert(done >= -VulkanFrame.Slots) || Retiring.Count == 0) return;
        Retiring.Collect(done);
        Release();
    }

    // Empty blocks (Taken 0) go back to the driver but one (so the next mirror need not allocate): a burst of large
    // mirrors leaves no lasting high-water mark.
    private void Release()
    {
        var spare = false;
        _ = Assert(_blocks.Count <= MaxBlocks) && NotNull(device);
        for (var n = 0; n < MaxBlocks; n++)
        {
            var i = _blocks.Count - 1 - n;
            if (i < 0) break;
            var block = _blocks[i];
            if (block.Taken != 0) continue;
            if (!spare)
            {
                spare = true;
                continue;
            }

            _ = Assert(block.Free.Count == 1) && Assert(block.Size > 0);
            VkApi.DestroyBuffer(device.Handle, block.Buffer, null);
            VkApi.FreeMemory(device.Handle, block.Memory, null);
            _blocks.RemoveAt(i);
            Released++;
        }

        _ = Assert(_blocks.Count <= MaxBlocks);
    }

    public long Released { get; private set; }

    private static ulong? Carve(Block block, ulong size)
    {
        if (!NotNull(block) || !Assert(block.Free.Count <= MaxRanges)) return null;
        for (var i = 0; i < Math.Min(block.Free.Count, MaxRanges); i++)
        {
            var (at, length) = block.Free[i];
            if (length < size) continue;
            if (length == size) block.Free.RemoveAt(i);
            else block.Free[i] = (at + size, length - size);
            block.Taken++;
            return at;
        }

        return null;
    }

    private static void Give(Range range)
    {
        var block = range.Block;
        if (!NotNull(block) || !Assert(block.Taken > 0) || !Assert(range.Offset + range.Size <= block.Size)) return;
        block.Taken--;
        FreeList.Give(block.Free, range.Offset, range.Size);
    }

    private Block? Made(ulong size)
    {
        if (!Assert(_blocks.Count < MaxBlocks) || !Assert(size > 0)) return null;
        var handle = device.Handle;
        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Size = size, Usage = Usage };
        ulong buffer, memory;
        if (VkApi.CreateBuffer(handle, &info, null, &buffer) != Vk.Success) return null;
        Vk.MemoryRequirements needs;
        VkApi.GetBufferRequirements(handle, buffer, &needs);
        var type = device.MemoryType(needs.TypeBits, Vk.DeviceLocal);
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        if (type >= 0 && VkApi.AllocateMemory(handle, &allocate, null, &memory) == Vk.Success)
        {
            if (VkApi.BindBufferMemory(handle, buffer, memory, 0) == Vk.Success) return new Block(buffer, memory, size);
            VkApi.FreeMemory(handle, memory, null);
        }

        VkApi.DestroyBuffer(handle, buffer, null);
        return null;
    }

    public void Dispose()
    {
        _ = Assert(_blocks.Count <= MaxBlocks) && Assert(Retiring.Count <= MaxRetired) && device.WaitIdle();
        foreach (var block in _blocks.Bounded(MaxBlocks))
        {
            VkApi.DestroyBuffer(device.Handle, block.Buffer, null);
            VkApi.FreeMemory(device.Handle, block.Memory, null);
        }

        _blocks.Clear();
        Retiring.Clear(freed: false); // the blocks went whole
    }
}
