using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Vulkan-only lets go: OpenGL's textures lack what only Vulkan drew or copied (map tiles, icons, mod framebuffers,
// distant shadow map, render targets). Each such image is written into its texture once via host memory, all levels and
// layers; lean textures (GlTap.Lean) are respecified at full size first (Restored). Waits for the GPU: a switch.
internal sealed unsafe partial class TextureImages
{
    private const int UnpackImageHeight = 0x806E, UnpackSkipImages = 0x806D, UnpackBuffer = 0x88EF;
    private const int Binding2D = 0x8069, BindingArray = 0x8C1D, BindingCube = 0x8514;

    // Written, bytes, and those left (past most or budget, or no exact read). skip: already restored whole
    public (int Count, long Bytes, int Left) WriteBack(int most, long budget, IReadOnlySet<int> skip)
    {
        if (!Assert(most > 0) || !Assert(budget > 0) || !NotNull(skip) || !_device.WaitIdle()) return default;
        return HandedAll(_images
            .Where(p => p.Value is { Stale: false } && (p.Value.Drawn || GlTap.Behind((uint)p.Key)) && !GlTap.LeanTexture((uint)p.Key) && !skip.Contains(p.Key) &&
                        !GlTap.IsRenderbuffer(p.Key)) // a renderbuffer takes no upload: it stays as OpenGL had it
            .Select(p => (p.Key, p.Value.Image, Math.Clamp(GlTexture.Levels(p.Key), 1, p.Value.Image.Levels), // none past OpenGL's
                Vk.LayoutShaderRead)), (most, budget));
    }

    // Render targets (TerrainTargets' images): like textures, within what is left
    public (int Count, long Bytes, int Left) WriteBack(IEnumerable<SharedImage> targets, System.Func<int, int> resting,
        (int Most, long Budget) room, IReadOnlySet<int> skip)
    {
        if (!NotNull(targets) || !NotNull(resting) || !NotNull(skip) || room.Most <= 0 || room.Budget <= 0) return default;
        return HandedAll(targets
            .Where(i => i.Texture > 0 && !GlTap.LeanTexture((uint)i.Texture) && !skip.Contains(i.Texture))
            .Select(i => (i.Texture, i, 1, resting(i.Texture))), room);
    }

    // In Komet's quiet with tight unpack state, while they fit
    private (int Count, long Bytes, int Left) HandedAll(IEnumerable<(int Texture, SharedImage Image, int Levels, int Resting)> from,
        (int Most, long Budget) room)
    {
        if (!NotNull(from) || !Assert(room.Most >= 0) || !Assert(room.Budget >= 0)) return default;
        var (count, bytes, left) = (0, 0L, 0);
        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work
        var saved = Unpacked();
        try
        {
            foreach (var (texture, image, levels, resting) in from.Bounded(MaxImages))
            {
                if (Handed(texture, (image, levels, resting), (room.Most - count, room.Budget - bytes)) is not { } size)
                {
                    left++;
                    continue;
                }

                (count, bytes) = (count + 1, bytes + size);
            }
        }
        finally
        {
            Repacked(saved);
        }

        return Assert(count <= room.Most) && Assert(bytes <= room.Budget) ? (count, bytes, left) : default;
    }

    // null when it does not fit or is not exactly readable
    private long? Handed(int texture, (SharedImage Image, int Levels, int Resting) from, (int Most, long Budget) room)
    {
        var size = Footprint(from.Image, from.Levels);
        if (room.Most <= 0 || size <= 0 || size > room.Budget || !Assert(texture > 0) || !Assert(from.Levels > 0)) return null;
        return Written(texture, from, size) ? size : null;
    }

    // Tight per PackOf; 0 for formats without exact read
    private static long Footprint(SharedImage image, int levels)
    {
        if (!NotNull(image) || image.Format.Pack is not var (_, _, texel)) return 0;
        var bytes = 0L;
        for (var level = 0; level < Math.Min(levels, MaxLevels); level++)
            bytes += (long)Math.Max(image.Width >> level, 1) * Math.Max(image.Height >> level, 1) * image.Layers * texel;
        return Assert(bytes >= 0) && bytes < int.MaxValue ? bytes : 0;
    }

