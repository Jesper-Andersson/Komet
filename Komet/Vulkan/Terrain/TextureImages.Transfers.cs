namespace Komet.Vulkan;

// Images the open segment holds transfer inside it between draws (VulkanFrame.Transfer); others in the open setup, the
// next setup (Filled) or frame end (Ending), from and back to rest (shader read; new images undefined). Before the
// first frame (world join) uploads use their own ring, run in waited batches (Settle). Converted as OpenGL unpacks.
internal sealed unsafe partial class TextureImages
{
    private const int MaxQueued = 1 << 14;
    private const ulong EarlyBytes = 64UL << 20;

    private enum OpKind
    {
        Init, // undefined to the resting layout
        Fill, // from host memory (Buffer, Region)
        Mipmaps, // Levels made from the base
        Grow, // every level From has, into the larger Image
        Write // before the first frame, an upload into a buffer (Into at Region.Offset, Levels bytes from Buffer)
    }

    private readonly record struct Op(OpKind Kind, SharedImage? Image, ulong Buffer, Vk.BufferImageCopy Region, int Levels)
    {
        public SharedImage? From { get; init; }
        public (ulong Buffer, ulong At) Into { get; init; }
    }

    private readonly List<Op> _queued = [];
    private readonly FrameStaging _staging;
    private readonly Action<IntPtr> _flush;
    private HostBuffer? _early;

    private bool Fill(Entry entry, Op op)
    {
        if (!NotNull(entry) || !Assert(_queued.Count <= MaxQueued)) return false;
        if (_frame.Open && op.Kind is OpKind.Fill or OpKind.Mipmaps && _frame.Holds(op.Image!)) return InStream(op);
        if (_frame.Open)
        {
            Record(_frame.Setup, op);
            return true;
        }

        if (_queued.Count >= MaxQueued) return false; // Taken settled before its memory was taken
        _queued.Add(op);
        return true;
    }

    private bool InStream(Op op)
    {
        var held = op.Image!;
        _ = Assert(held.Image != 0) && Assert(_frame.Open);
        return _frame.Transfer(held, commands => Transferred(commands, op));
    }

    private static void Transferred(IntPtr commands, Op op)
    {
        if (!Assert(commands != IntPtr.Zero) || !NotNull(op.Image)) return;
        var region = op.Region;
        if (op.Kind == OpKind.Fill) VkApi.CmdCopyBufferToImage(commands, op.Buffer, op.Image.Image, Vk.LayoutTransferDst, 1, &region);
        else Vulkan.Mipmaps.Record(commands, op.Image, op.Levels);
    }

    private static void Record(IntPtr commands, Op op)
    {
        var image = op.Image;
        if (!Assert(commands != IntPtr.Zero)) return;
        if (op.Kind == OpKind.Write)
        {
            Written(commands, op);
            return;
        }

        if (!NotNull(image)) return;
        if (op.Kind == OpKind.Init)
        {
            image.Moved(commands, (Vk.LayoutUndefined, Vk.LayoutShaderRead));
            return;
        }

        if (op.Kind == OpKind.Grow)
        {
            if (op.From is { } from) Grow(commands, from, image);
            return;
        }

        image.Moved(commands, (Vk.LayoutShaderRead, Vk.LayoutTransferDst));
        Transferred(commands, op);
        image.Moved(commands, (Vk.LayoutTransferDst, Vk.LayoutShaderRead));
    }

