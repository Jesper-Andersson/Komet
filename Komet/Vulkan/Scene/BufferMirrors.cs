using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Writes replay in OpenGL's order; persistent mappings (written unseen) get no mirror. A write goes into the open
// segment's setup while none of its draws read the mirror; after one did, the mirror is renamed (new range = old +
// write, earlier draws keep the old one until their frame is done). An unmirrored buffer is read back from OpenGL once.
internal sealed unsafe class BufferMirrors : IDisposable
{
    public const long EagerLimit = 16L << 20;

    // Up to this size, new buffers are mirrored from their first contents (a draw reading an unmirrored buffer waited
    // for OpenGL's whole queue: most scene stutter, per new entity mesh); larger only once a draw wants them.
    public const long FirstLimit = 1L << 20;
    private const int MaxMirrors = 1 << 17, MaxPending = 1 << 14, MaxWritten = 256, MaxPersistent = 1 << 12,
        MaxReasons = 8, Recent = 128;

    private readonly VulkanDevice _device;
    private readonly VulkanFrame _frame;
    private readonly DeviceHeap _heap;
    private readonly Dictionary<uint, Mirror> _mirrors = [];
    private readonly HashSet<uint> _persistent = [];
    private readonly HashSet<uint> _wanted = []; // buffers some draw read: their writes are mirrored as they come
    private readonly HashSet<uint> _specified = []; // buffers given contents since the mirrors began, for the report
    private readonly List<Copy> _pending = [];
    private readonly List<(ulong Buffer, ulong Offset, ulong Size)> _written = [];
    private readonly FrameStaging _staging;
    private readonly Dictionary<string, long> _reasons = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, long> _sizes = []; // each buffer's size as glBufferData or glBufferStorage gave it

    // Cache in front of _mirrors by the buffer's low bits: a frame reads the same few buffers again and again
    private readonly (uint Buffer, Mirror? Mirror)[] _recent = new (uint, Mirror?)[Recent];

    private sealed class Mirror(DeviceHeap.Range range, long size)
    {
        public DeviceHeap.Range Range { get; set; } = range;
        public long Size { get; } = size;
        public long ReadIn { get; set; } = -1; // the segment (VulkanFrame.Serial) that last read the range
        public bool Stale { get; private set; }
        public string Why { get; private set; } = ""; // what made it stale, for the report

        // In _wanted: only Gone and Forget remove it, dropping the mirror too
        public bool Wanted { get; set; }

        // Dropped or replaced: a stale _recent entry
        public bool Gone { get; set; }

        public void Spoil(string why)
        {
            (Stale, Why) = (true, why);
            _ = Assert(why.Length > 0) && Assert(Size > 0);
        }
    }

    private readonly record struct Copy(ulong From, ulong FromOffset, ulong To, ulong ToOffset, ulong Size);

    public BufferMirrors(VulkanDevice device, VulkanFrame frame)
    {
        _ = NotNull(device) && NotNull(frame);
        (_device, _frame, _heap, _staging) = (device, frame, new DeviceHeap(device), new FrameStaging(device, frame));
    }

    public int Count => _mirrors.Count;

    // Persistent buffers from Vulkan memory (PersistentMaps.Read)
    public System.Func<uint, (ulong Buffer, ulong Offset, ulong Size)?>? Mapped { get; init; }

    // Pool buffers (TerrainPools.BufferOf) are used in place (TerrainRenderer.PoolWritten): no mirror,
    // which lean (GlTap.LeanBuffer) would keep in full
    public System.Func<uint, (ulong Buffer, ulong Offset, ulong Size)?>? Pooled { get; init; }

    // The pools took the buffer over (TerrainPools.Held)
    public void Taken(uint buffer)
    {
        if (!Assert(buffer > 0) || !Assert(Pooled is not null)) return;
        Drop(buffer);
        _ = _wanted.Remove(buffer);
    }

    // Before the first frame, writes and copies go with the texture uploads, in order
    public TextureImages? Early { get; init; }

    // Approximate managed bytes for the report (contents live in device memory)
    public long ManagedBytes => Assert(_mirrors.Count <= MaxMirrors) && Assert(_sizes.Count <= MaxMirrors)
        ? (96L * _mirrors.Count) + (24L * _sizes.Count) + (16L * (_wanted.Count + _specified.Count + _persistent.Count))
        : 0;
    public ulong Bytes => _heap.Bytes;
    public long Imports { get; private set; }
    public long ImportedBytes { get; private set; }
    public long Asked { get; private set; } // sizes asked of OpenGL (a buffer made before the mirrors): a wait for glthread

    public string Reasons =>
        string.Join(", ", _reasons.OrderByDescending(r => r.Value).Take(MaxReasons).Select(r => $"{r.Key} {r.Value}"));
    public long Renames { get; private set; }

