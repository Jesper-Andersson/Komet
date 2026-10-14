using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Komet.Vulkan;

// As OpenGL: BGRA swapped, RGB opaque, signed shorts normalized (negatives to 0), floats to RGBA16F round-to-even; one
// and two channels, sRGB, RGB floats and depth too (Extra).
internal static unsafe class TextureUploads
{
    private const uint Rgba = 0x1908, Bgra = 0x80E1, Rgb = 0x1907, Bgr = 0x80E0, Red = 0x1903, Rg = 0x8227, Depth = 0x1902;
    private const uint UnsignedByte = 0x1401, Short = 0x1402, UnsignedShort = 0x1403, Float = 0x1406, HalfFloat = 0x140B;
    private const uint UnsignedInt = 0x1405, Rev8888 = 0x8367;
    private const uint Rgba8 = Vk.FormatRgba8, Rgba16F = Vk.FormatRgba16F, Rgba16 = Vk.FormatRgba16, Rgba32F = Vk.FormatRgba32F;
    private const uint Srgba8 = Vk.FormatSrgba8, R8 = Vk.FormatR8, Rg8 = Vk.FormatRg8, R16F = Vk.FormatR16F;
    private const uint Rg16F = Vk.FormatRg16F, R32F = Vk.FormatR32F, D32F = Vk.FormatD32F;
    private const int MaxSide = 16384, MaxValues = MaxSide * 4;

    public static (int Source, int Target) Bytes(uint target, (uint Format, uint Type) data) =>
        Assert(target > 0) && Common(target, data) is var bytes && bytes.Source > 0 && Assert(bytes.Source >= 0)
            ? bytes
            : Extra(target, data);

    private static (int Source, int Target) Extra(uint target, (uint Format, uint Type) data) => (target, data) switch
    {
        (Srgba8 or Rgba8, (Rgba or Bgra, UnsignedByte or Rev8888)) => (4, 4),
        (Srgba8, (Rgb, UnsignedByte)) => (3, 4),
        (Rgba8 or Srgba8, (Bgr, UnsignedByte)) => (3, 4),
        (R8, (Red, UnsignedByte)) => (1, 1),
        (Rg8, (Rg, UnsignedByte)) => (2, 2),
        (R16F, (Red, Float)) => (4, 2),
        (R16F, (Red, HalfFloat)) => (2, 2),
        (Rg16F, (Rg, Float)) => (8, 4),
        (Rg16F, (Rg, HalfFloat)) => (4, 4),
        (R32F, (Red, Float)) => (4, 4),
        (Rgba16F, (Rgb, Float)) => (12, 8),
        (Rgba16F, (Rgb, HalfFloat)) => (6, 8),
        (Rgba32F, (Rgb, Float)) => (12, 16),
        (D32F, (Depth, Float or UnsignedInt)) => (4, 4),
        (D32F, (Depth, UnsignedShort)) => (2, 4),
        _ => (0, 0)
    };

    private static (int Source, int Target) Common(uint target, (uint Format, uint Type) data) => (target, data) switch
    {
        (Rgba8, (Rgba or Bgra, UnsignedByte)) => (4, 4),
        (Rgba8, (Rgb, UnsignedByte)) => (3, 4),
        (Rgba16, (Rgba, Short or UnsignedShort)) => (8, 8),
        (Rgba32F, (Rgba, Float)) => (16, 16),
        (Rgba16F, (Rgba, Float)) => (16, 8),
        (Rgba16F, (Rgba, HalfFloat)) => (8, 8),
        _ => (0, 0)
    };

    public static void Convert(uint target, (uint Format, uint Type) data, byte* source, (int Width, int Height, int Stride) rows,
        Span<byte> into)
    {
        var (sourceBytes, targetBytes) = Bytes(target, data);
        if (sourceBytes == 0 || source == null || !Assert(rows.Width is > 0 and <= MaxSide) ||
            !Assert(rows.Height is > 0 and <= MaxSide) || !Assert(into.Length >= rows.Width * rows.Height * targetBytes)) return;
        for (var y = 0; y < Math.Min(rows.Height, MaxSide); y++)
        {
            var row = new ReadOnlySpan<byte>(source + (long)y * rows.Stride, rows.Width * sourceBytes);
            var packed = into.Slice(y * rows.Width * targetBytes, rows.Width * targetBytes);
            Row(target, data, row, packed);
        }
    }

