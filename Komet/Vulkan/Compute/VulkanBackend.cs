using Komet.Gpu;

namespace Komet.Vulkan;

// OpenGL semantics the passes rely on: Upload gives fresh contents (an upload-arena region) so earlier commands see the
// old ones; Zero is ordered before later dispatches; an unbound declared resource gets a placeholder.
internal sealed unsafe partial class VulkanBackend : IGpuBackend, IDisposable
{
    private const int MaxHandles = 4096, Align = 256;
    private const ulong ArenaBytes = 64UL << 20, BufferUsage = Vk.TransferSrc | Vk.TransferDst | Vk.UsageUniform |
                                                               Vk.UsageStorage | Vk.UsageIndirect;

    private readonly VulkanDevice _device;
    private readonly FrameStaging? _overflow;
    private long _collected = -1;
    private readonly IntPtr _handle;
    private readonly BufferSlot?[] _buffers = new BufferSlot?[MaxHandles];
    private readonly ImageSlot?[] _images = new ImageSlot?[MaxHandles];
    private BufferSlot? _arena;
    private ulong _arenaUsed;
    private IntPtr _commands;
    private int _placeholderImage, _placeholderBuffer;

    // frame: recording into its segments; shared: the shared image of an engine texture, for Engine
    internal VulkanBackend(VulkanDevice device, VulkanFrame? frame, System.Func<int, SharedImage?>? shared)
    {
        (_device, _frame, _shared) = (device, frame, shared);
        _overflow = frame is null ? null : new FrameStaging(device, frame);
        _handle = NotNull(device) ? device.Handle : IntPtr.Zero;
        if (frame is null) _arena = Allocate(ArenaBytes); // on a frame uploads go into the frame's own
        _ = Assert(_arena is not null || frame is not null);
        _placeholderBuffer = Buffer();
        Upload<uint>(_placeholderBuffer, [], Align);
        if (frame is null) _placeholderImage = Pyramid(1, 1, out _); // on a frame: at the first dispatch, in a segment
        var info = new Vk.SamplerInfo
        {
            SType = Vk.SamplerCreateInfo, AddressU = 2, AddressV = 2, AddressW = 2, MaxLod = 16 // nearest, clamped
        };
        ulong sampler;
        _sampler = Assert(_handle != IntPtr.Zero) && Assert(_sampler == 0) &&
                   VkApi.CreateSampler(_handle, &info, null, &sampler) == Vk.Success
            ? sampler
            : 0;
    }

    public bool Supported() => (_arena is not null || _frame is not null) && Assert(_handle != IntPtr.Zero) &&
                               (_placeholderImage != 0 || _frame is not null) && _sampler != 0 && !_disposed;

    public bool Doubles() => _device.Float64 && Assert(_handle != IntPtr.Zero);

    public int Buffer()
    {
        var at = Free(_buffers);
        if (Index(at, MaxHandles) && at > 0) _buffers[at] = new BufferSlot(0, 0, 0, null) { View = (0, 0, 0) };
        return at;
    }

