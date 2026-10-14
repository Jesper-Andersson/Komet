namespace Komet.Vulkan;

// Every resource is Vulkan's: targets (TerrainTargets), textures filled from the
// uploads (TextureImages), pools and persistent mappings (TerrainPools, PersistentMaps), pool uploads from client
// memory (PoolWritten), copies as blits. OpenGL keeps its objects and gets every upload so it can take over; resting
// (GlTap.Resting) it skips what Vulkan leaves, which is missing from the picture.
internal sealed partial class TerrainRenderer
{
    // GlTap.Taps at creation: the record it heard
    public long RecordTaps { get; set; } = -1;
    public TextureImages Images { get; }
    public PersistentMaps Maps { get; }

    // Pool uploads copied, and those without host memory (the pool buffer misses them)
    public long PoolWrites { get; private set; }
    public long PoolWritesLost { get; private set; }

    private FrameStaging? _poolStaging;
    private Action<IntPtr>? _poolsRecorded;

    // Key for the scene's resolved textures: the images' changes
    public long TextureVersion => Images.Version;

    public Samplers.State? SamplingOf(int texture) => Assert(texture >= 0) ? Images.SamplingOf(texture) : null;

    // The texture's image, now held by the open segment
    public SharedImage? Copy(int texture, out string why, bool stand = false)
    {
        why = "";
        if (!Assert(texture > 0)) return null;
        var image = Images.Get(texture, out why, stand);
        if (image is not null && Frame.Open) Hold(image);
        _ = Assert(image is not null || why.Length > 0);
        return image;
    }

    // Taken on demand from rest (sampling layout)
    private void Hold(SharedImage image)
    {
        if (!NotNull(image) || Frame.Holds(image) || !Assert(Frame.Open)) return;
        _ = Frame.Take(new VulkanFrame.Shared(image, Vk.LayoutShaderRead));
    }

    // Images a draw samples and draws into, held by the open segment (targets come with every segment)
    private void Holding(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        if (!Frame.Open || !NotNull(target) || !Assert(sampled.Count <= MaxSamplers)) return;
        foreach (var image in sampled.Bounded(MaxSamplers)) Hold(image);
        foreach (var color in target.Colors.Bounded(GlTap.MaxBuffers))
            if (color is not null)
                Hold(color);
        if (target.Depth is { } depth) Hold(depth);
    }

    public void Imaged(int texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, IntPtr pixels)
    {
        if (!Assert(texture > 0) || !NotNull(Images)) return;
        if (Targets.Find(texture) is null) Images.Imaged(texture, target, levels, spec, data, pixels);
        else if (levels.Level == 0) Unshared(texture); // a render target specified anew: its image is made again
    }

    public bool SubImaged(int texture, int level, (int X, int Y, int Z, int Width, int Height, int Depth) box,
        (uint Format, uint Type) data, IntPtr pixels) =>
        Assert(texture > 0) && Assert(level >= 0) && Images.SubImaged(texture, level, box, data, pixels);

    // glCopyImageSubData: texel-exact for same formats at any level/layer (Z: layer or face, Depth of them), else a
    // single-layer converting blit. Otherwise read back from OpenGL's copy, or missing while OpenGL rests.
    public bool Copied((uint Texture, int Level, int X, int Y, int Z) to, (uint Texture, int Level, int X, int Y, int Z) from,
        (int Width, int Height, int Depth) size)
    {
        var textures = Images;
        if (!Assert(size.Width >= 0)) return false;
        var into = Targets.Find((int)to.Texture) ?? textures.Get((int)to.Texture, out _);
        var source = into is null ? null : SampledFind((int)from.Texture) ?? textures.Get((int)from.Texture, out _);
        if (into is null || source is null || ReferenceEquals(into, source)) return Uncopied("of a texture into itself or one with no image");
        var (fromAt, toAt) = ((from.Level, from.Z, Layers: size.Depth), (to.Level, to.Z, Layers: size.Depth));
        var copied = source.Format == into.Format
            ? Exact((source, into), (fromAt, toAt), ((from.X, from.Y), (to.X, to.Y), (size.Width, size.Height)))
            : size.Depth == 1 && Blitted((source, into), ((from.X, from.Y), (to.X, to.Y), (size.Width, size.Height)),
                ((from.Level, from.Z), (to.Level, to.Z)));
        if (!copied) return Uncopied($"between formats {source.Format.Vulkan} and {into.Format.Vulkan}, or out of bounds");
        textures.Wrote((int)to.Texture);
        return Assert(into.Width > 0);
    }

