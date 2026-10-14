using Komet.Core;
using Vintagestory.API.Datastructures;

namespace Komet.Tessellation;

// Where the player stands and looks, in chunks: what the queue orders by
internal readonly record struct TessView(int X, int Y, int Z, double Fx, double Fz);

// Normal tessellation passes, lowest Score first, with the engine's semantics: keys are index3d; an edge-only mark
// (sign bit set) is dropped while the full pass waits, a full mark upgrades a waiting edge-only one. One lock for all:
// the tessellation thread fills, takes and defers, TessWorkers take and hand back (Home). A chunk is on one thread at a
// time: new marks wait until Done, since concurrent passes race to the upload queue and the older mesh landing last
// would undo a block edit. Count is published per change for lock-free main-thread reads.
internal sealed class TessQueue
{
    public const int MaxPending = 1 << 20;

    // Within NearChunks direction does not matter: the player may turn at once
    private const int MaxStale = 1 << 16, NearChunks = 2;

    private const int SeqBits = 24, SeqMask = (1 << SeqBits) - 1;
    private const int MaxFlights = WorkerPool.MaxThreads + 1; // one chunk per tessellation thread

    private readonly Lock _gate = new();
    private readonly PriorityQueue<(long Index, long Version), long> _heap = new();

    // Passes a worker could not finish, for the tessellation thread (TryTakeHome)
    private readonly List<long> _home = [];

    // Blocked: pending while in flight, pushed again by Done
    private readonly HashSet<long> _inFlight = [], _blocked = [];

    private readonly Dictionary<long, Entry> _pending = [];
    private int _count;
    private List<long> _deferred = [], _returning = [];
    private long _seq, _version, _mulX = 1, _mulZ = 1;
    private TessView _view;

    public int Count => Volatile.Read(ref _count); // waiting, handed back and deferred marks

    // The map's index multipliers (index3dMulX, index3dMulZ): how an index splits into chunk coordinates
    public void Map(long mulX, long mulZ)
    {
        if (!Assert(mulX > 0) || !Assert(mulZ > 0)) return;
        lock (_gate) (_mulX, _mulZ) = (mulX, mulZ);
    }

    public void AddRange(ReadOnlySpan<long> raws)
    {
        _ = Assert(raws.Length <= MaxPending);
        lock (_gate)
        {
            for (var i = 0; i < Math.Min(raws.Length, MaxPending); i++) Add(raws[i]);
            Publish();
        }
    }

    // The next chunk to tessellate, in flight until Done or Home; raw is what the engine's queue held
    public bool TryTake(out long raw)
    {
        raw = 0;
        lock (_gate)
        {
            var nodes = _heap.Count; // shrinks as the loop takes
            _ = Assert(nodes <= MaxPending + MaxStale); // Push rebuilds before the superseded nodes pass MaxStale
            for (var i = 0; i < Math.Min(nodes, MaxPending + MaxStale) && _heap.TryDequeue(out var node, out _); i++)
            {
                if (!_pending.TryGetValue(node.Index, out var entry) || entry.Version != node.Version)
                    continue; // superseded
                if (_inFlight.Contains(node.Index))
                {
                    _ = _blocked.Add(node.Index);
                    continue;
                }

                _ = _pending.Remove(node.Index);
                _ = _inFlight.Add(node.Index);
                raw = Raw(node.Index, entry);
                Publish();
                return Assert(node.Index >= 0) && Assert(_inFlight.Count <= MaxFlights);
            }
        }

        return false;
    }

    // A pass not from the queue (priority mark): in flight now, false while another thread has the chunk
    public bool TryBegin(long index)
    {
        if (!Assert(index >= 0)) return false;
        lock (_gate)
        {
            var begun = _inFlight.Add(index);
            return Assert(_inFlight.Count <= MaxFlights) && begun;
        }
    }

    // A taken or begun chunk's pass ended; a mark that arrived meanwhile becomes takeable (no-op after a Clear)
    public void Done(long index)
    {
        lock (_gate) _ = Release(index);
    }

    // A worker's retry: done, then the tessellation thread's (RetryTesselationException fixes queue only from there)
    public void Home(long raw)
    {
        lock (_gate)
        {
            if (!Release(raw & long.MaxValue)) return; // another world since
            if (Assert(_home.Count < MaxPending)) _home.Add(raw);
            Publish();
        }
    }

    // The oldest handed-back pass whose chunk no other thread is on, in flight until Done
    public bool TryTakeHome(out long raw)
    {
        raw = 0;
        lock (_gate)
        {
            _ = Assert(_home.Count <= MaxPending); // Home refuses more
            for (var i = 0; i < Math.Min(_home.Count, MaxPending); i++)
            {
                if (!_inFlight.Add(_home[i] & long.MaxValue)) continue;
                raw = _home[i];
                _home.RemoveAt(i);
                Publish();
                return Assert(_inFlight.Count <= MaxFlights);
            }
        }

        return false;
    }

    // Engine wants it again (not loaded, or RetryTesselationException): back at the next Undefer, not in this tick
    public void Defer(long raw)
    {
        lock (_gate)
        {
            if (Assert(_deferred.Count < MaxPending)) _deferred.Add(raw);
            Publish();
        }
    }

    public void Undefer()
    {
        lock (_gate)
        {
            (_deferred, _returning) = (_returning, _deferred);
            foreach (var raw in _returning.Bounded(MaxPending)) Add(raw);
            _returning.Clear();
            _ = Assert(_deferred.Count == 0);
            Publish();
        }
    }

    public void Aim(TessView view)
    {
        if (!Finite(view.Fx) || !Finite(view.Fz)) return;
        lock (_gate)
        {
            _view = view;
            Rebuild();
        }
    }

