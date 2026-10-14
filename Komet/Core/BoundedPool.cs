namespace Komet.Core;

// At most max items under one lock, no allocation once warm (ConcurrentStack allocates per push, walks for Count)
internal sealed class BoundedPool<T>(int max) where T : class
{
    private readonly Stack<T> _items = new();
    private readonly Lock _lock = new();

    public T? Take()
    {
        lock (_lock)
        {
            _ = Assert(_items.Count <= max);
            return _items.TryPop(out var item) && NotNull(item) ? item : null;
        }
    }

    public void Give(T item)
    {
        if (!NotNull(item) || !Assert(max > 0)) return;
        lock (_lock)
            if (_items.Count < max)
                _items.Push(item);
    }
}
