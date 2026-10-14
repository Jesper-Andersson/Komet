namespace Komet.Vulkan;

// Vulkan-only: glReadPixels (screenshots, atlas dump) and glGetTexImage are answered from Vulkan's images: copied out
// in the open segment, GPU awaited, written as OpenGL packs (GL_PACK_*, pack buffers). Row order matches OpenGL, so no
// flip. Formats without a conversion here stay OpenGL's (counted missing).
internal sealed unsafe partial class TerrainRenderer
{
    private const uint GlRgba = 0x1908, GlBgra = 0x80E1, GlRgb = 0x1907, GlBgr = 0x80E0, GlRed = 0x1903, GlRg = 0x8227;
    private const uint GlDepthComponent = 0x1902, GlUnsignedByte = 0x1401, GlUnsignedShort = 0x1403, GlUnsignedInt = 0x1405;
    private const uint GlFloat = 0x1406, GlHalfFloat = 0x140B;
    private const int MaxSide = 1 << 15;

    public long Answered { get; private set; }

    private const int MostHandedBack = 512, MostTargetsHandedBack = 64;
    private const long HandBackBytes = 1L << 30;

    // Vulkan hands over: its drawing into textures and targets is written into OpenGL's first
    // (TextureImages.WriteBack), within budget. Not after a device fault: nothing readable then.
    public (int Count, long Bytes, int Left) HandBack()
    {
        var images = Images;
        if (Frame.Fault.Length > 0 || !Assert(Frame.Number >= 0)) return default;
        if (Frame.Open) Frame.Close("the textures handed back to OpenGL");
        var handed = images.WriteBack(MostHandedBack, HandBackBytes, _restored);
        var targets = images.WriteBack(Targets.Images, Targets.RestingOf, (MostTargetsHandedBack, HandBackBytes - handed.Bytes),
            _restored);
        _ = Assert(handed.Count <= MostHandedBack) && Assert(targets.Count <= MostTargetsHandedBack);
        return (handed.Count + targets.Count, handed.Bytes + targets.Bytes, handed.Left + targets.Left);
    }

    public bool Answer(GlTap.ReadCall call)
    {
        if (GlTap.Pack is not { } pack) return false;
        if (pack.Buffer == 0 && call.Pixels == 0) return false; // no memory to read into: OpenGL's own error
        if (pack.Buffer != 0 && GlTap.LeanBuffer(pack.Buffer)) return Unanswered("into a pixel pack buffer OpenGL holds none of");
        var image = Source(call, out var at, out var why);
        if (image is null) return Unanswered(why);
        var (bytes, into) = (TexelBytes(image.Format.Vulkan), PixelBytes(call.Data.Format, call.Data.Type));
        if (bytes == 0 || into == 0 || ((image.Format.Aspect & Vk.AspectDepth) != 0) != (call.Data.Format == GlDepthComponent))
            return Unanswered($"of 0x{call.Data.Format:X} 0x{call.Data.Type:X} from a {image.Format.Vulkan} image");
        var level = Math.Clamp(call.Name > 0 ? call.Level : at.Level, 0, image.Levels - 1);
        var (width, height) = image.LevelSize(level);
        (int X, int Y, int Width, int Height) asked = call.Rect.Width < 0 ? (0, 0, width, height) : call.Rect; // glGetTexImage: all
        var rect = Clipped(asked, width, height);
        if (rect.Width <= 0 || rect.Height <= 0) return Assert(asked.Width >= 0); // all outside: undefined in OpenGL too
        var size = (long)rect.Width * rect.Height * bytes;
        if (size > int.MaxValue || HostBuffer.Create(Device, (ulong)size, bulk: true) is not { } host)
            return Unanswered("too large for host memory");
        try
        {
            if (!CopiedOut(image, (level, Math.Clamp(at.Layer, 0, image.Layers - 1), rect), host, out why))
                return Unanswered(why);
            Written(host.Pointer(0), (image, rect, asked), (call, pack.Store), pack.Buffer);
        }
        finally
        {
            host.Dispose();
        }

        Answered++;
        return Assert(Answered > 0);
    }

    private bool _slowRead;

    // The read waits for the whole GPU (a screenshot hitch); one over a second is logged once
    private void Slow(TimeSpan waited)
    {
        if (_slowRead || waited.TotalSeconds < 1 || !Assert(waited >= TimeSpan.Zero)) return;
        _slowRead = true;
        _logger?.Warning("Komet: an OpenGL read answered from Vulkan waited {0:0} ms for the GPU (logged once)",
            waited.TotalMilliseconds);
        _ = Assert(_slowRead);
    }

    private bool Unanswered(string why)
    {
        Miss($"an OpenGL read {why}");
        return !Assert(why.Length > 0) && !Assert(Missed > 0);
    }