    // Everything waiting goes into the engine's queue as it would hold it (schedule switched off); in-flight stays
    public void Drain(UniqueQueue<long> into)
    {
        if (!NotNull(into)) return;
        lock (_gate)
        {
            using var entries = _pending.GetEnumerator();
            for (var i = 0; i < MaxPending && entries.MoveNext(); i++)
                into.Enqueue(Raw(entries.Current.Key, entries.Current.Value));
            foreach (var raw in _deferred.Bounded(MaxPending)) into.Enqueue(raw);
            foreach (var raw in _home.Bounded(MaxPending)) into.Enqueue(raw);
            Empty(false);
        }
    }

    public int Near(TessView view, int radius)
    {
        if (!Assert(radius >= 0)) return 0;
        var n = 0;
        lock (_gate)
        {
            using var keys = _pending.Keys.GetEnumerator();
            for (var i = 0; i < MaxPending && keys.MoveNext(); i++)
                n += Within(keys.Current, view, radius, _mulX, _mulZ) ? 1 : 0;
        }

        return n;
    }

    public void Clear()
    {
        lock (_gate) Empty(true);
    }

    public static bool Within(long index, TessView view, int radius, long mulX, long mulZ)
    {
        if (!Assert(index >= 0) || !Assert(mulX > 0 && mulZ > 0)) return false;
        int x = (int)(index % mulX), z = (int)(index / mulX % mulZ);
        return Math.Abs(x - view.X) <= radius && Math.Abs(z - view.Z) <= radius;
    }

    // Lower is sooner: squared distance, past NearChunks x1 ahead, x2 sideways, x3 behind (f: look direction on ground)
    public static long Score(int dx, int dy, int dz, double fx, double fz)
    {
        if (!Finite(fx) || !Finite(fz)) return long.MaxValue >> SeqBits;
        var d2 = (long)dx * dx + (long)dy * dy + (long)dz * dz;
        if (!Assert(d2 >= 0) || d2 <= NearChunks * NearChunks) return d2;
        var h = Math.Sqrt((double)dx * dx + (double)dz * dz);
        var cos = h > 0 ? Math.Clamp((dx * fx + dz * fz) / h, -1, 1) : 1;
        return (long)(d2 * (2 - cos));
    }

    // Camera yaw as the engine uses it (TesselatedChunk.RecalcPriority: Atan2(-dz, dx) vs CameraYaw), on the ground
    public static (double Fx, double Fz) Facing(double yaw)
    {
        if (!Finite(yaw)) return (1, 0);
        var (fx, fz) = (Math.Cos(yaw), -Math.Sin(yaw));
        return Assert(Math.Abs(fx * fx + fz * fz - 1) < 1e-6) ? (fx, fz) : (1, 0);
    }

    // Under the lock, as every helper below
    private void Add(long raw)
    {
        var (index, full) = (raw & long.MaxValue, raw >= 0);
        if (!Assert(_pending.Count < MaxPending)) return;
        var known = _pending.TryGetValue(index, out var entry);
        if (known && (!full || entry.Full)) return; // the waiting pass covers it
        entry = known ? entry with { Full = true, Version = ++_version } : new Entry(full, ++_version, ++_seq);
        _pending[index] = entry;
        if (_inFlight.Contains(index)) _ = _blocked.Add(index); // takeable once the pass running on it is done
        else Push(index, entry.Version, entry.Seq);
    }

    // False when the chunk was not in flight: the queue was cleared for another world since it was taken
    private bool Release(long index)
    {
        if (!Assert(index >= 0) || !_inFlight.Remove(index)) return false;
        if (_blocked.Remove(index) && _pending.TryGetValue(index, out var entry)) Push(index, entry.Version, entry.Seq);
        return true;
    }

    private void Empty(bool flights)
    {
        _pending.Clear();
        _deferred.Clear();
        _home.Clear();
        _blocked.Clear();
        _heap.Clear();
        if (flights) _inFlight.Clear();
        (_seq, _version) = (0, 0);
        Publish();
    }

    private void Publish()
    {
        _ = Assert(_pending.Count <= MaxPending); // Add refuses more
        Volatile.Write(ref _count, _pending.Count + _deferred.Count + _home.Count);
    }

    private static long Raw(long index, Entry entry) => entry.Full ? index : index | long.MinValue;

    // The score in the high bits, the arrival order below it: equal scores keep the engine's FIFO
    private long Priority(long index, long seq)
    {
        int x = (int)(index % _mulX), y = (int)(index / (_mulX * _mulZ)), z = (int)(index / _mulX % _mulZ);
        var score = Score(x - _view.X, y - _view.Y, z - _view.Z, _view.Fx, _view.Fz);
        return Assert(score >= 0)
            ? (Math.Min(score, long.MaxValue >> SeqBits) << SeqBits) | (seq & SeqMask)
            : long.MaxValue;
    }

    private void Push(long index, long version, long seq)
    {
        _heap.Enqueue((index, version), Priority(index, seq));
        if (_heap.Count > _pending.Count + MaxStale) Rebuild(); // superseded nodes piled up
        _ = Assert(_heap.Count > 0);
    }

    private void Rebuild()
    {
        _heap.Clear();
        using var entries = _pending.GetEnumerator();
        for (var i = 0; i < MaxPending && entries.MoveNext(); i++)
        {
            var (index, entry) = (entries.Current.Key, entries.Current.Value);
            if (_inFlight.Contains(index)) _ = _blocked.Add(index);
            else _heap.Enqueue((index, entry.Version), Priority(index, entry.Seq));
        }

        _ = Assert(_heap.Count <= _pending.Count);
    }

    private readonly record struct Entry(bool Full, long Version, long Seq);
}
