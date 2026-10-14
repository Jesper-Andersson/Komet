namespace Komet.Vulkan;

// Pool uploads (PoolWritten) copied from frame host memory: in the open segment, else in order into the next segment's
// setup or, at frame end, a transfer of their own (PoolsAlone)
internal sealed partial class TerrainRenderer
{
    private const int MaxPoolCopies = 1024;

    private readonly List<PoolCopy> _poolCopies = [];

    private readonly record struct PoolCopy(HostBuffer From, long At, SharedBuffer To, long Offset, int Size);

    // TerrainRebuild: all pools go, and so do pending writes (their buffers would be gone)
    public void PoolWritesDropped()
    {
        _poolCopies.Clear();
        _ = Assert(_poolCopies.Count == 0) && Assert(MaxPoolCopies > 0);
    }

    public void PoolsOpened()
    {
        if (_poolCopies.Count == 0 || !Assert(Frame.Open)) return;
        PoolsRecorded(Frame.Setup);
    }

    // In order into a segment's setup or their own transfer
    private unsafe void PoolsRecorded(IntPtr setup)
    {
        if (!Assert(setup != IntPtr.Zero) || !Assert(_poolCopies.Count <= MaxPoolCopies)) return;
        var written = 0; // the copies since the last barrier: _poolCopies[first..]
        var first = 0;
        for (var c = 0; c < Math.Min(_poolCopies.Count, MaxPoolCopies); c++)
        {
            var copy = _poolCopies[c];
            if (Overlaps(copy, first, written))
            {
                var memory = new Vk.GlobalBarrier
                {
                    SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite, DstAccess = Vk.AccessTransferWrite
                };
                VkApi.CmdPipelineBarrier(setup, Vk.StageTransfer, Vk.StageTransfer, 0, 1, &memory, 0, null, 0, null);
                (first, written) = (c, 0); // the same range written twice: the later copy after the earlier one
            }

            var region = new Vk.BufferCopy
            {
                SourceOffset = (ulong)copy.At, TargetOffset = (ulong)copy.Offset, Size = (ulong)copy.Size
            };
            VkApi.CmdCopyBuffer(setup, copy.From.Buffer, copy.To.Buffer, 1, &region); // the setup's barriers order the rest
            written++;
        }

        _poolCopies.Clear();
    }

    private bool Overlaps(PoolCopy copy, int first, int written)
    {
        if (!NotNull(copy.To) || !Assert(first + written <= _poolCopies.Count)) return false;
        for (var i = first; i < Math.Min(first + written, MaxPoolCopies); i++)
        {
            var w = _poolCopies[i];
            if (ReferenceEquals(w.To, copy.To) && w.Offset < copy.Offset + copy.Size && copy.Offset < w.Offset + w.Size)
                return true;
        }

        return false;
    }
}