    public long PersistentReads { get; private set; }
    public long Writes { get; private set; }

    public bool Persistent(uint buffer) => Assert(_persistent.Count <= MaxPersistent) && _persistent.Contains(buffer);

    public (ulong Buffer, ulong Offset, ulong Size)? Read(uint buffer)
    {
        if (buffer == 0 || !Assert(_frame.Open)) return null;
        if (Pooled?.Invoke(buffer) is { } pooled) return pooled;
        // per draw: a mirrored buffer is never persistent (Data drops it, Imported is not asked)
        var mirror = Find(buffer);
        if ((mirror is null || mirror.Stale) && _persistent.Contains(buffer))
        {
            PersistentReads++; // read where the engine writes, ordered by no fence of OpenGL's
            return Mapped?.Invoke(buffer);
        }

        if (mirror is null || mirror.Stale) mirror = ReadBack(buffer, mirror);
        if (mirror is null) return null;
        if (!mirror.Wanted && _wanted.Count < MaxMirrors)
        {
            _ = _wanted.Add(buffer);
            mirror.Wanted = true;
        }

        mirror.ReadIn = _frame.Serial;
        _ = Assert(mirror.Range.Size >= (ulong)mirror.Size);
        return (mirror.Range.Buffer, mirror.Range.Offset, (ulong)mirror.Size);
    }

    // A Vulkan range copy (draw indices into uploads), ordered with mirror writes before the segment's
    // draws
    public bool CopyAhead((ulong Buffer, ulong Offset) from, (ulong Buffer, ulong Offset) to, ulong size) =>
        Assert(size > 0) && Assert(from.Buffer != 0 && to.Buffer != 0) &&
        Enqueue(new Copy(from.Buffer, from.Offset, to.Buffer, to.Offset, size));

    // Mirror as it stands (stale or not), for a copy back to OpenGL outside any frame
    public (ulong Buffer, ulong Offset, long Size)? Held(uint buffer) =>
        Assert(buffer > 0) && _mirrors.TryGetValue(buffer, out var mirror) && Assert(!mirror.Gone)
            ? (mirror.Range.Buffer, mirror.Range.Offset, mirror.Size)
            : null;

    private Mirror? Find(uint buffer)
    {
        ref var slot = ref _recent[buffer % Recent];
        if (slot.Buffer == buffer && slot.Mirror is { Gone: false } known) return known;
        var found = _mirrors.GetValueOrDefault(buffer);
        if (found is not null) slot = (buffer, found);
        _ = Assert(found is null || !found.Gone) && Assert(_recent.Length == Recent);
        return found;
    }

    public void Data(uint buffer, long size, IntPtr data, bool persistent)
    {
        if (buffer == 0 || !Assert(size >= 0)) return;
        Drop(buffer);
        if (_specified.Count < MaxMirrors) _ = _specified.Add(buffer);
        if (_sizes.Count < MaxMirrors || _sizes.ContainsKey(buffer)) _sizes[buffer] = size;
        if (persistent && _persistent.Count < MaxPersistent) _ = _persistent.Add(buffer);
        else _ = _persistent.Remove(buffer);
        // lean (GlTap.LeanBuffer): OpenGL keeps nothing, so the mirror is what OpenGL gets back, whatever the size
        var lean = GlTap.LeanBuffer(buffer) && size <= int.MaxValue;
        if (persistent || size == 0 || (size > EagerLimit && !lean) || _mirrors.Count >= MaxMirrors ||
            (!_wanted.Contains(buffer) && size > FirstLimit && !lean)) return;
        if (_heap.Take((ulong)size) is not { } range) return;
        var mirror = new Mirror(range, size);
        _mirrors[buffer] = mirror;
        _ = Assert(range.Size >= (ulong)size);
        if (data != IntPtr.Zero && !Stage(new ReadOnlySpan<byte>((void*)data, (int)size), range, 0))
            mirror.Spoil("no staging room"); // read back when next read
        Writes++;
    }

    public void SubData(uint buffer, long offset, long size, IntPtr data)
    {
        if (Find(buffer) is not { } mirror || mirror.Stale || !Assert(offset >= 0 && size >= 0)) return;
        if (data == IntPtr.Zero || offset + size > mirror.Size || size > int.MaxValue)
        {
            mirror.Spoil(data == IntPtr.Zero ? "sub-data from a bound buffer" : "sub-data past the end");
            return;
        }

        if (size == 0 || !Assert(mirror.Size > 0)) return;
        Renamed(mirror);
        if (!Stage(new ReadOnlySpan<byte>((void*)data, (int)size), mirror.Range, (ulong)offset))
            mirror.Spoil("no staging room");
        Writes++;
    }