    // Read framebuffer's attachment (depth for GL_DEPTH_COMPONENT), or the texture's image
    private SharedImage? Source(GlTap.ReadCall call, out (int Level, int Layer) at, out string why)
    {
        (why, at) = ("", default);
        if (!Assert(call.Level >= 0) || !Assert(call.Fbo >= 0 || call.Name > 0)) return null;
        if (call.Name > 0) return SampledFind((int)call.Name) ?? Images.Get((int)call.Name, out why);
        if (call.Fbo <= 0) return None<SharedImage>(out why, "of the window itself");
        var attachment = call.Data.Format == GlDepthComponent ? GlFramebuffer.Depth : call.Attachment;
        if (attachment is < 0 or > GlFramebuffer.Colors) return None<SharedImage>(out why, "from no known read buffer");
        return ReadAt(call.Fbo, attachment, out at, out why);
    }

    private static (int X, int Y, int Width, int Height) Clipped((int X, int Y, int Width, int Height) rect, int width,
        int height)
    {
        var (x, y) = (Math.Clamp(rect.X, 0, width), Math.Clamp(rect.Y, 0, height));
        var (right, top) = (Math.Clamp(rect.X + rect.Width, x, width), Math.Clamp(rect.Y + rect.Height, y, height));
        _ = Assert(right >= x) && Assert(top >= y);
        return (x, y, right - x, top - y);
    }

    // In its own segment: waits for everything drawn before, as OpenGL's read does
    private bool CopiedOut(SharedImage image, (int Level, int Layer, (int X, int Y, int Width, int Height) Rect) at,
        HostBuffer host, out string why)
    {
        why = "";
        if (!Frame.Open && !Frame.Begin(out why)) return false;
        Hold(image);
        var region = new Vk.BufferImageCopy
        {
            Aspect = (image.Format.Aspect & Vk.AspectDepth) != 0 ? Vk.AspectDepth : image.Format.Aspect, // a depth's depth alone
            Level = (uint)at.Level, BaseLayer = (uint)at.Layer, Layers = 1, X = at.Rect.X,
            Y = at.Rect.Y,
            Width = (uint)at.Rect.Width, Height = (uint)at.Rect.Height, Depth = 1
        };
        if (!Frame.CopyOut(image, region, host.Buffer)) return Refuse(out why, "found the image not held");
        Frame.Close("an OpenGL read answered from Vulkan");
        var start = Hitches.Now;
        var idle = Device.WaitIdle();
        Hitches.Since(Hitches.Kind.GpuWait, start);
        Slow(System.Diagnostics.Stopwatch.GetElapsedTime(start));
        if (!idle || Frame.Fault.Length > 0) return Refuse(out why, "found the GPU not done");
        return Assert(!Frame.Open) && Assert(host.Size > 0);
    }

    // Rows per GL_PACK_*; with a pack buffer bound (Pixels its offset) each row is converted aside and written into it
    private static void Written(byte* from, (SharedImage Image, (int X, int Y, int Width, int Height) Rect,
        (int X, int Y, int Width, int Height) Asked) read, (GlTap.ReadCall Call, (int RowLength, int SkipRows,
        int SkipPixels, int Alignment) Store) pack, uint buffer)
    {
        var (image, rect, asked) = read;
        var (call, store) = pack;
        var (texel, pixel) = (TexelBytes(image.Format.Vulkan), PixelBytes(call.Data.Format, call.Data.Type));
        var alignment = Math.Max(store.Alignment, 1);
        var stride = ((long)(store.RowLength > 0 ? store.RowLength : asked.Width) * pixel + alignment - 1) / alignment * alignment;
        var first = store.SkipRows * stride + (long)store.SkipPixels * pixel;
        var source = (image.Format.Vulkan, image.OpaqueAlpha);
        var aside = buffer != 0 ? (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)((long)rect.Width * pixel)) : null;
        try
        {
            using var quiet = GlTap.Quietly(); // Komet's own write into the engine's pack buffer
            for (var y = 0; y < Math.Min(rect.Height, MaxSide); y++)
            {
                var offset = first + (rect.Y - asked.Y + y) * stride + (long)(rect.X - asked.X) * pixel;
                var row = buffer != 0 ? aside : (byte*)call.Pixels + offset;
                var texels = from + (long)y * rect.Width * texel;
                for (var x = 0; x < Math.Min(rect.Width, MaxSide); x++)
                    Converted(texels + (long)x * texel, row + (long)x * pixel, source, (call.Data.Format, call.Data.Type));
                if (buffer != 0)
                    OpenTK.Graphics.OpenGL.GL.NamedBufferSubData((int)buffer, (nint)(call.Pixels + offset),
                        rect.Width * pixel, (IntPtr)aside);
            }
        }
        finally
        {
            if (aside != null) System.Runtime.InteropServices.NativeMemory.Free(aside);
        }

