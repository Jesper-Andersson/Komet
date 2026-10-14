using System.Runtime.InteropServices;
using unsafe CompressedImage2DFn = delegate* unmanaged<uint, int, uint, int, int, int, int, nint, void>;
using unsafe CompressedSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, int, nint, void>;
using unsafe CompressedImage3DFn = delegate* unmanaged<uint, int, uint, int, int, int, int, int, nint, void>;
using unsafe CopyImage2DFn = delegate* unmanaged<uint, int, uint, int, int, int, int, int, void>;
using unsafe ImageMultisampleFn = delegate* unmanaged<uint, int, uint, int, int, byte, void>;
using unsafe TextureViewFn = delegate* unmanaged<uint, uint, uint, uint, uint, uint, uint, uint, void>;
using unsafe TexBufferFn = delegate* unmanaged<uint, uint, uint, void>;
using unsafe RenderbufferStorageFn = delegate* unmanaged<uint, uint, int, int, void>;
using unsafe RenderbufferMultisampleFn = delegate* unmanaged<uint, int, uint, int, int, void>;
using unsafe BindRenderbufferFn = delegate* unmanaged<uint, uint, void>;
using unsafe UnmapFn = delegate* unmanaged<uint, byte>;
using unsafe SpecFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;

namespace Komet.Vulkan;

// Vulkan-only: engine and mod texture calls reach Writes as made (TextureImaged, TextureSubImaged, TextureCopied...)
// for every target the units follow, so Vulkan's images mirror GL's textures. Unfollowable calls (compressed data,
// multisampled, views, buffer textures) are recorded and mark the texture changed (read back on Vulkan's next need) or
// are counted (Untracked). Renderbuffers are recorded for glCheckFramebufferStatus. A persistent write mapping of a
// whole buffer gets Vulkan memory instead (Mapping); GL's own mapping stays for the way back.
internal static unsafe partial class GlTap
{
    private const int SpecsAt = 31, MaxRenderbuffers = 1024;
    private const uint MapReadBit = 0x1, MapWriteBit = 0x2, MapPersistentBit = 0x40;

    private static readonly Dictionary<uint, (int Format, int Samples)> RenderbufferRecords = [];
    private static uint _renderbuffer;

    // Texture calls on targets the record does not follow (rectangle, 3D, multisampled, buffer), for the report
    public static long UntrackedCalls { get; private set; }

    // Persistent whole-buffer write mapping (buffer, size, GL's pointer): the pointer the engine gets instead, or GL's
    public static System.Func<uint, long, nint, nint>? Mapping { get; set; }

    // A framebuffer object's attachments changed (Vulkan-only: what was resolved of it goes)
    public static Action<uint>? FramebufferChanged { get; set; }

    // A buffer unmapped (glUnmapBuffer, glUnmapNamedBuffer): OpenGL's mapping of it is gone
    public static Action<uint>? Unmapping { get; set; }

    private static Entry[] SpecEntries() =>
    [
        new("glCompressedTexImage2D", (nint)(CompressedImage2DFn)(&CompressedTexImage2D), Group.Records),
        new("glCompressedTexSubImage2D", (nint)(CompressedSubImage2DFn)(&CompressedTexSubImage2D), Group.Records),
        new("glCompressedTexImage3D", (nint)(CompressedImage3DFn)(&CompressedTexImage3D), Group.Records),
        new("glCopyTexImage2D", (nint)(CopyImage2DFn)(&CopyTexImage2D), Group.Records),
        new("glTexImage2DMultisample", (nint)(ImageMultisampleFn)(&TexImage2DMultisample), Group.Records),
        new("glTextureView", (nint)(TextureViewFn)(&TextureView), Group.Records),
        new("glTexBuffer", (nint)(TexBufferFn)(&TexBuffer), Group.Records),
        new("glRenderbufferStorage", (nint)(RenderbufferStorageFn)(&RenderbufferStorage), Group.Records),
        new("glRenderbufferStorageMultisample", (nint)(RenderbufferMultisampleFn)(&RenderbufferStorageMultisample),
            Group.Records),
        new("glNamedRenderbufferStorage", (nint)(RenderbufferStorageFn)(&NamedRenderbufferStorage), Group.Records),
        new("glBindRenderbuffer", (nint)(BindRenderbufferFn)(&BindRenderbuffer), Group.Records),
        new("glUnmapBuffer", (nint)(UnmapFn)(&UnmapBuffer), Group.Records),
        new("glUnmapNamedBuffer", (nint)(UnmapFn)(&UnmapNamedBuffer), Group.Records)
    ];

