using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.World;

// BlockSchematic.TransformWhilePacked re-encodes placed structures' block entities via Ascii85.Encode (StringBuilder,
// new char[5] per 4 bytes): the server's top allocation on join (~150 MB in 50 s). Here the length comes first and the
// string is written once (string.Create).
internal static class Ascii85Text
{
    private const int Group = 4, Digits = 5, Base = 85, First = 33, MaxGroups = int.MaxValue / Digits - 1, MaxChars = int.MaxValue / 4;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    public static void Install(Harmony harmony)
    {
        Patched = false;
        var target = AccessTools.DeclaredMethod(typeof(Ascii85), nameof(Ascii85.Encode), [typeof(byte[])]);
        if (!NotNull(harmony) || !NotNull(target) || !Assert(target.ReturnType == typeof(string))) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Encode)));
        var decode = AccessTools.DeclaredMethod(typeof(Ascii85), nameof(Ascii85.Decode), [typeof(string)]);
        if (NotNull(decode)) _ = NotNull(harmony.Patch(decode, new HarmonyMethod(Decode)));
    }

    // Ascii85.Decode grows a MemoryStream and copies it out (structures' block entities as chunks generate: 36 MB in 40 s
    // of flight); the length is counted first and the bytes are written once. The same exceptions for the same input.
    private static bool Decode(string? encoded, ref byte[] __result)
    {
        if (!Enabled || encoded is null || encoded.Length > int.MaxValue / 4) return true;
        __result = Bytes(encoded);
        return !Assert(__result.Length <= encoded.Length);
    }

    // Measured has checked every character: the digits add up unchecked in 64 bits, a group checked once at its end
    internal static byte[] Bytes(string encoded)
    {
        var bytes = new byte[Measured(encoded)];
        var (at, count, value) = (0, 0, 0ul);
        for (var i = 0; i < Math.Min(encoded.Length, MaxChars); i++)
        {
            var c = encoded[i];
            if (c == 'z' && count == 0)
            {
                at += Unpacked(bytes.AsSpan(at), 0, 0);
                continue;
            }

            value += Powers[count++] * (ulong)(c - First);
            if (count < Digits) continue;
            at += Unpacked(bytes.AsSpan(at), Narrowed(value), 0);
            (count, value) = (0, 0ul);
        }

        if (count > 1)
        {
            for (var j = count; j < Digits; j++) value += (Base - 1) * (ulong)Powers[j];
            at += Unpacked(bytes.AsSpan(at), Narrowed(value), Digits - count);
        }

        return Assert(at == bytes.Length) ? bytes : bytes[..at];
    }

    // How many bytes the text decodes to, with the engine's checks on it
    private static int Measured(string encoded)
    {
        var (length, count) = (0, 0);
        for (var i = 0; i < Math.Min(encoded.Length, MaxChars); i++)
        {
            var c = encoded[i];
            if (c == 'z' && count == 0)
            {
                length += Group;
                continue;
            }

            if (c is < '!' or > 'u') throw new FormatException("Invalid character '" + c + "' in Ascii85 block.");
            if (++count < Digits) continue;
            length += Group;
            count = 0;
        }

        if (count == 1) throw new FormatException("The final Ascii85 block must contain more than one character.");
        var tail = count > 1 ? count - 1 : 0;
        return Assert(length >= 0) ? length + tail : 0;
    }

    // A group's value as the engine's checked sum would have it (at most five digits of 84: below 2^33)
    private static uint Narrowed(ulong value)
    {
        if (value > uint.MaxValue)
            throw new FormatException("The current group of characters decodes to a value greater than UInt32.MaxValue.");
        _ = Assert(value < 1ul << 33) && Assert(Powers.Length == Digits);
        return (uint)value;
    }

    private static readonly uint[] Powers = [52200625u, 614125u, 7225u, 85u, 1u];

    // The value's bytes, big-endian, fewer by paddingChars
    private static int Unpacked(Span<byte> into, uint value, int paddingChars)
    {
        var count = Group - paddingChars;
        if (!Assert(paddingChars is >= 0 and < Group) || into.Length < count) return 0;
        for (var i = 0; i < Math.Min(count, Group); i++) into[i] = (byte)(value >> (24 - 8 * i));
        return count;
    }

    // A null array is left to the engine, which throws
    private static bool Encode(byte[]? bytes, ref string __result)
    {
        if (!Enabled || bytes is null || bytes.Length / Group >= MaxGroups) return true;
        __result = Of(bytes);
        return !Assert(__result.Length <= (bytes.Length / Group + 1) * Digits);
    }

    // Ascii85.Encode: 4 bytes big-endian as 5 digits from '!', 'z' if zero; an n-byte tail zero-padded, n + 1 digits
    internal static string Of(byte[] bytes)
    {
        var (full, tail) = (bytes.Length / Group, bytes.Length % Group);
        var length = tail == 0 ? 0 : tail + 1;
        for (var g = 0; g < Math.Min(full, MaxGroups); g++) length += Value(bytes, g * Group, Group) == 0 ? 1 : Digits;
        if (length == 0) return string.Empty;
        _ = Assert(length <= (full + 1) * Digits);
        return string.Create(length, bytes, static (chars, source) =>
        {
            var (groups, rest) = (source.Length / Group, source.Length % Group);
            var at = 0;
            for (var g = 0; g < Math.Min(groups, MaxGroups); g++)
            {
                var value = Value(source, g * Group, Group);
                if (value == 0) chars[at++] = 'z';
                else at += Write(chars[at..], value, Digits);
            }

            if (rest > 0) at += Write(chars[at..], Value(source, groups * Group, rest), rest + 1);
            _ = Assert(at == chars.Length);
        });
    }

    // count bytes from start, big-endian, the missing ones zero
    private static uint Value(byte[] bytes, int start, int count)
    {
        uint value = 0;
        _ = Assert(count is > 0 and <= Group) && Assert(start >= 0);
        for (var i = 0; i < Group; i++)
            if (i < count)
                value |= (uint)bytes[start + i] << (24 - i * 8);
        return value;
    }

    // The first `keep` of the value's five digits, most significant first
    private static int Write(Span<char> chars, uint value, int keep)
    {
        Span<char> digits = stackalloc char[Digits];
        for (var k = 0; k < Digits; k++)
        {
            digits[Digits - 1 - k] = (char)(value % Base + First);
            value /= Base;
        }

        digits[..keep].CopyTo(chars);
        return Assert(keep is > 0 and <= Digits) ? keep : 0;
    }
}
