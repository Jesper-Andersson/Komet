using Komet.World;

namespace Komet.Test.World;

// The text is the engine's Ascii85.Encode for every length and for runs of zeros (the 'z' groups)
public sealed class Ascii85TextTests
{
    [Test]
    public void TheTextIsTheEngines()
    {
        var random = new Random(3);
        for (var length = 0; length < 300; length++)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            for (var i = 0; i < length; i++)
                if (random.Next(3) == 0)
                    bytes[i] = 0;
            Assert.That(Ascii85Text.Of(bytes), Is.EqualTo(Ascii85.Encode(bytes)), $"length {length}");
        }
    }

    // Decoding every encoding the engine makes gives the bytes back, as the engine's decoder does; broken text throws the same
    [Test]
    public void TheBytesAreTheEngines()
    {
        var random = new Random(5);
        for (var length = 0; length < 300; length++)
        {
            var bytes = new byte[length];
            random.NextBytes(bytes);
            for (var i = 0; i < length; i++)
                if (random.Next(3) == 0)
                    bytes[i] = 0;
            var text = Ascii85.Encode(bytes);
            Assert.That(Ascii85Text.Bytes(text), Is.EqualTo(Ascii85.Decode(text)), $"length {length}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(() => Ascii85Text.Bytes("abc~"), Throws.TypeOf<FormatException>());
            Assert.That(() => Ascii85Text.Bytes("abcdeA"), Throws.TypeOf<FormatException>(), "a final block of one character");
            Assert.That(() => Ascii85Text.Bytes("uuuuu"), Throws.TypeOf<FormatException>(), "over uint.MaxValue");
        });
    }
}
