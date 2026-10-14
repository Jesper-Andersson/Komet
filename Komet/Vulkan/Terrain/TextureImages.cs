namespace Komet.Vulkan;

// Vulkan-only (VulkanRenderer.OnlyOn): each engine texture Vulkan uses gets its own image, made with OpenGL's storage
// calls and filled from each upload as OpenGL unpacks; mipmaps and copies by Vulkan. OpenGL gets the same calls to take
// over later. What Vulkan cannot follow (compressed data, unpack buffers, levels past the image's, OpenGL drawing into
// it) leaves the image stale, read back from OpenGL once before next use, as are pre-Vulkan-only textures. Images
// Vulkan drew or copied into are never read back. Render targets belong to TerrainTargets (Claim).
internal sealed unsafe partial class TextureImages : IDisposable
{
    public const int MaxImages = 1 << 14;
    private const int MaxLevels = 16, MaxLayers = 16, Faces = 6, MaxRetired = 1 << 14;
    private const uint Usage = Vk.Sampled | Vk.TransferSrc | Vk.TransferDst;
    private const uint Texture2D = 0x0DE1, GlBaseLevel = 0x813C;

    private readonly VulkanDevice _device;
    private readonly VulkanFrame _frame;
    private readonly Dictionary<int, Entry> _images = [];
    private readonly Retired<SharedImage> _retired = new(MaxRetired, static image => image.Dispose());

    private sealed class Entry(SharedImage image, uint kind)
    {
        public SharedImage Image { get; set; } = image;
        public uint Kind { get; } = kind; // GL_TEXTURE_2D, _2D_ARRAY or _CUBE_MAP
        public bool Stale { get; set; } // OpenGL changed it unseen: read back before Vulkan next uses it
        public bool Drawn { get; set; } // Vulkan drew or copied into it: never read back
        public int BaseLevel { get; set; }
    }

    public TextureImages(VulkanDevice device, VulkanFrame frame)
    {
        _ = NotNull(device) && NotNull(frame);
        (_device, _frame, _staging) = (device, frame, new FrameStaging(device, frame));
        _flush = Flush;
    }

    public int Count => _images.Count;

    // Bumps whenever a texture's resolution may differ: image made, gone, stale, read back, or parameter set
    public long Version { get; private set; }

    public long Uploads { get; private set; }
    public long Readbacks { get; private set; }
    public long ReadBytes { get; private set; }
    public long Unseen { get; private set; }
    public long Refused { get; private set; }

    public ulong Bytes
    {
        get
        {
            var bytes = 0UL;
            foreach (var image in _images.Values.Select(static e => e.Image).Bounded(MaxImages)) // no copy of the images
                bytes += (ulong)image.Width * (ulong)image.Height * (ulong)image.Layers * 4;
            return Assert(_images.Count <= MaxImages) ? bytes : 0;
        }
    }

    // null when none or stale (Get reads it back)
    public SharedImage? Find(int texture) =>
        Assert(texture >= 0) && Assert(_images.Count <= MaxImages) && _images.TryGetValue(texture, out var entry) &&
        !entry.Stale
            ? entry.Image
            : null;

    public Samplers.State? SamplingOf(int texture) =>
        Assert(texture >= 0) && Assert(_images.Count <= MaxImages) && _images.ContainsKey(texture)
            ? GlTexture.Sampling(texture)
            : null;

    // Read back from OpenGL first when missing or stale. stand: Vulkan draws into it.
    public SharedImage? Get(int texture, out string why, bool stand = false)
    {
        why = "";
        if (!Assert(texture > 0) || !Assert(_images.Count <= MaxImages)) return null;
        _ = _images.TryGetValue(texture, out var entry);
        if (entry is { Stale: true } && GlTap.LeanTexture((uint)texture))
            entry.Stale = false; // lean: OpenGL holds nothing to read back, the image stays as Vulkan last had it
        if (entry is null || entry.Stale)
        {
            if (entry is null || !Fits(entry, texture)) entry = Made(texture, out why); // OpenGL's shape, not the old one's
            // renderbuffers (GlTap.RenderbufferName): nothing to read back, nothing drawn yet
            if (entry is null || (!GlTap.IsRenderbuffer(texture) && !ReadBack(texture, entry, out why))) return null;
            (entry.Stale, Version) = (false, Version + 1);
        }

        entry.Drawn |= stand;
        return entry.Image;
    }