    private bool Written(int texture, (SharedImage Image, int Levels, int Resting) from, long size)
    {
        var (image, levels, _) = from;
        if (!Assert(texture > 0) || image.Format.Pack is not var (format, type, texel)) return false;
        using var host = HostBuffer.Create(_device, (ulong)size, bulk: true);
        if (host is null || host.Take((int)size, out _, 16) != 0 || !CopiedOut(from, host.Buffer, texel)) return false;
        var at = 0L;
        for (var level = 0; level < Math.Min(levels, MaxLevels); level++)
        {
            var (width, height) = image.LevelSize(level);
            var pixels = (IntPtr)host.Pointer(at);
            if (!image.Arrayed && !image.Cube)
                GL.TextureSubImage2D(texture, level, 0, 0, width, height, format, type, pixels);
            else GL.TextureSubImage3D(texture, level, 0, 0, 0, width, height, image.Layers, format, type, pixels);
            at += (long)width * height * image.Layers * texel;
        }

        return Assert(at == size);
    }

    private bool CopiedOut((SharedImage Image, int Levels, int Resting) from, ulong buffer, int texel)
    {
        var (image, levels, resting) = from;
        if (!NotNull(image) || !Assert(buffer != 0) || !Assert(resting > 0)) return false;
        var aspect = Planes(image.Format.Aspect);
        return _device.Run(commands =>
        {
            image.Moved(commands, (resting, Vk.LayoutTransferSrc));
            var at = 0UL;
            for (var level = 0; level < Math.Min(levels, MaxLevels); level++)
            {
                var (width, height) = image.LevelSize(level);
                var region = new Vk.BufferImageCopy
                {
                    Offset = at, Aspect = aspect, Level = (uint)level, Layers = (uint)image.Layers,
                    Width = (uint)width, Height = (uint)height, Depth = 1
                };
                VkApi.CmdCopyImageToBuffer(commands, image.Image, Vk.LayoutTransferSrc, buffer, 1, &region);
                at += (ulong)((long)width * height * image.Layers * texel);
            }

            image.Moved(commands, (Vk.LayoutTransferSrc, resting));
            VulkanFrame.HostReadable(commands);
        }, []);
    }

    // Buffer copies take one plane: depth of a depth-stencil image
    private static uint Planes(uint aspect) =>
        Assert(aspect != 0) && Assert(aspect <= 7) && (aspect & Vk.AspectDepth) != 0 ? Vk.AspectDepth : aspect;

    // Any state: even stale it is closest, as OpenGL holds nothing
    public SharedImage? Held(int texture) =>
        Assert(texture > 0) && Assert(_images.Count <= MaxImages) && _images.TryGetValue(texture, out var entry) ? entry.Image : null;

    // Every recorded level respecified (own internal format), filled from a same-shape image, else blank. True when
    // filled.
    public bool Restored(int texture, SharedImage? image, int resting)
    {
        var gl = GlTexture.Of(texture);
        var kind = (uint)gl.Kind;
        if (!Assert(texture > 0) || gl.Width <= 0 || gl.Height <= 0 || kind is not (Texture2D or GlTexture.Array2D or GlTap.TextureCube))
        {
            GlTap.Restored((uint)texture, true); // nothing known to give it: OpenGL keeps its stand-in (sampled, never past it)
            return false;
        }

        var levels = Math.Clamp(GlTexture.Levels(texture), 1, MaxLevels);
        var layers = LayersOf(kind, gl.Depth);
        var source = image is not null && (image.Width, image.Height, image.Layers) == (gl.Width, gl.Height, layers) ? image : null;
        var filled = Math.Min(levels, source?.Levels ?? 0);
        var size = source is null ? 0 : Footprint(source, filled);
        using var host = size > 0 ? HostBuffer.Create(_device, (ulong)size, bulk: true) : null;
        var full = host is not null && host.Take((int)size, out _, 16) == 0 &&
                   CopiedOut((source!, filled, resting), host.Buffer, source!.Format.Pack!.Value.Bytes);
        var saved = Unpacked();
        var bound = GL.GetInteger((GetPName)(kind switch { GlTap.TextureCube => BindingCube, GlTexture.Array2D => BindingArray, _ => Binding2D }));
        try
        {
            GL.BindTexture((TextureTarget)kind, texture);
            Specified((kind, (uint)gl.Format), (gl.Width, gl.Height, layers, levels), full ? (source, filled, host) : (null, 0, null));
        }
        finally
        {
            GL.BindTexture((TextureTarget)kind, bound);
            Repacked(saved);
        }

        GlTap.Restored((uint)texture, true);
        return full;
    }

