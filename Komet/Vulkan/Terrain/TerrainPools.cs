using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Adopting gives each pool buffer a Vulkan buffer of its own (Stood) named by its GL buffer; OpenGL keeps its own,
// nothing is rebound, copied or awaited. Persistently mapped pools (since 1.22) are written through Vulkan's mapping
// (PersistentMaps), or OpenGL's mapping's engine pointer moves to Vulkan's until the pool goes.
internal sealed unsafe class TerrainPools(VulkanDevice device) : IDisposable
{
    // SSBO face data packed (CompactFaces); fixed while pools exist, as ports and pools must agree
    public bool Compact { get; private set; } = CompactFaces.Enabled;

    // TerrainRebuild: packing may change only with no pool held
    public bool Repack(bool compact)
    {
        if (_pools.Count > 0 || !Assert(_moving.Count == 0)) return false;
        Compact = compact;
        return Assert(Compact == compact);
    }

    private const int MaxAttributes = 16, MaxPools = 4096, MaxRetired = 1024;

    private readonly Dictionary<int, Pool> _pools = [];
    private readonly List<Moving> _moving = []; // Adopt's: it runs every frame
    private readonly Dictionary<int, SharedBuffer> _byName = []; // each pool buffer by its GL buffer
    private readonly List<(VAO Vao, int Slot, nint Gl)> _repointed = []; // OpenGL's mappings moved to Vulkan's
    private readonly Retired<SharedBuffer> _retired = new(MaxRetired, static buffer => buffer.Dispose()); // released pools'

    // GL buffer the engine writes through a Vulkan mapping (PersistentMaps.Of)
    public System.Func<uint, (ulong Buffer, nint Pointer, long Size)?>? Mapped { get; set; }

    // Read back from OpenGL once (made before Vulkan, or not adopted at creation)
    public long Readbacks { get; private set; }

    public SharedBuffer? BufferOf(int gl) => Assert(gl >= 0) && _byName.TryGetValue(gl, out var b) ? b : null;

    // For BufferMirrors.Pooled
    public (ulong Buffer, ulong Offset, ulong Size)? Range(uint gl) =>
        gl < int.MaxValue && BufferOf((int)gl) is { } b && Assert(b.Buffer != 0) && Assert(b.Size > 0)
            ? (b.Buffer, 0, b.Size)
            : null;

    // Told of each GL buffer a pool now holds (the scene drops its mirror)
    public Action<uint>? Held { get; set; }

    public readonly record struct Attribute(int Location, int Buffer, int Size, int Type, bool Normalized, bool Integer)
    {
        public int Stride { get; init; }
        public long Offset { get; init; }
        public int Divisor { get; init; }
    }

    public sealed class Pool(VAO vao, SharedBuffer? faces, SharedBuffer[] vertices, Attribute[] attributes)
    {
        public VAO Vao { get; } = vao;
        public SharedBuffer? Faces { get; } = faces;
        public SharedBuffer[] Vertices { get; } = vertices;
        public Attribute[] Attributes { get; } = attributes;
        // Classic path only; SSBO draws the quad pattern
        public SharedBuffer? Indices { get; init; }

        // Cached per location list: opaque, shadow and depth pipelines take turns on the pool
        public (uint Location, ulong Buffer, ulong Offset)[] Bindings(int[] locations)
        {
            if (!NotNull(locations) || !Assert(locations.Length <= MaxAttributes)) return [];
            for (var i = 0; i < Math.Min(_bound, MaxLists); i++)
                if (ReferenceEquals(_lists[i].For, locations))
                    return _lists[i].Bindings;
            var bindings = new (uint, ulong, ulong)[locations.Length];
            for (var i = 0; i < Math.Min(locations.Length, MaxAttributes); i++)
            {
                var location = locations[i];
                var a = Attributes.First(x => x.Location == location); // only when the list is new
                bindings[i] = ((uint)locations[i], Vertices[a.Buffer].Buffer, (ulong)a.Offset);
            }

            _lists[_bound++ % MaxLists] = (locations, bindings); // a pipeline made again replaces the oldest
            return Assert(bindings.Length == locations.Length) ? bindings : [];
        }

        private const int MaxLists = 8;
        private readonly (int[] For, (uint, ulong, ulong)[] Bindings)[] _lists = new (int[], (uint, ulong, ulong)[])[MaxLists];
        private int _bound;

        public string Layout { get; } = string.Join(';', attributes.Select(a =>
            $"{a.Location}:{a.Size}:{a.Type}:{a.Normalized}:{a.Integer}:{a.Stride}:{a.Divisor}"));
    }

    public int Count => _pools.Count;

    public ulong Bytes => Assert(_pools.Count <= MaxPools)
        ? (ulong)_pools.Values.SelectMany(Buffers).Distinct().Sum(b => (decimal)b.Size)
        : 0;