    private static void Row(uint target, (uint Format, uint Type) data, ReadOnlySpan<byte> row, Span<byte> into)
    {
        _ = Assert(row.Length > 0) && Assert(into.Length > 0);
        switch (target, data)
        {
            case (Rgba8, (Bgra, UnsignedByte)):
                Swapped(MemoryMarshal.Cast<byte, uint>(row), MemoryMarshal.Cast<byte, uint>(into));
                break;
            case (Rgba8, (Rgb, UnsignedByte)):
                Opaque(row, into);
                break;
            case (Rgba16, (Rgba, Short)):
                Normalized(MemoryMarshal.Cast<byte, short>(row), MemoryMarshal.Cast<byte, ushort>(into));
                break;
            case (Rgba16F, (Rgba, Float)):
                Halves(MemoryMarshal.Cast<byte, float>(row), MemoryMarshal.Cast<byte, Half>(into));
                break;
            default:
                OnlyRow(target, data, row, into);
                break;
        }
    }

    // Pairs only Vulkan-only converts; the rest copied byte for byte
    private static void OnlyRow(uint target, (uint Format, uint Type) data, ReadOnlySpan<byte> row, Span<byte> into)
    {
        _ = Assert(row.Length > 0) && Assert(into.Length > 0);
        switch (target, data)
        {
            case (Rgba8 or Srgba8, (Bgra, UnsignedByte or Rev8888)):
                Swapped(MemoryMarshal.Cast<byte, uint>(row), MemoryMarshal.Cast<byte, uint>(into));
                break;
            case (Srgba8, (Rgb, UnsignedByte)):
                Opaque(row, into);
                break;
            case (Rgba8 or Srgba8, (Bgr, UnsignedByte)):
                OpaqueSwapped(row, into);
                break;
            case (R16F or Rg16F, (Red or Rg, Float)):
                Halves(MemoryMarshal.Cast<byte, float>(row), MemoryMarshal.Cast<byte, Half>(into));
                break;
            case (Rgba16F, (Rgb, Float)):
                OpaqueHalves(MemoryMarshal.Cast<byte, float>(row), MemoryMarshal.Cast<byte, Half>(into));
                break;
            case (Rgba16F, (Rgb, HalfFloat)):
                OpaqueOf(MemoryMarshal.Cast<byte, Half>(row), MemoryMarshal.Cast<byte, Half>(into), Half.One);
                break;
            case (Rgba32F, (Rgb, Float)):
                OpaqueOf(MemoryMarshal.Cast<byte, float>(row), MemoryMarshal.Cast<byte, float>(into), 1f);
                break;
            case (D32F, (Depth, UnsignedInt or UnsignedShort)):
                Depths(row, MemoryMarshal.Cast<byte, float>(into), data.Type == UnsignedShort);
                break;
            default:
                row.CopyTo(into);
                break;
        }
    }

    private static void OpaqueSwapped(ReadOnlySpan<byte> bgr, Span<byte> rgba)
    {
        var n = Math.Min(bgr.Length / 3, rgba.Length / 4);
        _ = Assert(n >= 0) && Assert(bgr.Length % 3 == 0);
        for (var i = 0; i < Math.Min(n, MaxValues); i++)
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (bgr[3 * i + 2], bgr[3 * i + 1], bgr[3 * i], 255);
    }