    // glTexImage2D (cube: six faces) or glTexImage3D (array layers) per level, pixels from the host buffer where
    // present
    private static void Specified((uint Kind, uint Format) texture, (int Width, int Height, int Layers, int Levels) size,
        (SharedImage? Image, int Levels, HostBuffer? Host) data)
    {
        var (kind, format) = texture;
        var pack = data.Image is { } image ? image.Format.Pack : null;
        var (blankFormat, blankType) = GlTap.SpecData(format);
        var at = 0L;
        for (var level = 0; level < Math.Min(size.Levels, MaxLevels); level++)
        {
            var (width, height) = (Math.Max(size.Width >> level, 1), Math.Max(size.Height >> level, 1));
            var given = level < data.Levels && pack is not null && data.Host is not null;
            var (pf, pt) = given ? (pack!.Value.Format, pack.Value.Type) : ((PixelFormat)blankFormat, (PixelType)blankType);
            var face = (long)width * height * (given ? pack!.Value.Bytes : 0);
            var pixels = given ? (IntPtr)data.Host!.Pointer(at) : IntPtr.Zero;
            if (kind == GlTexture.Array2D)
                GL.TexImage3D(TextureTarget.Texture2DArray, level, (PixelInternalFormat)format, width, height, size.Layers, 0, pf,
                    pt, pixels);
            else
                for (var f = 0; f < Math.Min(kind == GlTap.TextureCube ? Faces : 1, Faces); f++)
                    GL.TexImage2D(kind == GlTap.TextureCube ? TextureTarget.TextureCubeMapPositiveX + f : TextureTarget.Texture2D,
                        level, (PixelInternalFormat)format, width, height, 0, pf, pt, given ? pixels + (nint)(f * face) : 0);
            at += face * size.Layers;
        }

        _ = Assert(size.Levels <= MaxLevels) && Assert(at >= 0);
    }

    // Rows tight (no unpack buffer, no skips), restored after
    private static (int RowLength, int SkipRows, int SkipPixels, int Alignment, int ImageHeight, int SkipImages, int Buffer)
        Unpacked()
    {
        var saved = (GL.GetInteger(GetPName.UnpackRowLength), GL.GetInteger(GetPName.UnpackSkipRows),
            GL.GetInteger(GetPName.UnpackSkipPixels), GL.GetInteger(GetPName.UnpackAlignment),
            GL.GetInteger((GetPName)UnpackImageHeight), GL.GetInteger((GetPName)UnpackSkipImages),
            GL.GetInteger((GetPName)UnpackBuffer));
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.PixelStore((PixelStoreParameter)UnpackImageHeight, 0);
        GL.PixelStore((PixelStoreParameter)UnpackSkipImages, 0);
        if (saved.Item7 != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        _ = Assert(saved.Item4 is 1 or 2 or 4 or 8) && Assert(saved.Item1 >= 0);
        return saved;
    }

    private static void Repacked(
        (int RowLength, int SkipRows, int SkipPixels, int Alignment, int ImageHeight, int SkipImages, int Buffer) saved)
    {
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, saved.RowLength);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, saved.SkipRows);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, saved.SkipPixels);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, saved.Alignment);
        GL.PixelStore((PixelStoreParameter)UnpackImageHeight, saved.ImageHeight);
        GL.PixelStore((PixelStoreParameter)UnpackSkipImages, saved.SkipImages);
        if (saved.Buffer != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, saved.Buffer);
        _ = Assert(saved.Alignment > 0) && Assert(saved.Buffer >= 0);
    }
}