    // glCopyTexSubImage2D: read buffer's level/layer (depth for depth textures) into the texture's (Z: cube face)
    public bool FramebufferRead(int texture, int level, (int X, int Y, int Z) at, (int Fbo, int Read) from,
        (int X, int Y, int Width, int Height) rect)
    {
        var textures = Images;
        if (from.Fbo <= 0 || !Assert(texture > 0) || !Assert(level >= 0)) return false;
        var into = Targets.Find(texture) ?? textures.Get(texture, out _);
        if (into is null) return Uncopied("into a texture with no image");
        var attachment = (into.Format.Aspect & Vk.AspectDepth) != 0 ? GlFramebuffer.Depth : from.Read;
        if (attachment is < 0 or > GlFramebuffer.Colors || ReadAt(from.Fbo, attachment, out var read, out _) is not { } source ||
            ReferenceEquals(source, into)) return Uncopied("from a framebuffer it cannot read");
        var copied = source.Format == into.Format
            ? Exact((source, into), ((read.Level, read.Layer, 1), (level, at.Z, 1)),
                ((rect.X, rect.Y), (at.X, at.Y), (rect.Width, rect.Height)))
            : Blitted((source, into), ((rect.X, rect.Y), (at.X, at.Y), (rect.Width, rect.Height)), (read, (level, at.Z)));
        if (!copied) return Uncopied($"from a framebuffer between formats {source.Format.Vulkan} and {into.Format.Vulkan}");
        textures.Wrote(texture);
        return Assert(into.Height > 0);
    }

    // Resting OpenGL makes no copy either: nothing to read back
    private bool Uncopied(string why)
    {
        Miss(GlTap.Resting ? $"a texture copy {why}" : $"a texture copy {why} (OpenGL's is read back)");
        return !Assert(why.Length > 0) && !Assert(Missed > 0);
    }

    private bool Exact((SharedImage From, SharedImage To) images,
        ((int Level, int Layer, int Layers) From, (int Level, int Layer, int Layers) To) at,
        ((int X, int Y) From, (int X, int Y) To, (int Width, int Height) Size) region)
    {
        if (!Inside(images.From, at.From, (region.From, region.Size)) || !Inside(images.To, at.To, (region.To, region.Size)) ||
            (!Frame.Open && !Frame.Begin(out _))) return false;
        Hold(images.From);
        Hold(images.To);
        var aspect = images.From.Format.Aspect;
        var copy = new Vk.ImageCopy
        {
            Source = new Vk.Layers { Aspect = aspect, Level = (uint)at.From.Level, BaseLayer = (uint)at.From.Layer, Count = (uint)at.From.Layers },
            SourceX = region.From.X, SourceY = region.From.Y,
            Target = new Vk.Layers { Aspect = aspect, Level = (uint)at.To.Level, BaseLayer = (uint)at.To.Layer, Count = (uint)at.To.Layers },
            TargetX = region.To.X, TargetY = region.To.Y, Width = (uint)region.Size.Width, Height = (uint)region.Size.Height,
            Depth = 1
        };
        return Assert(Frame.Holds(images.To)) && Assert(Frame.Holds(images.From)) &&
               Frame.CopyImage(images.From, images.To, [copy]);
    }

    private static bool Inside(SharedImage image, (int Level, int Layer, int Layers) at, ((int X, int Y) At, (int Width, int Height) Size) box)
    {
        if (!Index(at.Level, image.Levels) || at.Layer < 0 || at.Layers <= 0 || at.Layer + at.Layers > image.Layers) return false;
        var (width, height) = (Math.Max(image.Width >> at.Level, 1), Math.Max(image.Height >> at.Level, 1));
        return box.Size.Width > 0 && box.Size.Height > 0 && box.At.X >= 0 && box.At.Y >= 0 &&
               box.At.X + box.Size.Width <= width && box.At.Y + box.Size.Height <= height && Assert(width > 0);
    }

    // Spec-required blit formats (A2B10G10R10 too); others (B10G11R11F, R16, RGBA16) per VulkanDevice.Blits. Converts
    // like OpenGL's blit: via floats, normalized rounded to nearest
    internal static readonly uint[] Blittable =
    [
        Vk.FormatRgba8, Vk.FormatSrgba8, Vk.FormatRgba16F, Vk.FormatRgba32F, Vk.FormatR8, Vk.FormatRg8, Vk.FormatR16F,
        Vk.FormatRg16F, Vk.FormatR32F, Vk.FormatA2B10G10R10
    ];

    internal static bool Converts(SharedFormat from, SharedFormat to) =>
        Assert(from.Aspect != 0) && Assert(to.Aspect != 0) && (from == to ||
                                      (Blits(from.Vulkan, false) && Blits(to.Vulkan, true)));

    private static bool Blits(uint format, bool into) =>
        Assert(format != uint.MaxValue) && (Array.IndexOf(Blittable, format) >= 0 || VulkanDevice.Blits(format, into));

