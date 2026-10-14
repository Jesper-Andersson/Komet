using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Common;

namespace Komet.Chunks;

// DecompressCombined (every chunk unpack) gets an exact-size zstd copy of the thread buffer (ByteToByte), reads it
// twice and drops it: 8 MB/s garbage while streaming. The rewrite passes the buffer and answers the length check with
// the decompressed size; safe, nothing decompresses on this thread before ByteToIntArrays copies.
internal static class DecompressScratch
{
    private const int MaxInstructions = 1024;

    [ThreadStatic] private static byte[]? _buffer;
    [ThreadStatic] private static int _length;
    private static long _saved;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static long Saved => Interlocked.Read(ref _saved);

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static MethodInfo? Target()
    {
        var method = AccessTools.Method(typeof(Compression), "DecompressCombined"); // internal static, one overload
        return NotNull(method) && Assert(method.ReturnType == typeof(int[])) ? method : null;
    }

    // Expects Decompress(byte[], int, int); dup; ldlen; conv.i4 once, then into ByteToIntArrays (Stop); else engine IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var decompress = AccessTools.Method(typeof(ICompression), nameof(ICompression.Decompress),
            [typeof(byte[]), typeof(int), typeof(int)]);
        var consume = AccessTools.Method(typeof(ArrayConvert), nameof(ArrayConvert.ByteToIntArrays));
        var (mine, length) = (AccessTools.Method(typeof(DecompressScratch), nameof(Decompress)),
            AccessTools.Method(typeof(DecompressScratch), nameof(Length)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(decompress) ||
            !NotNull(consume) || !NotNull(mine) || !NotNull(length)) return code;
        var at = Il.Single(code, c => c.Calls(decompress));
        if (!Assert(at >= 0) || !Index(at + 3, code.Count) || !Assert(code[at].opcode == OpCodes.Callvirt) ||
            code[at + 1].opcode != OpCodes.Dup || code[at + 2].opcode != OpCodes.Ldlen ||
            code[at + 3].opcode != OpCodes.Conv_I4) return code;
        var stop = Stop(code, at + 4, consume);
        // Running off the body's end is logged, stopping is not
        if (!Index(stop, code.Count) || !code[stop].Calls(consume)) return code;
        // Length first (alone it answers any ldlen); conv.i4 after it is a no-op on an int
        Rewritten = Assert(Il.Substitute(code, at + 2, length)) && Assert(Il.Substitute(code, at, mine));
        return code;
    }

    // Walk stops at ByteToIntArrays, any call but new InvalidDataException(), or what could keep the array (store, dup,
    // return): the buffer is overwritten by the next decompression. code.Count if the body ends.
    private static int Stop(List<CodeInstruction> code, int from, MethodInfo consume)
    {
        if (!NotNull(consume) || !Assert(from > 0)) return code.Count;
        for (var i = from; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var op = code[i].opcode;
            var throws = op == OpCodes.Newobj && code[i].operand is ConstructorInfo { DeclaringType: var type } &&
                         type == typeof(InvalidDataException);
            if (code[i].Calls(consume) || (op.FlowControl == FlowControl.Call && !throws) || op == OpCodes.Dup ||
                op.FlowControl == FlowControl.Return || op.Name?.StartsWith("st", StringComparison.Ordinal) != false)
                return i;
        }

        return code.Count;
    }

    // zstd only (DecompressAndSize plus a copy); other codecs and bad sizes take the engine's call
    internal static byte[] Decompress(ICompression compression, byte[] data, int offset, int length)
    {
        if (!Enabled || !NotNull(compression) || compression.GetType() != typeof(CompressionZSTD))
            return compression.Decompress(data, offset, length);
        var size = compression.DecompressAndSize(data, offset, length, out var buffer);
        // What CompressionZSTD.Decompress returns on a zstd error; the length check then throws
        if (size < 0) return [];
        if (!NotNull(buffer) || !Assert(size <= buffer.Length)) return compression.Decompress(data, offset, length);
        (_buffer, _length) = (buffer, size);
        if (Counting.Hud) _ = Interlocked.Add(ref _saved, size);
        return buffer;
    }

    // Replaces the length check's ldlen: decompressed size for the buffer handed out above, else the array's length
    internal static int Length(byte[] array)
    {
        if (!NotNull(array) || !ReferenceEquals(array, _buffer)) return array.Length; // null throws here as ldlen would
        _buffer = null; // one answer per decompression, and zstd may swap its buffer on a later call
        return Assert(_length >= 0) ? _length : array.Length;
    }
}