        _ = Assert(stride > 0) && Assert(texel > 0);
    }

    private static int TexelBytes(uint format) => !Assert(format != uint.MaxValue) || !Assert(format < 1u << 31) ? 0 : format switch
    {
        Vk.FormatRgba8 or Vk.FormatSrgba8 or Vk.FormatD32F or Vk.FormatRg16F or Vk.FormatR32F or Vk.FormatA2B10G10R10 or
            Vk.FormatD32S8 or Vk.FormatD24S8 => 4, // a depth and stencil's depth plane: a float, or 24 bits in 32
        Vk.FormatR8 => 1,
        Vk.FormatRg8 or Vk.FormatR16F or Vk.FormatR16 => 2,
        Vk.FormatRgba16F or Vk.FormatRgba16 => 8,
        Vk.FormatRgba32F => 16,
        _ => 0
    };

    private static int Components(uint format) => format switch
    {
        GlRed => 1,
        GlRg => 2,
        GlRgb or GlBgr => 3,
        GlRgba or GlBgra => 4,
        GlDepthComponent => 1,
        _ => 0
    };

    private static int TypeBytes(uint type) => type switch
    {
        GlUnsignedByte => 1,
        GlUnsignedShort or GlHalfFloat => 2,
        GlFloat or GlUnsignedInt => 4,
        _ => 0
    };

    // GL_UNSIGNED_INT for depth only; a color read of it needs an integer image
    private static int PixelBytes(uint format, uint type) =>
        !Assert(format != uint.MaxValue) || !Assert(type != uint.MaxValue) || (type == GlUnsignedInt && format != GlDepthComponent) ||
        (type == GlHalfFloat && format == GlDepthComponent)
            ? 0
            : Components(format) * TypeBytes(type);

    private static void Converted(byte* from, byte* to, (uint Format, bool Opaque) source, (uint Format, uint Type) into)
    {
        if (source.Format is Vk.FormatD32F or Vk.FormatD32S8 || !Assert(from != null))
        {
            Put(to, 0, *(float*)from, into.Type);
            return;
        }

        if (source.Format == Vk.FormatD24S8)
        {
            Put(to, 0, (*(uint*)from & 0xFFFFFF) / (float)0xFFFFFF, into.Type);
            return;
        }

        var rgba = Texel(from, source.Format);
        if (source.Opaque) rgba.W = 1;
        var (count, swapped) = (Components(into.Format), into.Format is GlBgra or GlBgr);
        Span<float> values = [swapped ? rgba.Z : rgba.X, rgba.Y, swapped ? rgba.X : rgba.Z, rgba.W];
        for (var i = 0; i < Math.Min(count, 4); i++) Put(to, i, values[i], into.Type);
        _ = Assert(count is >= 1 and <= 4);
    }

    // Normalized types clamped, as OpenGL does
    private static void Put(byte* pixel, int at, float value, uint type)
    {
        if (!Assert(pixel != null) || !Assert(at is >= 0 and < 4)) return;
        var unit = Math.Clamp(value, 0f, 1f);
        switch (type)
        {
            case GlFloat: ((float*)pixel)[at] = value; break;
            case GlHalfFloat: ((Half*)pixel)[at] = (Half)value; break;
            case GlUnsignedShort: ((ushort*)pixel)[at] = (ushort)MathF.Round(unit * ushort.MaxValue); break;
            case GlUnsignedInt: ((uint*)pixel)[at] = (uint)Math.Round(unit * (double)uint.MaxValue); break;
            default: pixel[at] = (byte)MathF.Round(unit * 255); break;
        }
    }

    private static System.Numerics.Vector4 Texel(byte* at, uint format)
    {
        _ = Assert(at != null) && Assert(format != Vk.FormatD32F);
        return format switch
        {
            Vk.FormatRgba16F => new System.Numerics.Vector4((float)((Half*)at)[0], (float)((Half*)at)[1],
                (float)((Half*)at)[2], (float)((Half*)at)[3]),
            Vk.FormatRgba32F => new System.Numerics.Vector4(((float*)at)[0], ((float*)at)[1], ((float*)at)[2], ((float*)at)[3]),
            Vk.FormatR8 => new System.Numerics.Vector4(at[0] / 255f, 0, 0, 1),
            Vk.FormatRg8 => new System.Numerics.Vector4(at[0] / 255f, at[1] / 255f, 0, 1),
            Vk.FormatR16F => new System.Numerics.Vector4((float)((Half*)at)[0], 0, 0, 1),
            Vk.FormatRg16F => new System.Numerics.Vector4((float)((Half*)at)[0], (float)((Half*)at)[1], 0, 1),
            Vk.FormatR32F => new System.Numerics.Vector4(((float*)at)[0], 0, 0, 1),
            Vk.FormatR16 => new System.Numerics.Vector4(((ushort*)at)[0] / 65535f, 0, 0, 1),
            Vk.FormatRgba16 => new System.Numerics.Vector4(((ushort*)at)[0], ((ushort*)at)[1], ((ushort*)at)[2],
                ((ushort*)at)[3]) / 65535f,
            Vk.FormatA2B10G10R10 => Packed1010102(*(uint*)at),
            _ => new System.Numerics.Vector4(at[0], at[1], at[2], at[3]) / 255f
        };
    }

    // VK_FORMAT_A2B10G10R10_UNORM_PACK32: red in the low ten bits, alpha in the top two
    private static System.Numerics.Vector4 Packed1010102(uint word) =>
        Assert(word <= uint.MaxValue) && Assert(1023f > 0)
            ? new System.Numerics.Vector4((word & 1023) / 1023f, ((word >> 10) & 1023) / 1023f, ((word >> 20) & 1023) / 1023f,
                (word >> 30) / 3f)
            : default;
}