    // Empty data: own storage of >= minBytes (UploadAt, Read). Data: fresh contents in the arena (frame uploads).
    public void Upload<T>(int buffer, ReadOnlySpan<T> data, int minBytes = 0) where T : unmanaged
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(minBytes >= 0)) return;
        var bytes = (ulong)Math.Max(data.Length * sizeof(T), Math.Max(minBytes, 16));
        if (data.IsEmpty)
        {
            if (b.Size < bytes) _buffers[buffer] = Own(b, bytes);
            return;
        }

        if (_frame is not null) // into the frame's uploads, which the frame keeps until the GPU is done with its commands
        {
            if (!Assert(bytes <= int.MaxValue)) return;
            var uploads = _frame.Uploads;
            var staged = uploads?.Take((int)bytes, out _, Align) ?? -1;
            if (staged < 0) uploads = Staging((int)bytes, out staged, Align); // read where it lies, as the uploads are
            if (uploads is null) return;
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>(uploads.Pointer(staged), (int)bytes));
            b.View = (uploads.Buffer, (ulong)staged, bytes);
            return;
        }

        if (_arenaUsed + bytes + Align > ArenaBytes) Flush(); // the arena is reused once its commands are done
        var offset = (_arenaUsed + Align - 1) / Align * Align;
        _arenaUsed = offset + bytes;
        if (_arena is not { } arena || !Assert(_arenaUsed <= ArenaBytes)) return;
        fixed (T* source = data)
            System.Buffer.MemoryCopy(source, arena.Mapped + offset, bytes, (ulong)(data.Length * sizeof(T)));
        b.View = (arena.Handle, offset, bytes);
    }

    public void UploadAt<T>(int buffer, int offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { Mapped: not null } b ||
            !Assert(offsetBytes >= 0) || data.IsEmpty) return;
        var bytes = (ulong)(data.Length * sizeof(T));
        if (!Assert((ulong)offsetBytes + bytes <= b.Size)) return;
        if (_frame is not null) // the GPU may still read the frames before: the data goes in with the frame's commands
        {
            if (Staging((int)bytes, out var at) is not { } staging) return;
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(data).CopyTo(new Span<byte>(staging.Pointer(at), (int)bytes));
            Copied(staging.Buffer, b.Handle,
                new Vk.BufferCopy { SourceOffset = (ulong)at, TargetOffset = (ulong)offsetBytes, Size = bytes });
            b.View = (b.Handle, 0, b.Size);
            return;
        }

        fixed (T* source = data)
            System.Buffer.MemoryCopy(source, b.Mapped + offsetBytes, b.Size - (ulong)offsetBytes, bytes);
        b.View = (b.Handle, 0, b.Size);
    }

    // On a frame: frame staging, past it an own buffer kept until GPU-done (FrameStaging); a rows region is up to 48 MB
    private HostBuffer? Staging(int bytes, out long at, int align = 16)
    {
        at = -1;
        if (_overflow is null || _frame is null || !Assert(bytes > 0) || !Assert(align is > 0 and <= Align)) return null;
        if (_collected != _frame.Number)
        {
            _overflow.Collect();
            _collected = _frame.Number;
        }

        var host = _overflow.Take(bytes, out at, align);
        if (host is null) Refused++; // no memory for a buffer of its own
        return host;
    }

    internal (ulong Buffer, ulong Offset) Native(int buffer) =>
        Index(buffer, MaxHandles) && _buffers[buffer] is { } b && Assert(b.View.Buffer != 0)
            ? (b.View.Buffer, b.View.Offset)
            : (0, 0);

    public void Zero(int buffer, int firstUint, int uints)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(firstUint >= 0 && uints > 0)) return;
        var (target, at, size) = (b.View.Buffer, b.View.Offset + 4 * (ulong)firstUint, 4 * (ulong)uints);
        if (target == 0) return;
        if (_frame is { } frame) // an op of its own: no closure per fill, a few dozen a frame
        {
            if (!frame.Fill(target, at, size)) Refused++;
            return;
        }

        Record(commands =>
        {
            VkApi.CmdFillBuffer(commands, target, at, size, 0);
            Ordered(commands); // ordered before what follows, as a GL buffer clear is
        });
    }

    // On a frame nothing waits: reads are of what the GPU wrote frames ago, already fenced
    public void Read(int buffer, int firstUint, Span<uint> into)
    {
        if (!Index(buffer, MaxHandles) || _buffers[buffer] is not { } b || !Assert(firstUint >= 0)) return;
        if (_frame is null) Flush();
        byte* source = null;
        if (b.View.Buffer == b.Handle) source = b.Mapped;
        else if (_arena is { } arena && b.View.Buffer == arena.Handle) source = arena.Mapped + b.View.Offset;
        if (source is null || !Assert(4 * (ulong)(firstUint + into.Length) <= b.View.Range)) return;
        new ReadOnlySpan<uint>(source + 4 * firstUint, into.Length).CopyTo(into);
    }

    // Nothing to flush: every buffer with own storage is host-visible and coherent
    public void Readable(int buffer, int bytes)
    {
        if (!Assert(bytes > 0)) return;
        Upload<uint>(buffer, [], bytes);
        _ = Assert(!Index(buffer, MaxHandles) || _buffers[buffer] is null or { Size: > 0 });
    }

    public void Copy(int from, int to, int firstUint, int uints)
    {
        if (!Index(from, MaxHandles) || !Index(to, MaxHandles) || _buffers[from] is not { } source ||
            _buffers[to] is not { } target || !Assert(firstUint >= 0 && uints > 0)) return;
        if (source.View.Buffer == 0 || target.View.Buffer == 0) return;
        var at = 4 * (ulong)firstUint;
        Copied(source.View.Buffer, target.View.Buffer,
            new Vk.BufferCopy
            {
                SourceOffset = source.View.Offset + at, TargetOffset = target.View.Offset + at, Size = 4 * (ulong)uints
            });
    }

    // Records the copy, ordered before what follows as a GL buffer copy is
    private void Copied(ulong from, ulong to, Vk.BufferCopy region)
    {
        if (!Assert(from != 0 && to != 0) || !Assert(region.Size > 0)) return;
        var op = Taken();
        op.Copy = (from, to, region);
        Record(op.Run);
    }

    // Waits for the GPU, which frees the arena; on a frame the frame submits instead
    public void Flush()
    {
        _ = Assert(_arenaUsed <= ArenaBytes);
        if (_frame is not null) return;
        if (_commands == IntPtr.Zero)
        {
            _arenaUsed = 0;
            return;
        }

        _commands = IntPtr.Zero;
        _ = Assert(_device.Submit([]));
        _arenaUsed = 0;
    }

    private IntPtr Recording()
    {
        if (!Assert(_handle != IntPtr.Zero)) return IntPtr.Zero;
        if (_commands == IntPtr.Zero) _commands = _device.Begin();
        return Assert(_commands != IntPtr.Zero) ? _commands : IntPtr.Zero;
    }

    private static int Free<T>(T?[] table) where T : class
    {
        if (!Assert(table.Length > 1)) return 0;
        for (var i = 1; i < Math.Min(table.Length, MaxHandles); i++)
            if (table[i] is null)
                return i;
        _ = Assert(false); // a table full of live handles
        return 0;
    }

    private BufferSlot Own(BufferSlot old, ulong bytes)
    {
        var fresh = Allocate(bytes);
        if (!NotNull(fresh) || !Assert(bytes >= old.Size)) return old;
        if (old.Mapped is not null) System.Buffer.MemoryCopy(old.Mapped, fresh.Mapped, bytes, old.Size);
        Destroy(old);
        fresh.View = (fresh.Handle, 0, bytes);
        return fresh;
    }

    // Host-visible, coherent, device-local where offered (resizable BAR): atomics and indirect reads stay off the bus
    private BufferSlot? Allocate(ulong bytes)
    {
        if (!Assert(bytes > 0) || !Assert(_handle != IntPtr.Zero)) return null;
        var info = new Vk.BufferInfo { SType = Vk.BufferCreateInfo, Size = bytes, Usage = (uint)BufferUsage };
        ulong buffer, memory = 0;
        if (VkApi.CreateBuffer(_handle, &info, null, &buffer) != Vk.Success) return null;
        Vk.MemoryRequirements needs;
        VkApi.GetBufferRequirements(_handle, buffer, &needs);
        var type = _device.MemoryType(needs.TypeBits, Vk.HostVisible | Vk.HostCoherent | Vk.DeviceLocal);
        if (type < 0) type = _device.MemoryType(needs.TypeBits, Vk.HostVisible | Vk.HostCoherent);
        var allocate = new Vk.AllocateInfo { SType = Vk.MemoryAllocateInfo, Size = needs.Size, TypeIndex = (uint)type };
        void* mapped = null;
        if (type < 0 || VkApi.AllocateMemory(_handle, &allocate, null, &memory) != Vk.Success ||
            VkApi.BindBufferMemory(_handle, buffer, memory, 0) != Vk.Success ||
            VkApi.MapMemory(_handle, memory, 0, needs.Size, 0, &mapped) != Vk.Success)
        {
            VkApi.DestroyBuffer(_handle, buffer, null);
            VkApi.FreeMemory(_handle, memory, null); // 0 when the allocation failed
            return null;
        }

        new Span<byte>(mapped, (int)Math.Min(bytes, int.MaxValue)).Clear();
        return new BufferSlot(buffer, memory, bytes, (byte*)mapped) { View = (buffer, 0, bytes) };
    }

    private void Destroy(BufferSlot buffer)
    {
        if (!NotNull(buffer) || !Assert(_handle != IntPtr.Zero)) return;
        if (buffer.Handle != 0) VkApi.DestroyBuffer(_handle, buffer.Handle, null);
        if (buffer.Memory != 0) VkApi.FreeMemory(_handle, buffer.Memory, null); // unmaps it
    }

    public void DeleteBuffer(ref int buffer)
    {
        if (!Index(buffer, MaxHandles) || buffer == 0 || !Assert(buffer != _placeholderBuffer || _disposed)) return;
        Idle();
        if (_buffers[buffer] is { } b) Destroy(b);
        _buffers[buffer] = null;
        buffer = 0;
    }

    public void Dispose()
    {
        if (!Assert(_handle != IntPtr.Zero) || !Assert(_buffers.Length == MaxHandles) || _disposed) return;
        Idle();
        _overflow?.Dispose();
        _disposed = true;
        for (var i = 1; i < MaxHandles; i++)
        {
            if (_buffers[i] is { } b) Destroy(b);
            if (_images[i] is { Shared: null } image) DestroyImage(image);
            if (_programs[i] is { } program) DestroyProgram(program);
            (_buffers[i], _images[i], _programs[i]) = (null, null, null);
        }

        if (_arena is { } arena) Destroy(arena);
        if (_sampler != 0) VkApi.DestroySampler(_handle, _sampler, null);
        (_arena, _sampler, _placeholderBuffer, _placeholderImage) = (null, 0, 0, 0);
        _outside.Clear();
    }

    // A buffer: its own storage (Handle, zero before the first Upload without data), and what descriptors use (View)
    private sealed class BufferSlot(ulong handle, ulong memory, ulong size, byte* mapped)
    {
        public ulong Handle { get; } = handle;
        public ulong Memory { get; } = memory;
        public ulong Size { get; } = size;
        public byte* Mapped { get; } = mapped;
        public (ulong Buffer, ulong Offset, ulong Range) View { get; set; }
    }
}
