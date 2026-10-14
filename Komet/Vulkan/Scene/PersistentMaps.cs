using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Vulkan-only: persistent write mappings (glBufferStorage + MAP_PERSISTENT: classic chunk pools, pointer-updated
// meshes) are answered with Vulkan memory (GlTap.Mapping), so the engine writes where Vulkan draws. On Dispose each
// buffer is copied into OpenGL's own mapping and the vertex arrays (Register) pointed back. Pointers kept elsewhere (a
// mod's own mapping) cannot be redirected: that memory leaks until the game ends.
internal sealed unsafe class PersistentMaps(VulkanDevice device) : IDisposable
{
    private const int MaxMaps = 1 << 14, MaxVaos = 1 << 15, MaxRetired = 1 << 14, Slots = TerrainPools.Slots;
    private sealed class Mapped(HostBuffer host, long size)
    {
        public HostBuffer Host { get; } = host;
        public ulong Buffer => Host.Buffer;
        public nint Pointer { get; } = (nint)host.Pointer(0);
        public long Size { get; } = size;
        public nint Gl { get; set; } // OpenGL's own mapping, for the way back
    }

    private readonly Dictionary<uint, Mapped> _maps = [];
    private readonly HashSet<uint> _filled = []; // given contents by glBufferStorage: copied in from OpenGL's mapping
    private Retired<Mapped>? _retiredList;
    private Retired<Mapped> Retiring => _retiredList ??= new Retired<Mapped>(MaxRetired, map => Freed(map, false));
    private readonly List<WeakReference<VAO>> _vaos = [];

    public int Count => _maps.Count;
    public long Bytes { get; private set; }
    public long Leaked { get; private set; } // mappings no vertex array named as Vulkan let go

    // GlTap.Mapping: whole buffer mapped for good; returns OpenGL's pointer where no Vulkan one can be had
    public nint Map(uint buffer, long size, nint gl)
    {
        if (!Assert(size > 0) || !Assert(gl != 0) || !Assert(_maps.Count <= MaxMaps)) return gl;
        if (_maps.TryGetValue(buffer, out var known))
        {
            known.Gl = gl; // mapped again
            return known.Size == size ? known.Pointer : gl;
        }

        if (_maps.Count >= MaxMaps || Allocated(size) is not { } made) return gl;
        made.Gl = gl;
        if (_filled.Remove(buffer)) System.Buffer.MemoryCopy((void*)gl, (void*)made.Pointer, size, size);
        _maps[buffer] = made;
        Bytes += size;
        return made.Pointer;
    }

    // A mod unmapped the buffer: nothing to copy back into
    public void Unmapped(uint buffer)
    {
        if (Assert(buffer > 0) && Assert(_maps.Count <= MaxMaps) && _maps.TryGetValue(buffer, out var map)) map.Gl = 0;
    }

    // glBufferStorage with data: the mapping takes the contents over from OpenGL's
    public void Specified(uint buffer, bool filled)
    {
        if (!Assert(buffer > 0) || !Assert(_filled.Count <= MaxMaps)) return;
        if (filled && _filled.Count < MaxMaps) _ = _filled.Add(buffer);
        else _ = _filled.Remove(buffer);
    }

    public (ulong Buffer, nint Pointer, long Size)? Of(uint buffer) =>
        Assert(_maps.Count <= MaxMaps) && _maps.TryGetValue(buffer, out var m) && Assert(m.Size > 0)
            ? (m.Buffer, m.Pointer, m.Size)
            : null;

    public (ulong Buffer, ulong Offset, ulong Size)? Read(uint buffer) =>
        Assert(_maps.Count <= MaxMaps) && _maps.TryGetValue(buffer, out var m) && Assert(m.Size > 0)
            ? (m.Buffer, 0UL, (ulong)m.Size)
            : null;

    // Host-visible, coherent, device-local where possible
    private Mapped? Allocated(long size) =>
        Assert(size > 0) && Assert(_maps.Count < MaxMaps) && HostBuffer.Create(device, (ulong)size) is { } host ? new Mapped(host, size) : null;

    // Freed once the frame being drawn is done
    public void Gone(ReadOnlySpan<uint> buffers, long frame)
    {
        _ = Assert(frame >= 0) && Assert(buffers.Length <= 1 << 20);
        foreach (var buffer in buffers.Bounded(1 << 20))
        {
            _ = _filled.Remove(buffer);
            if (!_maps.Remove(buffer, out var gone)) continue;
            Bytes -= gone.Size;
            if (!Retiring.Add(gone, frame)) Freed(gone, true);
        }
    }

    public void Collect(long done)
    {
        Retiring.Collect(done);
        _ = Assert(Retiring.Count <= MaxRetired) && Assert(done >= -VulkanFrame.Slots);
    }

    private void Freed(Mapped map, bool wait)
    {
        if (!NotNull(map) || !Assert(device.Handle != IntPtr.Zero)) return;
        if (wait) _ = device.WaitIdle();
        map.Host.Dispose(); // unmapped with it
    }

    // Vertex array with persistent mappings (AllocateEmptyMesh): pointed back at OpenGL's when Vulkan lets go
    public void Register(VAO vao)
    {
        if (!NotNull(vao) || !Assert(_vaos.Count <= MaxVaos)) return;
        if (_vaos.Count >= MaxVaos) _ = _vaos.RemoveAll(static w => !w.TryGetTarget(out _));
        if (_vaos.Count < MaxVaos) _vaos.Add(new WeakReference<VAO>(vao));
    }

    // Contents into OpenGL's mappings, vertex arrays pointed back; then the memory goes, except mappings no array named
    public void Dispose()
    {
        _ = Assert(_maps.Count <= MaxMaps) && device.WaitIdle();
        var back = new Dictionary<nint, nint>(_maps.Count);
        foreach (var map in _maps.Values.Bounded(MaxMaps))
        {
            if (map.Gl != 0) System.Buffer.MemoryCopy((void*)map.Pointer, (void*)map.Gl, map.Size, map.Size);
            if (map.Gl != 0) back[map.Pointer] = map.Gl;
        }

        var named = PointedBack(back);
        foreach (var map in _maps.Values.Bounded(MaxMaps))
            if (named.Contains(map.Pointer)) Freed(map, false);
            else Leaked++;
        _maps.Clear();
        Retiring.Clear();
        _vaos.Clear();
        _filled.Clear();
        Bytes = 0;
    }

    private HashSet<nint> PointedBack(Dictionary<nint, nint> back)
    {
        var named = new HashSet<nint>();
        foreach (var weak in _vaos.Bounded(MaxVaos))
        {
            if (!weak.TryGetTarget(out var vao)) continue;
            for (var slot = 0; slot < Slots; slot++)
            {
                ref var pointer = ref TerrainPools.Pointer(vao, slot);
                if (pointer == 0 || !back.TryGetValue(pointer, out var gl)) continue;
                _ = named.Add(pointer);
                pointer = gl;
            }
        }

        _ = Assert(named.Count <= back.Count) && Assert(_vaos.Count <= MaxVaos);
        return named;
    }
}