    // For the report: SSBO faces, their vertex buffers (colours, custom data), classic pools (liquids)
    public string Breakdown()
    {
        if (!Assert(_pools.Count <= MaxPools)) return "";
        var (faces, beside, classic, classicPools) = (0UL, 0UL, 0UL, 0);
        foreach (var pool in _pools.Values.ToArray().Bounded(MaxPools))
        {
            var bytes = Buffers(pool).Aggregate(0UL, static (sum, b) => sum + b.Size);
            if (pool.Indices is not null)
            {
                (classic, classicPools) = (classic + bytes, classicPools + 1);
                continue;
            }

            var face = pool.Faces?.Size ?? 0;
            (faces, beside) = (faces + face, beside + bytes - face);
        }

        var mode = Compact ? "packed faces" : "faces";
        return Assert(classicPools >= 0)
            ? $"{mode} {faces >> 20} MB, colours and custom data {beside >> 20} MB, classic (liquids) {classic >> 20} MB in {classicPools} pools"
            : "";
    }

    private static IEnumerable<SharedBuffer> Buffers(Pool pool)
    {
        if (!NotNull(pool) || !Assert(pool.Vertices.Length <= MaxAttributes)) return [];
        IEnumerable<SharedBuffer?> all = [.. pool.Vertices, pool.Faces, pool.Indices];
        return all.OfType<SharedBuffer>().Distinct();
    }

    public Pool? Find(VAO vao) =>
        NotNull(vao) && Assert(_pools.Count <= MaxPools) && _pools.TryGetValue(vao.VaoId, out var pool) ? pool : null;

    private sealed record Moving(VAO Vao, Attribute[] Attributes, Dictionary<int, SharedBuffer> Moved, int Indices);

    // classic: its index buffer moves too. fresh: just made, unwritten (AllocateEmptyMesh): no copy, no wait
    public Pool? Adopt(VAO vao, bool classic, out string why, bool fresh = false)
    {
        why = "";
        if (!NotNull(vao) || !Assert(vao.VaoId > 0)) return null;
        if (_pools.TryGetValue(vao.VaoId, out var known)) return known;
        var moving = Copied(vao, classic, fresh, out why);
        if (moving is null || !Assert(moving.Moved.Count > 0)) return null;
        return Rebound(moving);
    }

    // All copies first, one wait, then all pools rebound
    public int Adopt(List<(VAO Vao, bool Classic)> pools, out string why)
    {
        why = "";
        if (!NotNull(pools) || !Assert(pools.Count <= MaxPools)) return 0;
        _moving.Clear();
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var (vao, classic) = pools[i];
            if (vao.VaoId <= 0 || _pools.ContainsKey(vao.VaoId) || Listed(vao.VaoId)) continue;
            if (Copied(vao, classic, false, out var refused) is { } made) _moving.Add(made);
            else if (why.Length == 0) why = refused;
        }