    private bool Blitted((SharedImage From, SharedImage To) images,
        ((int X, int Y) From, (int X, int Y) To, (int Width, int Height) Size) region,
        ((int Level, int Layer) From, (int Level, int Layer) To) at)
    {
        if (images.From.Format.Aspect != images.To.Format.Aspect || !Converts(images.From.Format, images.To.Format) ||
            (images.From.OpaqueAlpha && !images.To.OpaqueAlpha) ||
            !Inside(images.From, (at.From.Level, at.From.Layer, 1), (region.From, region.Size)) ||
            !Inside(images.To, (at.To.Level, at.To.Layer, 1), (region.To, region.Size)) ||
            (!Frame.Open && !Frame.Begin(out _))) return false;
        Hold(images.From);
        Hold(images.To);
        var blit = new Vk.ImageBlit
        {
            Source = new Vk.Layers { Level = (uint)at.From.Level, BaseLayer = (uint)at.From.Layer, Count = 1 },
            SourceX0 = region.From.X, SourceY0 = region.From.Y, SourceX1 = region.From.X + region.Size.Width,
            SourceY1 = region.From.Y + region.Size.Height, SourceZ1 = 1,
            Target = new Vk.Layers { Level = (uint)at.To.Level, BaseLayer = (uint)at.To.Layer, Count = 1 },
            TargetX0 = region.To.X, TargetY0 = region.To.Y,
            TargetX1 = region.To.X + region.Size.Width, TargetY1 = region.To.Y + region.Size.Height, TargetZ1 = 1
        };
        _ = Assert(Frame.Open) && Assert(Frame.Holds(images.To));
        return Frame.Blit([images], blit, false);
    }

    // Vulkan mappings of deleted buffers go once the frame being drawn is done
    public void BuffersGone(ReadOnlySpan<uint> buffers)
    {
        if (Assert(buffers.Length < 1 << 20) && NotNull(Maps)) Maps.Gone(buffers, Frame.Number);
    }

    // glBufferSubData into a pool buffer (OpenGL gets it too): copied from client memory in the open segment, before
    // the next segment's draws, or at frame end
    public unsafe bool PoolWritten(uint buffer, long offset, long size, IntPtr data)
    {
        if (data == 0 || size <= 0 || Pools.BufferOf((int)buffer) is not { } to || offset < 0 || size > int.MaxValue)
            return false;
        var written = new ReadOnlySpan<byte>((void*)data, (int)size);
        var packed = to.Packed;
        if (packed)
        {
            // packed face data (CompactFaces): whole quads only, at 52/64 of the engine's offset and size
            if (!CompactFaces.Aligned(offset, size))
            {
                (PoolWritesLost, UnalignedFaces) = (PoolWritesLost + 1, UnalignedFaces + 1);
                return false;
            }

            (offset, size) = (CompactFaces.PackedOf(offset), CompactFaces.PackedOf(size));
        }

        if (offset + size > (long)to.Size) return false;
        if (Frame.Staging is null) // before the first frame: with the textures' uploads
        {
            if (!packed) return Images.Write((to.Buffer, (ulong)offset), written) ? Tallied(true) : Tallied(false);
            var bytes = new byte[size]; // a world's first pools, once
            CompactFaces.Pack(written, bytes);
            return Images.Write((to.Buffer, (ulong)offset), bytes) ? Tallied(true) : Tallied(false);
        }

        _poolStaging ??= new FrameStaging(Device, Frame);
        if (_poolStaging.Take((int)size, out var at) is not { } staging || _poolCopies.Count >= MaxPoolCopies)
        {
            PoolWritesLost++;
            return false;
        }

        if (!Assert(at >= 0)) return false;
        var into = new Span<byte>(staging.Pointer(at), (int)size);
        _ = Assert(into.Length == size) && Assert(!packed || size % CompactFaces.Packed == 0);
        if (packed) CompactFaces.Pack(written, into); // in the copy into staging the write needs anyway: one pass over the bytes
        else written.CopyTo(into);
        var copy = new PoolCopy(staging, at, to, offset, (int)size);
        if (!Frame.Open || !Frame.CopyBuffer((staging.Buffer, (ulong)at), (to.Buffer, (ulong)offset), (ulong)size))
            _poolCopies.Add(copy);
        PoolWrites++;
        return Assert(PoolWrites > 0);
    }

    // Non-whole-quad writes into packed face data (lost: should stay 0)
    public long UnalignedFaces { get; private set; }

    // Frame ends with no segment open: waiting work runs on its own
    public void Ending()
    {
        _ = Assert(!Frame.Open) && Assert(_poolCopies.Count <= MaxPoolCopies);
        PoolsAlone();
        Images.Ending();
    }

    private bool Tallied(bool written)
    {
        if (written) PoolWrites++;
        else PoolWritesLost++;
        return Assert(PoolWrites >= 0) && written;
    }

    private void PoolsAlone()
    {
        _poolsRecorded ??= PoolsRecorded;
        if (_poolCopies.Count > 0 && Assert(!Frame.Open)) _ = Frame.Alone(_poolsRecorded);
        _poolStaging?.Collect();
        _ = Assert(_poolCopies.Count <= MaxPoolCopies);
    }

    // Renderers live from mod start: the quad pattern grows with the largest engine pool
    public void Requads(int quads)
    {
        if (!Assert(quads > 0) || (ulong)quads * 24 <= Quads.Size || quads > 1 << 26) return;
        if (TerrainDraw.Quads(Device, quads) is not { } grown) return;
        Frame.Close("the quad pattern grows");
        _ = Device.WaitIdle();
        var old = Quads;
        (Quads, Recorder, _context) = (grown, new TerrainDraw(grown), null);
        old.Dispose();
        _ = Assert(Quads.Size >= (ulong)quads * 24);
    }
}
