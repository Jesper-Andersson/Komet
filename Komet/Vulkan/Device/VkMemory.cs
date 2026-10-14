namespace Komet.Vulkan;

// Freed from the recording thread too, hence the lock.
internal static class VkMemory
{
    private const int MaxLive = 1 << 16;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, (string Owner, ulong Bytes)> Live = [];
    private static readonly Dictionary<string, ulong> ByOwner = new(StringComparer.Ordinal);

    public static ulong Bytes { get; private set; }
    public static long Made { get; private set; }

    public static void Allocated(ulong memory, ulong bytes, string file)
    {
        if (!Assert(memory != 0) || !NotNull(file)) return;
        var owner = Path.GetFileNameWithoutExtension(file);
        lock (Gate)
        {
            if (Live.Count >= MaxLive) return; // past this the report is wrong anyway: the guard has long fired
            Live[memory] = (owner, bytes);
            ByOwner[owner] = ByOwner.GetValueOrDefault(owner) + bytes;
            (Bytes, Made) = (Bytes + bytes, Made + 1);
        }
    }

    public static void Freed(ulong memory)
    {
        if (!Assert(memory != 0)) return;
        lock (Gate)
        {
            if (!Live.Remove(memory, out var was)) return;
            ByOwner[was.Owner] = ByOwner.GetValueOrDefault(was.Owner) - was.Bytes;
            Bytes -= was.Bytes;
            _ = Assert(Bytes < ulong.MaxValue / 2) && Assert(Made >= Live.Count);
        }
    }

    public static string Report()
    {
        lock (Gate)
        {
            var owners = ByOwner.Where(o => o.Value > 0).OrderByDescending(o => o.Value).Take(8)
                .Select(o => $"{o.Key} {o.Value >> 20} MB");
            _ = Assert(Live.Count <= MaxLive);
            return $"{Bytes >> 20} MB in {Live.Count} allocations ({Made} made): {string.Join(", ", owners)}; " +
                   $"process {Environment.WorkingSet >> 20} MB, managed {GC.GetTotalMemory(false) >> 20} MB ({Survived()})";
        }
    }

    // Alive after the last full collection: unlike the managed figure, excludes garbage made since
    internal static string Survived()
    {
        var (full, background) = (GC.GetGCMemoryInfo(GCKind.FullBlocking), GC.GetGCMemoryInfo(GCKind.Background));
        var last = full.Index >= background.Index ? full : background;
        if (last.Index == 0 || !Assert(last.Generation <= 2)) return "no full collection yet";
        var live = 0L;
        foreach (var generation in last.GenerationInfo.Bounded(8))
            live += generation.SizeAfterBytes - generation.FragmentationAfterBytes;
        _ = Assert(live >= 0);
        return $"{live >> 20} MB alive after full collection {GC.CollectionCount(2)}";
    }
}
