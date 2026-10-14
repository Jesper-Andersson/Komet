namespace Komet.Vulkan;

// The free ranges of a block, sorted by offset (DeviceHeap): a range given back merges with its neighbours
internal static class FreeList
{
    public const int MaxRanges = 1 << 14;

    public static void Give(List<(ulong At, ulong Size)> free, ulong at, ulong size)
    {
        if (!NotNull(free) || !Assert(size > 0) || !Assert(free.Count < MaxRanges)) return;
        var i = free.Count;
        for (var f = 0; f < Math.Min(free.Count, MaxRanges); f++)
            if (free[f].At > at)
            {
                i = f;
                break;
            }

        free.Insert(i, (at, size));
        if (i + 1 < free.Count && free[i].At + free[i].Size == free[i + 1].At)
        {
            free[i] = (free[i].At, free[i].Size + free[i + 1].Size);
            free.RemoveAt(i + 1);
        }

        if (i > 0 && free[i - 1].At + free[i - 1].Size == free[i].At)
        {
            free[i - 1] = (free[i - 1].At, free[i - 1].Size + free[i].Size);
            free.RemoveAt(i);
        }
    }
}