    // glTexImage*, glTex(ture)Storage*: new image when level 0's shape or format changed, then the call's data
    public void Imaged(int texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, IntPtr pixels)
    {
        if (!Assert(texture > 0) || !Assert(levels.Level >= 0)) return;
        var known = _images.GetValueOrDefault(texture);
        var entry = levels.Level == 0 && (known is null || !Fits(known, texture)) ? Made(texture, out _) : known;
        if (entry is null) return;
        if (levels.Level >= entry.Image.Levels && !Grown(entry, texture))
        {
            Stale(entry);
            return;
        }

        if (pixels == 0 || data.Format == 0) return;
        var layers = entry.Kind == GlTap.TextureCube ? 1 : Math.Max(spec.Depth, 1);
        var box = (0, 0, GlTap.Face(target), Math.Max(spec.Width, 1), Math.Max(spec.Height, 1), layers);
        if (!SubImaged(entry, levels.Level, box, (data, pixels))) Stale(entry);
    }

    // Z: layer or cube face; false: not taken (none here, or stale)
    public bool SubImaged(int texture, int level, (int X, int Y, int Z, int Width, int Height, int Depth) box,
        (uint Format, uint Type) data, IntPtr pixels)
    {
        if (!Assert(texture > 0) || !_images.TryGetValue(texture, out var entry) || entry.Stale) return false;
        if (SubImaged(entry, level, box, (data, pixels))) return Assert(Uploads > 0);
        Stale(entry);
        return false;
    }

    private bool SubImaged(Entry entry, int level, (int X, int Y, int Z, int Width, int Height, int Depth) box,
        ((uint Format, uint Type) Data, IntPtr Pixels) from)
    {
        var image = entry.Image;
        if (!NotNull(image) || level < 0 || level >= image.Levels || box.Width <= 0 || box.Height <= 0 || box.Depth <= 0)
            return Refuse();
        var (width, height) = image.LevelSize(level);
        if (box.X < 0 || box.Y < 0 || box.Z < 0 || box.X + box.Width > width || box.Y + box.Height > height ||
            box.Z + box.Depth > image.Layers) return Refuse();
        if (Staged(image.Format.Vulkan, (box.Width, box.Height, box.Depth), from) is not var (buffer, at)) return Refuse();
        var region = new Vk.BufferImageCopy
        {
            Offset = (ulong)at, Aspect = image.Format.Aspect, Level = (uint)level, BaseLayer = (uint)box.Z,
            Layers = (uint)box.Depth, X = box.X, Y = box.Y, Width = (uint)box.Width, Height = (uint)box.Height, Depth = 1
        };
        if (!Fill(entry, new Op(OpKind.Fill, image, buffer, region, 0))) return Refuse();
        Uploads++;
        return Assert(Uploads > 0);
    }

    // Record's levels already made: levels from the base, the image grown first if needed
    public bool Mipmapped(int texture)
    {
        if (!Assert(texture > 0) || !_images.TryGetValue(texture, out var entry)) return true; // read back with them
        if (entry.Stale) return false;
        var levels = Math.Min(GlTexture.Levels(texture), MaxLevels);
        if (levels <= 1) return Assert(levels == 1);
        if (entry.BaseLevel != 0 || entry.Image.Format.Aspect != Vk.AspectColor ||
            (entry.Image.Levels < levels && !Grown(entry, texture))) return false;
        if (!Fill(entry, new Op(OpKind.Mipmaps, entry.Image, 0, default, Math.Min(levels, entry.Image.Levels))))
            return false;
        Uploads++;
        return Assert(entry.Image.Levels >= 1);
    }

    public void Changed(int texture)
    {
        if (Assert(texture >= 0) && Assert(_images.Count <= MaxImages) && _images.TryGetValue(texture, out var entry))
            Stale(entry);
    }

    private void Stale(Entry entry)
    {
        if (!NotNull(entry) || entry.Drawn || entry.Stale) return;
        (entry.Stale, Unseen, Version) = (true, Unseen + 1, Version + 1);
        _ = Assert(entry.Stale);
    }

    public void Tuned(int texture, uint name, float value)
    {
        if (!Assert(texture >= 0) || !Finite(value) || !_images.TryGetValue(texture, out var entry)) return;
        if (name == GlBaseLevel) entry.BaseLevel = (int)value;
        Version++; // its sampling is the record's, read anew
    }

    // Vulkan drew (an unknown framebuffer, via its image) or copied into it
    public void Wrote(int texture)
    {
        if (Assert(texture >= 0) && Assert(_images.Count <= MaxImages) && _images.TryGetValue(texture, out var entry))
            (entry.Drawn, entry.Stale) = (true, false);
    }

    // Deleted, or now a render target: goes once the frame being drawn is done
    public void Forget(int texture)
    {
        if (!Assert(texture >= 0) || !_images.Remove(texture, out var entry)) return;
        Retire(entry.Image);
        Version++;
        _ = Assert(!_images.ContainsKey(texture));
    }

