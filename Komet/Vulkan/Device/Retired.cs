namespace Komet.Vulkan;

// What an in-flight frame may read (image, buffer, heap range), kept with its last frame and freed once that is done,
// no device wait. Full (max): Add says so, the caller frees it itself.
internal sealed class Retired<T>(int max, Action<T> free)
{
    private const int MaxItems = 1 << 16;

    private readonly List<(T Item, long Frame)> _items = [];

    public int Count => _items.Count;

    public bool Add(T item, long frame)
    {
        if (!Assert(_items.Count <= max) || _items.Count >= Math.Min(max, MaxItems)) return false;
        _items.Add((item, frame));
        return Assert(frame >= -VulkanFrame.Slots);
    }

    // Those whose frame is done go; the others keep their order
    public void Collect(long done)
    {
        if (_items.Count == 0 || !Assert(done >= -VulkanFrame.Slots)) return;
        var kept = 0;
        for (var i = 0; i < Math.Min(_items.Count, MaxItems); i++)
        {
            if (_items[i].Frame <= done) free(_items[i].Item);
            else _items[kept++] = _items[i];
        }

        _items.RemoveRange(kept, _items.Count - kept);
        _ = Assert(kept <= max);
    }

    // With the device idle: every one goes (free: false, gone with what holds them, as a heap's blocks)
    public void Clear(bool freed = true)
    {
        _ = NotNull(free) && Assert(_items.Count <= MaxItems);
        if (freed)
            foreach (var (item, _) in _items.Bounded(MaxItems))
                free(item);
        _items.Clear();
    }
}