        foreach (var made in _moving.Bounded(MaxPools)) _ = Rebound(made);
        var count = _moving.Count;
        _moving.Clear();
        return count;
    }

    // Runs per pool per frame: a capturing lambda here allocated a closure each
    private bool Listed(int vao)
    {
        foreach (var m in _moving.Bounded(MaxPools))
            if (m.Vao.VaoId == vao)
                return true;
        return !Assert(vao > 0);
    }

    private Moving? Copied(VAO vao, bool classic, bool fresh, out string why)
    {
        why = "";
        if (!NotNull(vao) || !Assert(_pools.Count < MaxPools)) return null;
        VulkanWatch.Mark($"adopting pool {vao.VaoId}");
        var moved = new Dictionary<int, SharedBuffer>();
        var attributes = Formats(vao);
        var indices = classic ? vao.vboIdIndex : 0;
        var names = attributes.Select(a => a.Buffer).Append(vao.xyzVboId).Append(indices).Where(b => b > 0).Distinct()
            .ToArray();
        foreach (var name in names.Bounded(MaxAttributes + 2))
        {
            var shared = Stood(vao, name, fresh, !classic && name == vao.xyzVboId, out why);
            if (shared is null)
            {
                foreach (var made in moved.Values.Bounded(MaxAttributes + 2)) made.Dispose();
                return null;
            }

            moved[name] = shared;
        }

        return new Moving(vao, attributes, moved, indices);
    }

    private Pool Rebound(Moving moving)
    {
        _ = Assert(!_pools.ContainsKey(moving.Vao.VaoId));
        var pool = Rebind(moving.Vao, moving.Attributes, moving.Moved, moving.Indices);
        _pools[moving.Vao.VaoId] = pool;
        return pool;
    }

    // GlTap's vertex array tracking (asked of OpenGL once for pre-tap arrays); Buffer is the GL name until Rebind
    private static Attribute[] Formats(VAO vao)
    {
        var found = new List<Attribute>();
        if (!NotNull(vao) || GlTap.ArrayAt((uint)vao.VaoId) is not { } array) return [];
        for (var i = 0; i < Math.Min(vao.vaoSlotNumber, MaxAttributes); i++)
        {
            var a = array.Attributes[i];
            if (!a.Enabled) continue;
            found.Add(new Attribute(i, (int)a.Buffer, a.Size, (int)a.Type, a.Normalized, a.Integer)
            {
                Stride = a.Stride, Offset = a.Offset, Divisor = (int)a.Divisor
            });
        }

        _ = Assert(found.Count <= MaxAttributes);
        return [.. found];
    }

    private Pool Rebind(VAO vao, Attribute[] attributes, Dictionary<int, SharedBuffer> moved, int indices)
    {
        var vertices = moved.Where(m => (m.Key != vao.xyzVboId && m.Key != indices) ||
                                        attributes.Any(a => a.Buffer == m.Key))
            .Select(m => m.Value).ToArray();
        var rebound = new Attribute[attributes.Length];
        for (var i = 0; i < Math.Min(attributes.Length, MaxAttributes); i++)
        {
            var a = attributes[i];
            rebound[i] = a with { Buffer = Array.IndexOf(vertices, moved[a.Buffer]) };
        }

        var faces = moved.GetValueOrDefault(vao.xyzVboId);
        var elements = indices > 0 ? moved.GetValueOrDefault(indices) : null;
        foreach (var (name, buffer) in moved.ToArray().Bounded(MaxAttributes + 2))
        {
            _byName[name] = buffer;
            Held?.Invoke((uint)name);
        }

        _ = Assert(rebound.All(a => a.Buffer >= 0));
        return new Pool(vao, faces, vertices, rebound) { Indices = elements };
    }

    // The Vulkan mapping answered for it, else a buffer made here, filled unless fresh. faces: SSBO face data, packed
    // when Compact (never mapped: the engine would write 64-byte quads through it)
    private SharedBuffer? Stood(VAO vao, int name, bool fresh, bool faces, out string why)
    {
        why = "";
        if (!Assert(name > 0) || !Assert(_pools.Count < MaxPools)) return null;
        if (Mapped?.Invoke((uint)name) is { } mapped)
        {
            if (!(Compact && faces)) return SharedBuffer.Wrapping(device, mapped, name);
            why = $"face data {name} is mapped and cannot be packed"; // the packed shaders could not read it
            return null;
        }

        var size = (int)GlTap.BufferSize((uint)name);
        var mapping = MappingOf(vao, name);
        var packed = Compact && faces && mapping.Gl == 0 && size > 0 && size % CompactFaces.Engine == 0;
        if (Compact && faces && !packed && size > 0)
        {
            why = $"face data {name} cannot be packed";
            return null;
        }

        var room = packed ? CompactFaces.PackedOf(size) : size;
        var made = size > 0 ? SharedBuffer.Private(device, (ulong)room, name, mapping.Gl != 0, out why, packed) : null;
        if (made is null) why = why.Length > 0 ? why : $"buffer {name} has no storage";
        else if (!fresh && !Seeded(vao, made, mapping, size)) why = $"buffer {name} could not be read back";
        if (why.Length > 0) made?.Dispose();
        return why.Length > 0 ? null : made;
    }

    // OpenGL's own persistent mapping, made before Vulkan answered the engine's
    private static (int Slot, nint Gl) MappingOf(VAO vao, int name)
    {
        if (!NotNull(vao) || !Assert(name > 0) || !vao.Persistent) return (-1, 0);
        for (var slot = 0; slot < Slots; slot++)
            if (Name(vao, slot) == name && Pointer(vao, slot) != 0)
                return (slot, Pointer(vao, slot));
        return (-1, 0);
    }

    // From OpenGL's mapping, whose engine pointer then moves to Vulkan's (back on Dispose), else read back via host
    // memory and waited for (adoption is rare). size: the engine's; packed in host memory before the copy
    private bool Seeded(VAO vao, SharedBuffer made, (int Slot, nint Gl) mapping, long size)
    {
        _ = Assert(size >= (long)made.Size) && Assert(made.Packed || size == (long)made.Size);
        if (mapping.Gl != 0 && NotNull(vao) && Assert(made.Pointer != 0))
        {
            System.Buffer.MemoryCopy((void*)mapping.Gl, (void*)made.Pointer, size, size);
            Pointer(vao, mapping.Slot) = made.Pointer;
            _repointed.Add((vao, mapping.Slot, mapping.Gl));
            return true;
        }

        if (GlTap.LeanBuffer((uint)made.Gl)) return false; // lean: OpenGL holds none of it to read back
        var copied = (long)made.Size;
        using var host = HostBuffer.Create(device, (ulong)(size + (made.Packed ? copied : 0)), bulk: true);
        if (host is null || host.Take((int)size, out _, 16) is not 0) return false;
        using (GlTap.Quietly()) GL.GetNamedBufferSubData(made.Gl, IntPtr.Zero, (int)size, (IntPtr)host.Pointer(0));
        Readbacks++;
        var source = 0UL;
        if (made.Packed)
        {
            var at = host.Take((int)copied, out _, 16);
            if (at < 0) return false;
            CompactFaces.Pack(new ReadOnlySpan<byte>(host.Pointer(0), (int)size), new Span<byte>(host.Pointer(at), (int)copied));
            source = (ulong)at;
        }

        var (from, to) = (host.Buffer, made.Buffer);
        return Assert(size > 0) && device.Run(commands =>
        {
            var region = new Vk.BufferCopy { SourceOffset = source, Size = (ulong)copied };
            VkApi.CmdCopyBuffer(commands, from, to, 1, &region);
        }, []);
    }


    internal const int Slots = 10; // a VAO's buffers (PersistentMaps' too)

    internal static ref int Name(VAO vao, int slot)
    {
        _ = Assert(slot is >= 0 and < Slots);
        switch (slot)
        {
            case 0: return ref vao.xyzVboId;
            case 1: return ref vao.normalsVboId;
            case 2: return ref vao.uvVboId;
            case 3: return ref vao.rgbaVboId;
            case 4: return ref vao.flagsVboId;
            case 5: return ref vao.customDataFloatVboId;
            case 6: return ref vao.customDataIntVboId;
            case 7: return ref vao.customDataShortVboId;
            case 8: return ref vao.customDataByteVboId;
            default: return ref vao.vboIdIndex;
        }
    }

    internal static ref nint Pointer(VAO vao, int slot)
    {
        _ = Assert(slot is >= 0 and < Slots);
        switch (slot)
        {
            case 0: return ref vao.xyzPtr;
            case 1: return ref vao.normalsPtr;
            case 2: return ref vao.uvPtr;
            case 3: return ref vao.rgbaPtr;
            case 4: return ref vao.flagsPtr;
            case 5: return ref vao.customDataFloatPtr;
            case 6: return ref vao.customDataIntPtr;
            case 7: return ref vao.customDataShortPtr;
            case 8: return ref vao.customDataBytePtr;
            default: return ref vao.indicesPtr;
        }
    }

    // Buffers go once frame is done (Collect; -1: at once); PoolTrim deletes empty pools every few seconds
    public void Release(VAO vao, long frame)
    {
        if (!NotNull(vao) || !Assert(_pools.Count <= MaxPools) || !_pools.Remove(vao.VaoId, out var pool)) return;
        Unstood(pool, false, frame);
    }

    private void Retire(SharedBuffer buffer, long frame)
    {
        if (!NotNull(buffer) || !Assert(_retired.Count <= MaxRetired) || (frame >= 0 && _retired.Add(buffer, frame))) return;
        if (frame >= 0) _ = device.WaitIdle(); // too many waiting: this one goes now
        buffer.Dispose();
    }

    public void Collect(long done)
    {
        _retired.Collect(done);
        _ = Assert(_retired.Count <= MaxRetired) && Assert(done >= -VulkanFrame.Slots);
    }

    // Buffers go (PersistentMaps' stay theirs); back: OpenGL's mappings get the contents and pointers again
    private void Unstood(Pool pool, bool back, long frame = -1)
    {
        if (!NotNull(pool) || !Assert(_repointed.Count <= MaxPools * Slots)) return;
        var count = _repointed.Count;
        for (var k = 0; k < Math.Min(count, MaxPools * Slots); k++)
        {
            var i = count - 1 - k; // from the last: removal keeps the rest where they are
            var (vao, slot, gl) = _repointed[i];
            if (!ReferenceEquals(vao, pool.Vao)) continue;
            var size = GlTap.BufferSizeOf((uint)Name(vao, slot)) ?? 0;
            if (back && size > 0) System.Buffer.MemoryCopy((void*)Pointer(vao, slot), (void*)gl, size, size);
            Pointer(vao, slot) = gl;
            _repointed.RemoveAt(i);
        }

        foreach (var buffer in Buffers(pool).ToArray().Bounded(MaxAttributes + 2))
        {
            _ = _byName.Remove(buffer.Gl);
            Retire(buffer, frame);
        }
    }

    // Pointers handed back once the GPU is done with the buffers
    public void Dispose()
    {
        _ = Assert(_pools.Count <= MaxPools) && device.WaitIdle();
        _retired.Clear();
        foreach (var pool in _pools.Values.ToArray().Bounded(MaxPools)) Unstood(pool, true);
        _repointed.Clear();
        _pools.Clear();
        _byName.Clear();
    }
}
