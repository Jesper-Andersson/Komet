namespace Komet.Vulkan;

// VulkanOnlyLean lets go with the device alive: OpenGL holds 1x1 stand-ins of large textures and 16-byte ones of large
// buffers. Each gets its full size back after GPU idle, filled via host memory from Vulkan's copy (image, pool buffer,
// mirror); blank if none or inexact. Not after a device fault.
internal sealed unsafe partial class TerrainRenderer
{
    private const int MaxLeanObjects = 1 << 14;

    private readonly HashSet<int> _restored = []; // textures given back whole: HandBack writes them no more

    // Lean stand-ins for textures OpenGL got whole before lean (GlTap.LeanEarlier): Vulkan-held, drawn targets, or
    // mipmapped textures (atlas) with a current image
    public (int Count, long Bytes) LeanEarlier() =>
        Frame.Fault.Length == 0
            ? GlTap.LeanEarlier((texture, mipmapped) =>
                Targets.Find((int)texture) is not null || (mipmapped && Images.Find((int)texture) is not null))
            : (0, 0);

    // null after a fault (nothing given back)
    public (int Textures, int Blank, int Buffers, long Bytes)? Restore(BufferMirrors? mirrors)
    {
        var images = Images;
        if (Frame.Fault.Length > 0 || !Assert(Frame.Number >= 0)) return null;
        if (Frame.Open) Frame.Close("the lean textures and buffers given back to OpenGL");
        Ending(); // what waits for a segment (uploads, pool writes, mirror writes) lands before anything is read
        mirrors?.Ending();
        if (!Device.WaitIdle() || Frame.Fault.Length > 0) return null;
        var (whole, blank, buffers, bytes) = (0, 0, 0, 0L);
        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work: not lean, not heard as the engine's
        foreach (var texture in GlTap.LeanTextureNames().Bounded(MaxLeanObjects))
        {
            // target images rest per RestingOf, textures sampled
            var image = Targets.Find((int)texture) ?? images.Held((int)texture);
            if (images.Restored((int)texture, image, Targets.RestingOf((int)texture))) whole++;
            else blank++;
            _ = _restored.Add((int)texture);
        }

        foreach (var (buffer, size, usage) in GlTap.LeanBufferList().Bounded(MaxLeanObjects))
        {
            var pool = Pools.BufferOf((int)buffer);
            var from = pool is not null ? (pool.Buffer, 0UL, (long)pool.Size) : mirrors?.Held(buffer);
            bytes += Restored(buffer, (size, usage), from, pool?.Packed ?? false);
            buffers++;
        }

        return Assert(whole + blank >= 0) ? (whole, blank, buffers, bytes) : null;
    }

    // glNamedBufferData at full size with Vulkan's contents, else none. packed: read after the engine-sized region and
    // unpacked into it
    private long Restored(uint buffer, (long Size, uint Usage) spec, (ulong Buffer, ulong Offset, long Size)? from, bool packed = false)
    {
        if (!Assert(buffer > 0) || !Assert(spec.Size > 0)) return 0;
        var reach = packed ? CompactFaces.PackedOf(spec.Size) : spec.Size;
        var held = from is { } f ? Math.Min(f.Size, reach) : 0;
        _ = Assert(held >= 0) && Assert(reach >= 0);
        var copied = packed ? CompactFaces.EngineOf(held) : held;
        var room = spec.Size + (packed ? held : 0);
        var host = copied > 0 && room <= int.MaxValue ? HostBuffer.Create(Device, (ulong)room, bulk: true) : null;
        try
        {
            var read = host is not null && host.Take((int)spec.Size, out _, 16) == 0;
            if (read && packed)
            {
                var at = host!.Take((int)held, out _, 16);
                read = at >= 0 && Read(from!.Value, host.Buffer, held, (ulong)at);
                if (read) CompactFaces.Unpack(new ReadOnlySpan<byte>(host.Pointer(at), (int)held), new Span<byte>(host.Pointer(0), (int)copied));
            }
            else read = read && Read(from!.Value, host!.Buffer, copied);
            if (read && copied < spec.Size) new Span<byte>(host!.Pointer(copied), (int)(spec.Size - copied)).Clear();
            OpenTK.Graphics.OpenGL.GL.NamedBufferData((int)buffer, (int)Math.Min(spec.Size, int.MaxValue),
                read ? (IntPtr)host!.Pointer(0) : IntPtr.Zero, (OpenTK.Graphics.OpenGL.BufferUsageHint)spec.Usage);
            if (!read) copied = 0;
        }
        finally
        {
            host?.Dispose();
        }

        GlTap.Restored(buffer, false);
        return copied;
    }

    private bool Read((ulong Buffer, ulong Offset, long Size) from, ulong into, long size, ulong at = 0)
    {
        if (!Assert(from.Buffer != 0) || !Assert(size > 0)) return false;
        return Device.Run(commands =>
        {
            var region = new Vk.BufferCopy { SourceOffset = from.Offset, TargetOffset = at, Size = (ulong)size };
            VkApi.CmdCopyBuffer(commands, from.Buffer, into, 1, &region);
            VulkanFrame.HostReadable(commands);
        }, []);
    }
}