    private static void OpaqueHalves(ReadOnlySpan<float> rgb, Span<Half> rgba)
    {
        var n = Math.Min(rgb.Length / 3, rgba.Length / 4);
        _ = Assert(n >= 0) && Assert(rgb.Length % 3 == 0);
        for (var i = 0; i < Math.Min(n, MaxValues); i++)
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) =
                ((Half)rgb[3 * i], (Half)rgb[3 * i + 1], (Half)rgb[3 * i + 2], Half.One);
    }

    internal static void OpaqueOf<T>(ReadOnlySpan<T> rgb, Span<T> rgba, T one) where T : unmanaged
    {
        var n = Math.Min(rgb.Length / 3, rgba.Length / 4);
        _ = Assert(n >= 0) && Assert(rgb.Length % 3 == 0);
        for (var i = 0; i < Math.Min(n, MaxValues); i++)
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2], one);
    }

    // Normalized depths (GL_UNSIGNED_INT or SHORT) as DEPTH_COMPONENT32F floats
    internal static void Depths(ReadOnlySpan<byte> source, Span<float> into, bool shorts)
    {
        var n = Math.Min(shorts ? source.Length / 2 : source.Length / 4, into.Length);
        _ = Assert(n >= 0) && Assert(into.Length >= n);
        var wide = MemoryMarshal.Cast<byte, uint>(source);
        var narrow = MemoryMarshal.Cast<byte, ushort>(source);
        for (var i = 0; i < Math.Min(n, MaxValues); i++)
            into[i] = shorts ? narrow[i] / 65535f : (float)(wide[i] / 4294967295.0);
    }

    internal static void Swapped(ReadOnlySpan<uint> bgra, Span<uint> rgba)
    {
        var n = Math.Min(bgra.Length, rgba.Length);
        var vectors = Vector256.IsHardwareAccelerated ? n / Vector256<uint>.Count : 0;
        if (!Assert(n >= 0) || !Assert(vectors * Vector256<uint>.Count <= n)) return;
        var (keep, low) = (Vector256.Create(0xFF00FF00u), Vector256.Create(0xFFu));
        for (var v = 0; v < Math.Min(vectors, MaxValues); v++)
        {
            var at = v * Vector256<uint>.Count;
            var p = Vector256.Create(bgra[at..]);
            ((p & keep) | ((p >> 16) & low) | ((p & low) << 16)).CopyTo(rgba[at..]);
        }

        for (var i = vectors * Vector256<uint>.Count; i < Math.Min(n, MaxValues); i++)
        {
            var p = bgra[i];
            rgba[i] = (p & 0xFF00FF00u) | ((p >> 16) & 0xFFu) | ((p & 0xFFu) << 16);
        }
    }

    private static void Opaque(ReadOnlySpan<byte> rgb, Span<byte> rgba)
    {
        var n = Math.Min(rgb.Length / 3, rgba.Length / 4);
        _ = Assert(n >= 0) && Assert(rgb.Length % 3 == 0);
        for (var i = 0; i < Math.Min(n, MaxValues); i++)
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2], 255);
    }

    // Clamped to [0, 1], * 65535 round-to-even: for s in [0, 32767] that is 2s + (s >= 16384): integer lanes, no
    // widening
    internal static void Normalized(ReadOnlySpan<short> source, Span<ushort> into)
    {
        var n = Math.Min(source.Length, into.Length);
        var vectors = Vector256.IsHardwareAccelerated ? n / Vector256<short>.Count : 0;
        if (!Assert(n >= 0) || !Assert(vectors * Vector256<short>.Count <= n)) return;
        for (var v = 0; v < Math.Min(vectors, MaxValues); v++)
        {
            var at = v * Vector256<short>.Count;
            var clamped = Vector256.Max(Vector256.Create(source[at..]), Vector256<short>.Zero).AsUInt16();
            (clamped + clamped + Vector256.ShiftRightLogical(clamped, 14)).CopyTo(into[at..]);
        }

        for (var i = vectors * Vector256<short>.Count; i < Math.Min(n, MaxValues); i++) into[i] = Unorm(source[i]);
    }

    internal static ushort Unorm(short value)
    {
        var clamped = Math.Max((int)value, 0);
        _ = Assert(clamped <= short.MaxValue) && Assert(clamped >> 14 <= 1);
        return (ushort)(2 * clamped + (clamped >> 14));
    }

    private static void Halves(ReadOnlySpan<float> source, Span<Half> into)
    {
        var n = Math.Min(source.Length, into.Length);
        _ = Assert(n >= 0) && Assert(source.Length == into.Length);
        for (var i = 0; i < Math.Min(n, MaxValues); i++) into[i] = (Half)source[i];
    }
}