    // glCopyBufferSubData
    public void Copied(uint from, uint to, long fromOffset, long toOffset, long size)
    {
        if (!_mirrors.TryGetValue(to, out var target) || target.Stale || !Assert(size >= 0)) return;
        if (!_mirrors.TryGetValue(from, out var source) || source.Stale || fromOffset + size > source.Size ||
            toOffset + size > target.Size || fromOffset < 0 || toOffset < 0)
        {
            target.Spoil("a copy from an unmirrored buffer");
            return;
        }

        if (size == 0 || !Assert(source.Size > 0)) return;
        Renamed(target);
        if (!Enqueue(new Copy(source.Range.Buffer, source.Range.Offset + (ulong)fromOffset, target.Range.Buffer,
                target.Range.Offset + (ulong)toOffset, (ulong)size))) target.Spoil("copies queued full");
        Writes++;
    }

    public void Unseen(uint buffer, string why = "a write unseen")
    {
        if (why == GlTap.OwnData) _ = _sizes.Remove(buffer);
        if (_mirrors.TryGetValue(buffer, out var mirror) && Assert(mirror.Size > 0)) mirror.Spoil(why);
        _ = Assert(_mirrors.Count <= MaxMirrors);
    }

    public void Forget()
    {
        foreach (var buffer in _mirrors.Keys.ToArray().Bounded(MaxMirrors)) Drop(buffer);
        _persistent.Clear();
        _wanted.Clear();
        _specified.Clear();
        _sizes.Clear();
        _ = Assert(_mirrors.Count == 0);
    }

    public void Gone(ReadOnlySpan<uint> buffers)
    {
        foreach (var buffer in buffers.Bounded(4096))
        {
            Drop(buffer);
            _ = _persistent.Remove(buffer);
            _ = _wanted.Remove(buffer);
            _ = _specified.Remove(buffer);
            _ = _sizes.Remove(buffer);
        }

        _ = Assert(_mirrors.Count <= MaxMirrors);
    }

    private void Drop(uint buffer)
    {
        if (!_mirrors.Remove(buffer, out var mirror) || !Assert(mirror.Size > 0)) return;
        mirror.Gone = true;
        _heap.Retire(mirror.Range, _frame.Number);
        _ = Assert(_mirrors.Count <= MaxMirrors);
    }

    private void Renamed(Mirror mirror)
    {
        if (!NotNull(mirror) || !_frame.Open || mirror.ReadIn != _frame.Serial) return;
        if (_heap.Take((ulong)mirror.Size) is not { } range)
        {
            mirror.Spoil("no heap room to rename");
            return;
        }

        var old = mirror.Range;
        if (!Enqueue(new Copy(old.Buffer, old.Offset, range.Buffer, range.Offset, (ulong)mirror.Size)))
            mirror.Spoil("copies queued full");
        _heap.Retire(old, _frame.Number);
        (mirror.Range, mirror.ReadIn) = (range, -1);
        Renames++;
        _ = Assert(range.Size >= (ulong)mirror.Size);
    }

    // OpenGL's contents via host memory (waits for OpenGL's queue, once per pre-mirror or unseen buffer)
    private Mirror? ReadBack(uint buffer, Mirror? old)
    {
        if (!Assert(buffer > 0) || !Assert(_mirrors.Count <= MaxMirrors) || _mirrors.Count >= MaxMirrors) return null;
        if (GlTap.LeanBuffer(buffer)) return old; // lean: OpenGL holds none of it, what Vulkan last heard stays
        var size = SizeOf(buffer);
        if (size <= 0 || size > int.MaxValue || _heap.Take((ulong)size) is not { } range) return null;
        if (_staging.Take((int)size, out var at) is not { } staging)
        {
            _heap.Retire(range, _frame.Number);
            return null;
        }

        var start = Hitches.Now;
        using (GlTap.Quietly()) GL.GetNamedBufferSubData((int)buffer, IntPtr.Zero, (int)size, (IntPtr)staging.Pointer(at));
        Hitches.Since(Hitches.Kind.BufferRead, start);
        return Placed(buffer, old, (range, size), (staging.Buffer, (ulong)at));
    }

    // New mirror copied from staging in order with the frame's writes; the old range retires
    private Mirror Placed(uint buffer, Mirror? old, (DeviceHeap.Range Range, long Size) to, (ulong Buffer, ulong At) from)
    {
        _ = Assert(buffer > 0) && Assert(to.Size > 0);
        if (old is not null)
        {
            _heap.Retire(old.Range, _frame.Number);
            old.Gone = true; // replaced
        }

        var (range, size) = to;
        var mirror = new Mirror(range, size);
        if (!Enqueue(new Copy(from.Buffer, from.At, range.Buffer, range.Offset, (ulong)size))) mirror.Spoil("copies queued full");
        _mirrors[buffer] = mirror;
        (Imports, ImportedBytes) = (Imports + 1, ImportedBytes + size);
        var why = old?.Why ?? FirstWhy(buffer, size);
        if (_reasons.Count < MaxReasons || _reasons.ContainsKey(why)) _reasons[why] = _reasons.GetValueOrDefault(why) + 1;
        return mirror;
    }