    private void Retire(SharedImage image)
    {
        if (!NotNull(image) || !Assert(_retired.Count <= MaxRetired) || _retired.Add(image, _frame.Number)) return;
        _ = _device.WaitIdle(); // too many waiting: this one goes now
        image.Dispose();
    }

    public void Collect(long done)
    {
        _retired.Collect(done);
        EarlyGone(done);
        _staging.Collect();
        _ = Assert(_retired.Count <= MaxRetired) && Assert(done >= -VulkanFrame.Slots);
    }

    private bool Refuse()
    {
        Refused++;
        return !Assert(Refused > 0) && !Assert(_images.Count <= MaxImages);
    }

    private static int LayersOf(uint kind, int depth) =>
        Assert(depth >= 0) && Assert(kind != 0) ? kind switch { GlTap.TextureCube => Faces, GlTexture.Array2D => Math.Max(depth, 1), _ => 1 } : 1;

    // From the record: kind, level 0 size and format, level count
    private Entry? Made(int texture, out string why)
    {
        why = "";
        if (!Assert(texture > 0) || !Assert(_images.Count <= MaxImages)) return null;
        var gl = GlTexture.Of(texture);
        var kind = gl.Kind is GlTexture.Array2D or (int)GlTap.TextureCube ? (uint)gl.Kind : Texture2D;
        if (gl.Kind != 0 && gl.Kind != Texture2D && kind == Texture2D) return Nothing(out why, texture, "is no 2D texture");
        var layers = LayersOf(kind, gl.Depth);
        if (gl.Width <= 0 || gl.Height <= 0) // OpenGL holds no storage for it either (a GUI text 22x0): not refused
            return Nothing(out why, texture, "has no storage", refused: false);
        if (layers > MaxLayers) return Nothing(out why, texture, $"has {layers} layers");
        if (SharedFormat.Only(gl.Format) is not var (format, opaque)) return Nothing(out why, texture, $"has format 0x{gl.Format:X}");
        var levels = Math.Clamp(GlTexture.Levels(texture), 1, MaxLevels);
        var image = Image(texture, (gl.Width, gl.Height, levels, layers), format, kind);
        if (image is null) return Nothing(out why, texture, "has no image");
        image.OpaqueAlpha = opaque;
        if (_images.Remove(texture, out var old)) Retire(old.Image);
        var entry = _images.Count < MaxImages ? new Entry(image, kind) : null;
        if (entry is null || !Fill(entry, new Op(OpKind.Init, image, 0, default, 0)))
        {
            image.Dispose();
            return Nothing(out why, texture, "has no room");
        }

        (_images[texture], Version) = (entry, Version + 1);
        return entry;
    }

    // Drawable where the format allows (a mod's framebuffer may hold it)
    private SharedImage? Image(int texture, (int Width, int Height, int Levels, int Layers) size, SharedFormat format,
        uint kind)
    {
        var attachment = (format.Aspect & Vk.AspectDepth) != 0 ? Vk.DepthAttachment : Vk.ColorAttachment; // a stencil's too
        var (cube, arrayed) = (kind == GlTap.TextureCube, kind == GlTexture.Array2D);
        var image = SharedImage.Private(_device, size, format, Usage | attachment, out _, cube, texture, arrayed);
        _ = Assert(size.Levels is > 0 and <= MaxLevels) && Assert(texture > 0);
        return image ?? SharedImage.Private(_device, size, format, Usage, out _, cube, texture, arrayed);
    }

    private Entry? Nothing(out string why, int texture, string reason, bool refused = true)
    {
        why = $"texture {texture} {reason}";
        if (refused) Refused++;
        _ = Assert(why.Length > 0) && Assert(Refused >= 0);
        return null;
    }

    private static bool Fits(Entry entry, int texture)
    {
        var gl = GlTexture.Of(texture);
        var image = entry.Image;
        var layers = LayersOf(entry.Kind, gl.Depth);
        return NotNull(image) && SharedFormat.Only(gl.Format) is var (format, _) && format == image.Format &&
               gl.Width == image.Width && gl.Height == image.Height && layers == image.Layers &&
               GlTexture.Levels(texture) <= image.Levels && Assert(texture > 0);
    }

    public void Dispose()
    {
        _ = Assert(_images.Count <= MaxImages) && _device.WaitIdle();
        foreach (var entry in _images.Values.Bounded(MaxImages)) entry.Image.Dispose();
        _images.Clear();
        _retired.Clear();
        _queued.Clear();
        _staging.Dispose();
        _early?.Dispose();
        _earlyGoes?.Ring.Dispose();
        (_early, _earlyGoes) = (null, null);
    }
}
