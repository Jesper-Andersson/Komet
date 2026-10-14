using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Images whose contents Vulkan missed are read back once: all levels and layers, tight into host memory
// (glGetTextureSubImage, no pack buffer), filled like an upload; GL_PACK_* state restored.
internal sealed unsafe partial class TextureImages
{
    private const int PackAlignment = 0x0D05, PackRowLength = 0x0D02, PackSkipRows = 0x0D03, PackSkipPixels = 0x0D04;

    private bool ReadBack(int texture, Entry entry, out string why)
    {
        why = "";
        var image = entry.Image;
        if (!Assert(texture > 0) || image.Format.Pack is not var (format, type, bytes))
        {
            why = $"texture {texture} cannot be read back";
            return false;
        }

        if (GlTap.LeanTexture((uint)texture))
        {
            why = $"texture {texture} changed in a way Vulkan did not follow, and lean gave OpenGL none of it to read back";
            return false;
        }

        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work
        var saved = Packed();
        try
        {
            for (var level = 0; level < Math.Min(image.Levels, MaxLevels); level++)
                if (!LevelRead(texture, (entry, level), (format, type, bytes), out why))
                    return false;
        }
        finally
        {
            Unpacked(saved);
        }

        Readbacks++;
        return Assert(Readbacks > 0);
    }

    private bool LevelRead(int texture, (Entry Entry, int Level) at, (PixelFormat Format, PixelType Type, int Bytes) pack,
        out string why)
    {
        why = "";
        var image = at.Entry.Image;
        if (!NotNull(image) || !Assert(at.Level < image.Levels)) return false;
        var (width, height) = (Math.Max(image.Width >> at.Level, 1), Math.Max(image.Height >> at.Level, 1));
        var size = (long)width * height * image.Layers * pack.Bytes;
        if (size > int.MaxValue || Taken((int)size) is not var (buffer, offset))
        {
            why = $"no host memory to read texture {texture} back";
            return false;
        }

        GL.GetTextureSubImage(texture, at.Level, 0, 0, 0, width, height, image.Layers, pack.Format, pack.Type, (int)size,
            (IntPtr)buffer.Pointer(offset));
        var region = new Vk.BufferImageCopy
        {
            Offset = (ulong)offset, Aspect = image.Format.Aspect, Level = (uint)at.Level, Layers = (uint)image.Layers,
            Width = (uint)width, Height = (uint)height, Depth = 1
        };
        if (!Fill(at.Entry, new Op(OpKind.Fill, image, buffer.Buffer, region, 0)))
        {
            why = $"texture {texture} read back found no room to fill";
            return false;
        }

        ReadBytes += size;
        return Assert(ReadBytes > 0);
    }

    // Rows tight (alignment 1, no row length or skips), no pack buffer
    private static ((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer) Packed()
    {
        var saved = GlTap.Pack ?? (GlTap.Packing(), (uint)GL.GetInteger((GetPName)0x88ED));
        var (rows, skipRows, skipPixels, alignment) = saved.Store;
        if (alignment != 1) GL.PixelStore((PixelStoreParameter)PackAlignment, 1);
        if (rows != 0) GL.PixelStore((PixelStoreParameter)PackRowLength, 0);
        if (skipRows != 0) GL.PixelStore((PixelStoreParameter)PackSkipRows, 0);
        if (skipPixels != 0) GL.PixelStore((PixelStoreParameter)PackSkipPixels, 0);
        if (saved.Item2 != 0) GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        _ = Assert(rows >= 0) && Assert(alignment >= 0);
        return saved;
    }

    private static void Unpacked(((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer) saved)
    {
        var (rows, skipRows, skipPixels, alignment) = saved.Store;
        if (alignment != 1) GL.PixelStore((PixelStoreParameter)PackAlignment, alignment);
        if (rows != 0) GL.PixelStore((PixelStoreParameter)PackRowLength, rows);
        if (skipRows != 0) GL.PixelStore((PixelStoreParameter)PackSkipRows, skipRows);
        if (skipPixels != 0) GL.PixelStore((PixelStoreParameter)PackSkipPixels, skipPixels);
        if (saved.Buffer != 0) GL.BindBuffer(BufferTarget.PixelPackBuffer, saved.Buffer);
        _ = Assert(rows >= 0) && Assert(skipRows >= 0);
    }
}