    // As OpenGL was told; for pre-mirror buffers asked of OpenGL (waits for Mesa's glthread, not the GPU)
    private long SizeOf(uint buffer)
    {
        if (_sizes.TryGetValue(buffer, out var size) || !Assert(buffer > 0)) return size;
        if (GlTap.BufferSizeOf(buffer) is { } recorded) return recorded;
        GL.GetNamedBufferParameter((int)buffer, BufferParameterName.BufferSize, out int asked);
        Asked++;
        if (_sizes.Count < MaxMirrors) _sizes[buffer] = asked;
        _ = Assert(asked >= 0);
        return asked;
    }

    private string FirstWhy(uint buffer, long size)
    {
        if (!Assert(buffer > 0) || _wanted.Contains(buffer)) return "respecified without a mirror";
        if (size > FirstLimit) return "first read of a large buffer";
        _ = Assert(_specified.Count <= MaxMirrors);
        return _specified.Contains(buffer) ? "first read" : "first read of a buffer made before the mirrors";
    }

    private bool Stage(ReadOnlySpan<byte> data, DeviceHeap.Range range, ulong offset)
    {
        if (!Assert(offset + (ulong)data.Length <= range.Size) || data.IsEmpty) return true;
        if (Early is { Before: true } early) return early.Write((range.Buffer, range.Offset + offset), data);
        var staging = _staging.Take(data.Length, out var at);
        if (staging is null) return false;
        _ = Assert(at >= 0);
        data.CopyTo(new Span<byte>(staging.Pointer(at), data.Length));
        return Enqueue(new Copy(staging.Buffer, (ulong)at, range.Buffer, range.Offset + offset, (ulong)data.Length));
    }

    private bool Enqueue(Copy copy)
    {
        if (!Assert(copy.Size > 0) || !Assert(_pending.Count <= MaxPending)) return false;
        if (Early is { Before: true } early)
            return early.Copy((copy.From, copy.FromOffset), (copy.To, copy.ToOffset), copy.Size);
        if (_frame.Open)
        {
            Record(_frame.Setup, copy);
            return true;
        }

        if (_pending.Count >= MaxPending) return false;
        _pending.Add(copy);
        return true;
    }

    // Waits for earlier copies in the same transfers whose target it overlaps
    private void Record(IntPtr commands, Copy copy)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(_written.Count <= MaxWritten)) return;
        if (Overlaps(copy.From, copy.FromOffset, copy.Size) || Overlaps(copy.To, copy.ToOffset, copy.Size) ||
            _written.Count == MaxWritten)
        {
            var memory = new Vk.GlobalBarrier
            {
                SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite,
                DstAccess = Vk.AccessTransferRead | Vk.AccessTransferWrite
            };
            VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageTransfer, 0, 1, &memory, 0, null, 0, null);
            _written.Clear();
        }

        var region = new Vk.BufferCopy { SourceOffset = copy.FromOffset, TargetOffset = copy.ToOffset, Size = copy.Size };
        VkApi.CmdCopyBuffer(commands, copy.From, copy.To, 1, &region);
        _written.Add((copy.To, copy.ToOffset, copy.Size));
    }

    private bool Overlaps(ulong buffer, ulong offset, ulong size)
    {
        _ = Assert(size > 0) && Assert(_written.Count <= MaxWritten);
        foreach (var (b, o, s) in _written.Bounded(MaxWritten))
            if (b == buffer && offset < o + s && o < offset + size)
                return true;
        return false;
    }

    public void Opened()
    {
        _written.Clear(); // the setup began with a barrier after everything before
        if (!Assert(_frame.Open) || _pending.Count == 0) return;
        foreach (var copy in _pending.Bounded(MaxPending)) Record(_frame.Setup, copy);
        _pending.Clear();
    }

    public void Ending()
    {
        if (_pending.Count > 0 && Assert(!_frame.Open) && _frame.Alone(commands =>
            {
                _written.Clear();
                foreach (var copy in _pending.Bounded(MaxPending)) Record(commands, copy);
            }))
            _pending.Clear();

        _heap.Collect(_frame.Done);
        _staging.Collect();
        _ = Assert(_pending.Count <= MaxPending);
    }

    public void Dispose()
    {
        _ = Assert(_mirrors.Count <= MaxMirrors) && _device.WaitIdle();
        _staging.Dispose();
        _mirrors.Clear();
        Array.Clear(_recent);
        _persistent.Clear();
        _wanted.Clear();
        _sizes.Clear();
        _pending.Clear();
        _heap.Dispose();
    }
}