    [UnmanagedCallersOnly]
    private static byte UnmapBuffer(uint target)
    {
        Unmapped(Bound(target));
        return Kept(SpecsAt + 11) && Assert(Recording) ? ((UnmapFn)Original[RecordsAt + SpecsAt + 11])(target) : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static byte UnmapNamedBuffer(uint buffer)
    {
        Unmapped(buffer);
        return Kept(SpecsAt + 12) && Assert(Recording) ? ((UnmapFn)Original[RecordsAt + SpecsAt + 12])(buffer) : (byte)0;
    }

    private static void Unmapped(uint buffer)
    {
        if (buffer != 0 && Unmapping is { } unmapping && Assert(Recording)) Heard(unmapping, buffer);
    }

    [UnmanagedCallersOnly]
    private static void CompressedTexImage2D(uint target, int level, uint format, int width, int height, int border,
        int size, nint data)
    {
        if (Kept(SpecsAt) && Assert(Recording) && Assert(level >= 0))
            ((CompressedImage2DFn)Original[RecordsAt + SpecsAt])(target, level, format, width, height, border, size, data);
        Heard(static s => Compressed(s.target, s.level, ((int)s.format, s.width, s.height, 1)),
            (target, level, format, width, height));
    }

    [UnmanagedCallersOnly]
    private static void CompressedTexSubImage2D(uint target, int level, int x, int y, int width, int height, uint format,
        int size, nint data)
    {
        if (Kept(SpecsAt + 1) && Assert(Recording) && Assert(level >= 0))
            ((CompressedSubImage2DFn)Original[RecordsAt + SpecsAt + 1])(target, level, x, y, width, height, format, size,
                data);
        if (Specifiable(target)) Changed(BoundName(target));
        else UntrackedCalls++;
    }

    [UnmanagedCallersOnly]
    private static void CompressedTexImage3D(uint target, int level, uint format, int width, int height, int depth,
        int border, int size, nint data)
    {
        if (Kept(SpecsAt + 2) && Assert(Recording) && Assert(level >= 0))
            ((CompressedImage3DFn)Original[RecordsAt + SpecsAt + 2])(target, level, format, width, height, depth, border,
                size, data);
        Heard(static s => Compressed(s.target, s.level, ((int)s.format, s.width, s.height, s.depth)),
            (target, level, format, width, height, depth));
    }

    // Recorded as specified, its contents unseen: read back as Vulkan next needs them
    private static void Compressed(uint target, int level, (int Format, int Width, int Height, int Depth) spec)
    {
        if (!Assert(level >= 0) || !Assert(Recording)) return;
        if (!Specifiable(target))
        {
            UntrackedCalls++;
            return;
        }

        var texture = BoundName(target);
        Specified(texture, target, (level, 1), spec, 0);
        Imaged(texture, target, (level, 1), spec, default, 0);
        Changed(texture);
    }

    [UnmanagedCallersOnly]
    private static void CopyTexImage2D(uint target, int level, uint format, int x, int y, int width, int height,
        int border)
    {
        if (Rests && Specifiable(target)) SpecOnly(target, level, format, (width, height)); // the copy is Vulkan's
        else if (Kept(SpecsAt + 3) && Assert(Recording) && Assert(level >= 0))
            ((CopyImage2DFn)Original[RecordsAt + SpecsAt + 3])(target, level, format, x, y, width, height, border);
        Heard(static s => CopiedSpec(s.target, s.level, (int)s.format, (s.x, s.y, s.width, s.height)),
            (target, level, format, x, y, width, height));
    }

    // glCopyTexImage2D while GL rests: GL gets the level's storage (glTexImage2D, no data) for takeover and state
    // queries, not the copy (GL's framebuffer is undrawn); lean as any spec
    private static void SpecOnly(uint target, int level, uint format, (int Width, int Height) size)
    {
        if (!Assert(level >= 0) || !Assert(Rests)) return;
        var lean = TextureLean(BoundName(target), level, (size.Width, size.Height, 1));
        var (data, type) = SpecData(format);
        var small = lean == Leaning.Small;
        if (lean != Leaning.Held && Original[TouchAt + 41] != IntPtr.Zero && Index(TouchAt + 41, _entries.Length))
            ((SpecFn)Original[TouchAt + 41])(target, level, (int)format, small ? 1 : size.Width, small ? 1 : size.Height, 0,
                data, type, 0);
    }

    // A pixel format and type glTexImage2D takes with the internal format (no data is given)
    internal static (uint Format, uint Type) SpecData(uint internalFormat) =>
        Assert(internalFormat != uint.MaxValue) && Assert(internalFormat < 1u << 20)
            ? internalFormat switch
            {
                0x1902 or (>= 0x81A5 and <= 0x81A7) or 0x8CAC => (0x1902u, 0x1406u), // depth: GL_DEPTH_COMPONENT, GL_FLOAT
                0x84F9 or 0x88F0 => (0x84F9u, 0x84FAu), // GL_DEPTH_STENCIL, GL_UNSIGNED_INT_24_8
                0x8CAD => (0x84F9u, 0x8DADu), // GL_DEPTH32F_STENCIL8: GL_FLOAT_32_UNSIGNED_INT_24_8_REV
                (>= 0x8D70 and <= 0x8D9D) or (>= 0x8231 and <= 0x823C) => (0x8D99u, 0x1401u), // integer: GL_RGBA_INTEGER
                _ => (0x1908u, 0x1401u) // GL_RGBA, GL_UNSIGNED_BYTE
            }
            : (0x1908u, 0x1401u);

    // glCopyTexImage2D: the level specified, then filled from the read framebuffer as glCopyTexSubImage2D fills it
    private static void CopiedSpec(uint target, int level, int format, (int X, int Y, int Width, int Height) rect)
    {
        if (!Assert(level >= 0) || !Assert(Recording)) return;
        if (!Specifiable(target))
        {
            UntrackedCalls++;
            return;
        }

        var texture = BoundName(target);
        Specified(texture, target, (level, 1), (format, rect.Width, rect.Height, 1), 0x1401);
        Imaged(texture, target, (level, 1), (format, rect.Width, rect.Height, 1), default, 0);
        FramebufferRead(texture, target, level, (0, 0), rect);
    }

    [UnmanagedCallersOnly]
    private static void TexImage2DMultisample(uint target, int samples, uint format, int width, int height, byte fixedAt)
    {
        if (Kept(SpecsAt + 4) && Assert(Recording) && Assert(samples >= 0))
            ((ImageMultisampleFn)Original[RecordsAt + SpecsAt + 4])(target, samples, format, width, height, fixedAt);
        UntrackedCalls++;
    }

    [UnmanagedCallersOnly]
    private static void TextureView(uint texture, uint target, uint original, uint format, uint minLevel, uint levels,
        uint minLayer, uint layers)
    {
        if (Kept(SpecsAt + 5) && Assert(Recording) && Assert(texture != uint.MaxValue))
            ((TextureViewFn)Original[RecordsAt + SpecsAt + 5])(texture, target, original, format, minLevel, levels,
                minLayer, layers);
        Heard(static t => Viewed(t), texture);
    }

    // A view shares another texture's storage: followed by nothing, asked from OpenGL each time
    private static void Viewed(uint texture)
    {
        if (texture == 0 || !Assert(Recording) || !Assert(TextureRecords.Count <= MaxRecords + MaxNames)) return;
        if (TextureRecords.TryGetValue(texture, out var t)) t.Untracked = true;
        UntrackedCalls++;
        Changed(texture);
    }

    [UnmanagedCallersOnly]
    private static void TexBuffer(uint target, uint format, uint buffer)
    {
        if (Kept(SpecsAt + 6) && Assert(Recording) && Assert(target > 0))
            ((TexBufferFn)Original[RecordsAt + SpecsAt + 6])(target, format, buffer);
        UntrackedCalls++;
    }

    [UnmanagedCallersOnly]
    private static void RenderbufferStorage(uint target, uint format, int width, int height)
    {
        if (Kept(SpecsAt + 7) && Assert(Recording) && Assert(width >= 0))
            ((RenderbufferStorageFn)Original[RecordsAt + SpecsAt + 7])(target, format, width, height);
        Heard(static s => Stored(_renderbuffer, ((int)s.format, s.width, s.height), 0), (format, width, height));
    }

    [UnmanagedCallersOnly]
    private static void RenderbufferStorageMultisample(uint target, int samples, uint format, int width, int height)
    {
        if (Kept(SpecsAt + 8) && Assert(Recording) && Assert(samples >= 0))
            ((RenderbufferMultisampleFn)Original[RecordsAt + SpecsAt + 8])(target, samples, format, width, height);
        Heard(static s => Stored(_renderbuffer, ((int)s.format, s.width, s.height), s.samples), (format, samples, width, height));
    }

    [UnmanagedCallersOnly]
    private static void NamedRenderbufferStorage(uint renderbuffer, uint format, int width, int height)
    {
        if (Kept(SpecsAt + 9) && Assert(Recording) && Assert(width >= 0))
            ((RenderbufferStorageFn)Original[RecordsAt + SpecsAt + 9])(renderbuffer, format, width, height);
        Heard(static s => Stored(s.renderbuffer, ((int)s.format, s.width, s.height), 0), (renderbuffer, format, width, height));
    }

    [UnmanagedCallersOnly]
    private static void BindRenderbuffer(uint target, uint renderbuffer)
    {
        if (Kept(SpecsAt + 10) && Assert(Recording) && Assert(target > 0))
            ((BindRenderbufferFn)Original[RecordsAt + SpecsAt + 10])(target, renderbuffer);
        _renderbuffer = renderbuffer;
    }

    // For glCheckFramebufferStatus, imaged like a texture (RenderbufferName: one level, nearest); multisample: 1 sample
    private static void Stored(uint renderbuffer, (int Format, int Width, int Height) spec, int samples)
    {
        if (renderbuffer is 0 or >= RenderbufferBase || !Recording || !Assert(samples >= 0)) return;
        if (RenderbufferRecords.Count >= MaxRenderbuffers) RenderbufferRecords.Clear();
        RenderbufferRecords[renderbuffer] = (spec.Format, samples);
        var name = RenderbufferName(renderbuffer);
        var format = Unsized(spec.Format) ? SizedRenderbuffer(spec.Format) : spec.Format;
        TextureRecords[name] = new TextureRecord
        {
            Target = Texture2D, Width = spec.Width, Height = spec.Height, Format = format, Levels = spec.Width > 0 ? 1 : 0,
            MaxLevel = 0, Min = 0x2600, Mag = 0x2600 // GL_NEAREST
        };
        if (Writes is { } writes && spec.Width > 0 && spec.Height > 0) // an image of the new shape for it
            Heard(static s => s.writes.TextureImaged(s.name, Texture2D, (0, 1), (s.format, s.Item4, s.Item5, 1), default, 0),
                (writes, name, format, spec.Width, spec.Height));
        _ = Assert(RenderbufferRecords.Count <= MaxRenderbuffers);
    }

    // A renderbuffer's unsized format as drivers size it (OpenGL answers no level query for it)
    private static int SizedRenderbuffer(int format) =>
        Assert(format >= 0) && Assert(Recording) ? format switch
        {
            0x84F9 => 0x88F0, // GL_DEPTH_STENCIL: DEPTH24_STENCIL8
            0x1902 => 0x81A6, // GL_DEPTH_COMPONENT: DEPTH_COMPONENT24
            0x1907 => 0x8051, // GL_RGB: RGB8
            _ => 0x8058 // GL_RGBA and the rest: RGBA8
        } : format;

    // A renderbuffer attachment completing a framebuffer: stored, single-sampled, sized format the attachment renders
    private static bool RenderbufferFits(uint renderbuffer, bool depth) =>
        Assert(Recording) && RenderbufferRecords.TryGetValue(renderbuffer, out var r) && Assert(r.Samples >= 0) &&
        r.Samples == 0 && !Unsized(r.Format) && DepthFormat(r.Format) == depth;

    private static void Unrenderbuffered()
    {
        _ = Assert(RenderbufferRecords.Count <= MaxRenderbuffers) && Assert(UntrackedCalls >= 0);
        RenderbufferRecords.Clear();
        (_renderbuffer, UntrackedCalls) = (0, 0);
    }

    // A face of a cube map is its layer
    internal static int Face(uint target) =>
        Assert(target > 0) && Assert(CubeLast - CubeFirst == 5) && target is >= CubeFirst and <= CubeLast
            ? (int)(target - CubeFirst)
            : 0;

    // A level the record took (glTexImage, glTexStorage: all levels) reaches Writes next. Pixel unpack data is unseen
    private static void Imaged(uint texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, nint pixels)
    {
        if (Writes is not { } writes || texture == 0 || !Assert(Recording) || !Assert(levels.Levels >= 1)) return;
        if (Unheard(texture)) return;
        Heard(static s => s.writes.TextureImaged(s.texture, s.target, s.levels, s.spec, s.data, s.Item7),
            (writes, texture, target, levels, spec, data, Unpacking ? 0 : pixels));
        if (Unpacking && data.Format != 0) Changed(texture);
    }

    private static bool SubImaged(uint texture, uint target, int level,
        (int X, int Y, int Z, int Width, int Height, int Depth) box, (uint Format, uint Type) data, nint pixels)
    {
        if (Writes is not { } writes || texture == 0 || !Assert(Recording) || !Assert(level >= 0)) return false;
        var taken = !Unheard(texture) && !Unpacking && pixels != 0 &&
                    Asked(static s => s.writes.TextureSubImaged(s.texture, s.target, s.level, s.box, s.data, s.pixels),
                        (writes, texture, target, level, box, data, pixels), false);
        if (!taken) Changed(texture);
        return taken;
    }

    // glGenerateMipmap once the record made the levels: Writes makes them too, else the texture changed
    private static void Remipmapped(uint texture)
    {
        if (Writes is not { } writes || texture == 0 || !Assert(Recording) || !Assert(texture != uint.MaxValue)) return;
        if (!Unheard(texture) && !Asked(static s => s.writes.TextureMipmapped(s.texture), (writes, texture), false)) Changed(texture);
    }

    // glCopyTexSubImage2D: a rectangle of the read framebuffer's read buffer (depth for a depth texture) into a level
    private static void FramebufferRead(uint texture, uint target, int level, (int X, int Y) at,
        (int X, int Y, int Width, int Height) rect)
    {
        if (Writes is not { } writes || texture == 0 || !Assert(Recording) || !Assert(level >= 0)) return;
        var read = Framed(_readFramebuffer);
        var from = (Fbo: (int)read, Read: ReadBufferOf(read));
        var taken = read != 0 && !Unheard(texture) && Asked(static s => s.writes.TextureRead(s.texture, s.level, (s.at.X, s.at.Y, s.face),
            s.from, s.rect), (writes, texture, level, at, face: Face(target), from, rect), false);
        if (!taken && !Rests) Changed(texture); // resting, OpenGL copies nothing either: missing, not stale
    }

    // glCopyImageSubData between two textures
    private static void ImageCopied((uint Texture, int Level, int X, int Y, int Z) from,
        (uint Texture, int Level, int X, int Y, int Z) to, (int Width, int Height, int Depth) size)
    {
        if (Writes is not { } writes || to.Texture == 0 || !Assert(Recording) || !Assert(size.Width >= 0)) return;
        var taken = from.Texture != 0 && !Unheard(to.Texture) && Asked(static s => s.writes.TextureCopied(s.to, s.from, s.size),
            (writes, to, from, size), false);
        if (!taken && !Rests) Changed(to.Texture);
    }

    // Komet's own texture work (Quietly) is not followed: the texture is changed, read back should Vulkan need it
    private static bool Unheard(uint texture)
    {
        if (_quiet == 0 || !Assert(_quiet > 0)) return false;
        Changed(texture);
        return Assert(texture > 0);
    }

    // The engine's glMapBufferRange: a persistent write mapping of the whole buffer goes to Mapping
    private static nint Mapped(uint buffer, nint offset, nint length, uint access, nint gl)
    {
        if (!Recording || _quiet > 0 || Mapping is not { } mapping || gl == 0 || buffer == 0 || offset != 0 ||
            (access & (MapReadBit | MapWriteBit | MapPersistentBit)) != (MapWriteBit | MapPersistentBit) ||
            BufferSizeOf(buffer) != length || !Assert(length > 0) || !Assert(BufferSizes.Count <= MaxRecords)) return gl;
        return Asked(static s => s.mapping(s.buffer, s.length, s.gl), (mapping, buffer, length: (long)length, gl), gl);
    }

    private static void Reattached(uint framebuffer)
    {
        if (FramebufferChanged is { } changed && Assert(framebuffer > 0) && Assert(Recording))
            Heard(changed, framebuffer);
    }
}