    // The old image returns to rest for frames still sampling it
    private static void Grow(IntPtr commands, SharedImage from, SharedImage to)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(from.Format == to.Format)) return;
        to.Moved(commands, (Vk.LayoutUndefined, Vk.LayoutTransferDst));
        from.Moved(commands, (Vk.LayoutShaderRead, Vk.LayoutTransferSrc));
        for (var level = 0; level < Math.Min(Math.Min(from.Levels, to.Levels), MaxLevels); level++)
        {
            var layers = new Vk.Layers { Aspect = from.Format.Aspect, Level = (uint)level, Count = (uint)from.Layers };
            var (width, height) = from.LevelSize(level);
            var blit = new Vk.ImageBlit
            {
                Source = layers, SourceX1 = width, SourceY1 = height, SourceZ1 = 1, Target = layers, TargetX1 = width,
                TargetY1 = height, TargetZ1 = 1
            };
            VkApi.CmdBlitImage(commands, from.Image, Vk.LayoutTransferSrc, to.Image, Vk.LayoutTransferDst, 1, &blit,
                Vk.FilterNearest);
        }

        from.Moved(commands, (Vk.LayoutTransferSrc, Vk.LayoutShaderRead));
        to.Moved(commands, (Vk.LayoutTransferDst, Vk.LayoutShaderRead));
    }

    // Each after all earlier transfers, so writes into the same range land in order
    private static void Written(IntPtr commands, Op op)
    {
        if (!Assert(op.Into.Buffer != 0) || !Assert(op.Levels > 0)) return;
        var memory = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite,
            DstAccess = Vk.AccessTransferRead | Vk.AccessTransferWrite
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageTransfer, 0, 1, &memory, 0, null, 0, null);
        var region = new Vk.BufferCopy { SourceOffset = op.Region.Offset, TargetOffset = op.Into.At, Size = (ulong)op.Levels };
        VkApi.CmdCopyBuffer(commands, op.Buffer, op.Into.Buffer, 1, &region);
    }

    // Before the first frame: Vulkan buffer uploads (pools, mirrors) go with the texture ones (Write, Copy), in order
    public bool Before => _frame.Staging is null && Assert(!_frame.Open);

    public bool Write((ulong Buffer, ulong At) into, ReadOnlySpan<byte> data)
    {
        if (!Before || data.IsEmpty || !Assert(into.Buffer != 0)) return false;
        if (Taken(data.Length) is not var (buffer, at)) return false;
        data.CopyTo(new Span<byte>(buffer.Pointer(at), data.Length));
        return Copy((buffer.Buffer, (ulong)at), into, (ulong)data.Length);
    }

    public bool Copy((ulong Buffer, ulong At) from, (ulong Buffer, ulong At) into, ulong size)
    {
        if (!Before || !Assert(from.Buffer != 0 && into.Buffer != 0) || size is 0 or > int.MaxValue) return false;
        if (_queued.Count >= MaxQueued) Settle();
        if (_queued.Count >= MaxQueued) return false;
        _queued.Add(new Op(OpKind.Write, null, from.Buffer, new Vk.BufferImageCopy { Offset = from.At }, (int)size)
        {
            Into = into
        });
        return Assert(_queued.Count <= MaxQueued);
    }

    private void Flush(IntPtr commands)
    {
        _ = Assert(commands != IntPtr.Zero) && Assert(_queued.Count <= MaxQueued);
        foreach (var op in _queued.Bounded(MaxQueued)) Record(commands, op);
        _queued.Clear();
    }

    public void Filled(IntPtr setup)
    {
        if (_queued.Count > 0 && Assert(setup != IntPtr.Zero) && Assert(_frame.Open)) Flush(setup);
    }

    // Frame ends with no segment open: pending work runs alone, before the frame's fence
    public void Ending()
    {
        if (_queued.Count > 0 && Assert(!_frame.Open)) _ = _frame.Alone(_flush); // else the next segment's setup
        Collect(_frame.Done);
        _ = Assert(_queued.Count <= MaxQueued);
    }

    // Pre-first-frame work runs first. Vulkan-only's frame may already be open: then into its open setup, the ring
    // freed once that frame is done
    public void Started()
    {
        if (_early is not { } early) return;
        if (!_frame.Open)
        {
            Settle();
            return;
        }

        Filled(_frame.Setup);
        if (_frame.Staging is null || !Assert(_queued.Count == 0)) return;
        (_earlyGoes, _early) = ((early, _frame.Number), null);
    }

    private (HostBuffer Ring, long Frame)? _earlyGoes;

    private void EarlyGone(long done)
    {
        if (_earlyGoes is not var (ring, frame) || frame > done || !Assert(_early is null) || !Assert(frame > 0)) return;
        ring.Dispose();
        _earlyGoes = null;
    }

    // Before (and at) the first frame: pending work runs now, waited for; once frames run the ring goes
    private void Settle()
    {
        if (!Assert(!_frame.Open) || (_early is null && _queued.Count == 0)) return;
        if (_queued.Count > 0 && !_device.Run(_flush, []))
        {
            Refused += _queued.Count;
            _queued.Clear(); // the device is lost: the renderer fails at its next frame
        }

        _ = Assert(_queued.Count == 0);
        if (_early is not { } early) return;
        early.Reset();
        if (_frame.Staging is null) return;
        early.Dispose();
        _early = null;
    }

    // The frame's host memory (or an own buffer until the frame is done); before the first frame the ring, flushed when
    // it or the queue is full (never between taking memory and queueing)
    private (HostBuffer Buffer, long At)? Taken(int bytes)
    {
        if (!Assert(bytes > 0)) return null;
        if (_frame.Staging is not null) return _staging.Take(bytes, out var at) is { } buffer ? (buffer, at) : null;
        _early ??= HostBuffer.Create(_device, EarlyBytes, bulk: true);
        if (_queued.Count >= MaxQueued - MaxLevels) Settle();
        if (_early is not { } early || (ulong)bytes > EarlyBytes) return null;
        var taken = early.Take(bytes, out _, 16);
        if (taken >= 0) return (early, taken);
        Settle();
        taken = early.Take(bytes, out _, 16);
        return taken >= 0 ? (early, taken) : null;
    }

    // As OpenGL unpacks (GL_UNPACK_*; no image height or skipped images); null when impossible
    private (ulong Buffer, long At)? Staged(uint format, (int Width, int Height, int Depth) box,
        ((uint Format, uint Type) Data, IntPtr Pixels) from)
    {
        var (source, target) = TextureUploads.Bytes(format, from.Data);
        if (source == 0 || from.Pixels == 0 || !Assert(box.Depth is > 0 and <= MaxLayers)) return null;
        var unpack = GlTap.Unpack;
        var row = (unpack.RowLength > 0 ? unpack.RowLength : box.Width) * source;
        var alignment = unpack.Alignment is 1 or 2 or 4 or 8 ? unpack.Alignment : 4;
        var stride = (row + alignment - 1) / alignment * alignment;
        var layer = (long)box.Width * box.Height * target;
        if (layer * box.Depth > int.MaxValue || Taken((int)(layer * box.Depth)) is not var (buffer, at)) return null;
        var first = (byte*)from.Pixels + (long)unpack.SkipRows * stride + (long)unpack.SkipPixels * source;
        for (var z = 0; z < Math.Min(box.Depth, MaxLayers); z++)
            TextureUploads.Convert(format, from.Data, first + (long)z * stride * box.Height, (box.Width, box.Height, stride),
                new Span<byte>(buffer.Pointer(at + z * layer), (int)layer));
        return (buffer.Buffer, at);
    }

    // To the record's levels; false if the open segment holds the old one (read back instead)
    private bool Grown(Entry entry, int texture)
    {
        var old = entry.Image;
        var levels = Math.Clamp(GlTexture.Levels(texture), 1, MaxLevels);
        if (levels <= old.Levels) return Assert(levels >= 1);
        if (_frame.Open && _frame.Holds(old)) return false;
        if (Image(texture, (old.Width, old.Height, levels, old.Layers), old.Format, entry.Kind) is not { } grown)
            return false;
        grown.OpaqueAlpha = old.OpaqueAlpha;
        if (!Fill(entry, new Op(OpKind.Grow, grown, 0, default, old.Levels) { From = old }))
        {
            grown.Dispose();
            return false;
        }

        entry.Image = grown;
        Retire(old);
        Version++;
        return Assert(entry.Image.Levels == levels);
    }
}
