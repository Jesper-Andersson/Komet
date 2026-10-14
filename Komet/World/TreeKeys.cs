using System.Collections.Concurrent;
using System.Text;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Datastructures;

namespace Komet.World;

// TreeAttribute.FromBytes reads each key as a new string only to intern it (~100 MB in a join's first minute). Decoded
// on the stack and looked up among seen names instead; same format as BinaryReader.ReadString. GetString interns every
// read value (EntityPlayer.PlayerUID: dozens a frame, 3 % of the main thread at a join): the same string object answers
// from a per-thread cache by identity instead, exact as interning never changes its answer.
internal static class TreeKeys
{
    private const int MaxInstructions = 256, MaxKey = 256, MaxKeys = 1 << 16;
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> ByChars =
        Known.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }
    public static bool Reads { get; private set; } // GetString's intern cached

    private const int Slots = 64;
    [ThreadStatic] private static (string? From, string To)[]? _interned;

    public static void Install(Harmony harmony)
    {
        var from = AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes), [typeof(BinaryReader)]);
        Rewritten = false;
        if (!NotNull(harmony) || !NotNull(from)) return;
        _ = NotNull(harmony.Patch(from, transpiler: new HarmonyMethod(Rewrite)));
        var get = AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.GetString), [typeof(string), typeof(string)]);
        Reads = false;
        if (get is not null) _ = NotNull(harmony.Patch(get, transpiler: new HarmonyMethod(RewriteReads)));
    }

    // The one string.Intern of GetString, else the engine's IL
    internal static List<CodeInstruction> RewriteReads(IEnumerable<CodeInstruction> instructions)
    {
        var (intern, mine) = (AccessTools.Method(typeof(string), nameof(string.Intern), [typeof(string)]),
            AccessTools.Method(typeof(TreeKeys), nameof(Interned)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(intern) || !NotNull(mine)) return code;
        Reads = Il.SubstituteSingle(code, c => c.Calls(intern), mine);
        return code;
    }

    // string.Intern(text), the last answers for these very objects kept per thread
    internal static string Interned(string text)
    {
        if (!Enabled) return string.Intern(text);
        if (!NotNull(text)) return text;
        var cache = _interned ??= new (string?, string)[Slots];
        ref var slot = ref cache[System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(text) & (Slots - 1)];
        if (ReferenceEquals(slot.From, text)) return slot.To;
        slot = (text, string.Intern(text));
        return Assert(slot.To.Length == text.Length) ? slot.To : text;
    }

    // Exactly one ReadString, the one string.Intern takes, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (read, intern, mine) = (AccessTools.Method(typeof(BinaryReader), nameof(BinaryReader.ReadString), []),
            AccessTools.Method(typeof(string), nameof(string.Intern), [typeof(string)]), AccessTools.Method(typeof(TreeKeys), nameof(Key)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(read) || !NotNull(intern) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.Calls(read));
        Rewritten = Assert(at >= 0) && Index(at + 1, code.Count) && code[at + 1].Calls(intern) && Il.Substitute(code, at, mine);
        return code;
    }

    // Replaces reader.ReadString(); string.Intern then finds it interned
    internal static string Key(BinaryReader reader)
    {
        if (!Enabled) return reader.ReadString();
        var length = reader.Read7BitEncodedInt();
        if (length is < 0 or > MaxKey) return Long(reader, length);
        Span<byte> bytes = stackalloc byte[length];
        var got = 0;
        for (var i = 0; i < MaxKey && got < length; i++) // each read brings at least one byte
        {
            var read = reader.Read(bytes[got..]);
            if (read <= 0) throw new EndOfStreamException();
            got += read;
        }

        Span<char> chars = stackalloc char[length]; // UTF-8 never decodes to more chars than bytes
        var count = Encoding.UTF8.GetChars(bytes, chars);
        _ = Assert(count <= length);
        if (ByChars.TryGetValue(chars[..count], out var known)) return known;
        var key = string.Intern(new string(chars[..count]));
        if (Known.Count < MaxKeys) _ = Known.TryAdd(key, key);
        return key;
    }

    private static string Long(BinaryReader reader, int length)
    {
        if (!Assert(length >= 0)) throw new FormatException("negative string length");
        return Encoding.UTF8.GetString(reader.ReadBytes(length) is { } bytes && bytes.Length == length
            ? bytes
            : throw new EndOfStreamException());
    }
}
