using System.Runtime.InteropServices;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class TextureUploadsTests
{
    [Test]
    public void EveryShortIsNormalizedAsOpenGlDoes()
    {
        var shorts = Enumerable.Range(short.MinValue, 65536).Select(v => (short)v).ToArray();
        var into = new ushort[shorts.Length];
        TextureUploads.Normalized(shorts, into);
        var worst = shorts.Select((s, i) =>
            Math.Abs(into[i] - (int)Math.Round(Math.Clamp(s / 32767.0, 0, 1) * 65535, MidpointRounding.ToEven))).Max();
        Assert.Multiple(() =>
        {
            Assert.That(worst, Is.Zero);
            Assert.That(into[Array.IndexOf(shorts, (short)32767)], Is.EqualTo(65535));
            Assert.That(into[Array.IndexOf(shorts, (short)-5)], Is.Zero);
            for (var i = 0; i < shorts.Length; i++) Assert.That(into[i], Is.EqualTo(TextureUploads.Unorm(shorts[i])));
        });
    }

    [Test]
    public void BgraIsSwappedIntoRgba()
    {
        foreach (var n in new[] { 1, 7, 8, 13, 100 })
        {
            var bgra = Enumerable.Range(0, n).Select(i => (uint)(0x11223344 * (i + 1))).ToArray();
            var rgba = new uint[n];
            TextureUploads.Swapped(bgra, rgba);
            for (var i = 0; i < n; i++)
            {
                var p = bgra[i];
                Assert.That(rgba[i], Is.EqualTo((p & 0xFF00FF00) | ((p >> 16) & 0xFF) | ((p & 0xFF) << 16)), $"{n}: {i}");
            }
        }
    }

    [Test]
    public void OneChannelAndRgbFloatPairsAreTaken()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TextureUploads.Bytes(Vk.FormatR8, (0x1903, 0x1401)), Is.EqualTo((1, 1)));
            Assert.That(TextureUploads.Bytes(Vk.FormatRgba16F, (0x1907, 0x1406)), Is.EqualTo((12, 8)));
            Assert.That(TextureUploads.Bytes(Vk.FormatRgba8, (0x1908, 0x1401)), Is.EqualTo((4, 4)));
        });
    }

    [Test]
    public void DepthsAreNormalizedFloats()
    {
        uint[] ints = [0, uint.MaxValue, 1u << 31];
        ushort[] shorts = [0, ushort.MaxValue, 32768];
        var (wide, narrow) = (new float[3], new float[3]);
        TextureUploads.Depths(MemoryMarshal.AsBytes(ints.AsSpan()), wide, false);
        TextureUploads.Depths(MemoryMarshal.AsBytes(shorts.AsSpan()), narrow, true);
        float[] wanted = [0f, 1f, (float)((1u << 31) / 4294967295.0)], wantedShort = [0f, 1f, 32768 / 65535f];
        Assert.Multiple(() =>
        {
            Assert.That(wide, Is.EqualTo(wanted));
            Assert.That(narrow, Is.EqualTo(wantedShort));
        });
    }

    [Test]
    public unsafe void RgbFloatsBecomeOpaqueHalves()
    {
        float[] rgb = [0.5f, 1.5f, -2f, 3f, 4f, 5f];
        var into = new byte[2 * 4 * 2];
        fixed (float* source = rgb)
            TextureUploads.Convert(Vk.FormatRgba16F, (0x1907, 0x1406), (byte*)source, (2, 1, 24), into);
        var halves = MemoryMarshal.Cast<byte, Half>(into).ToArray();
        Half[] wanted = [(Half)0.5f, (Half)1.5f, (Half)(-2f), Half.One, (Half)3f, (Half)4f, (Half)5f, Half.One];
        Assert.That(halves, Is.EqualTo(wanted));
    }
}
