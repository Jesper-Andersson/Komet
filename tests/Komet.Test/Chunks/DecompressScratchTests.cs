using Komet.Chunks;

namespace Komet.Test.Chunks;

// Golden test against the engine: layers compressed by Compression.CompressAndCombine, as ChunkDataLayer does, and unpacked by the
// engine's own DecompressCombined once as shipped and once rewritten. Palettes, slices and palette counts must match, and data that the
// engine rejects must still be rejected - also when the thread's zstd buffer is far longer than what was decompressed into it.
public sealed class DecompressScratchTests
{
    private const int Calls = 2000, Passes = 3;
    private static readonly int[] MeasuredPalettes = [4, 16, 64];

    private static readonly Combined Unpack =
        AccessTools.Method(typeof(Compression), "DecompressCombined").CreateDelegate<Combined>();

    [TearDown]
    public void Reset() => DecompressScratch.Enabled = true;

    // A palette of `count` entries in an array rounded up to a power of two, and one random 1024-int plane per palette bit
    private static byte[] Layer(int seed, int count)
    {
        var r = new Random(seed);
        var length = 1;
        for (var i = 0; i < 16 && length < count; i++) length <<= 1;
        var bits = length <= 1 ? 1 : (int)Math.Log2(length);
        var palette = new int[length];
        for (var i = 0; i < count; i++) palette[i] = r.Next();
        var planes = new int[bits * 1024];
        for (var i = 0; i < planes.Length; i++) planes[i] = r.Next();
        return Compression.CompressAndCombine(planes, palette, count);
    }

    private static Unpacked Run(byte[] data)
    {
        int[][]? blocks = null;
        var count = 0;
        var palette = Unpack(data, ref blocks, ref count, () => new int[1024]);
        return new Unpacked(palette, count, blocks);
    }

    private static string? Difference(Unpacked engine, Unpacked mine)
    {
        var bits = (int)Math.Log2(engine.Palette!.Length);
        if (!mine.Palette!.SequenceEqual(engine.Palette)) return "palette";
        if (mine.Count != engine.Count) return "palette count";
        if (mine.Slices!.Length < bits ||
            !Enumerable.Range(0, bits).All(b => mine.Slices[b].SequenceEqual(engine.Slices![b]))) return "slices";
        return mine.Slices.Skip(bits).All(slice => slice is null) ? null : "slices past the planes";
    }

    private static TestHarmony Patched()
    {
        var harmony = new TestHarmony("komet-test-decompressscratch");
        DecompressScratch.Install(harmony);
        Assert.That(DecompressScratch.Rewritten, Is.True, "DecompressCombined no longer has the expected shape");
        return harmony;
    }

    [Test]
    public void UnpacksExactlyWhatTheEngineUnpacks()
    {
        // raw palettes up to 18 entries, zstd-compressed ones above, one to six planes
        int[] counts = [2, 3, 4, 7, 8, 16, 17, 18, 19, 32, 33, 64];
        var layers = counts.SelectMany((count, i) => new[] { Layer(i, count), Layer(100 + i, count) }).ToList();
        layers.Add(Layer(1000, 64)); // leaves a large zstd buffer behind for the small layers after it
        layers.AddRange(counts.Select((count, i) => Layer(200 + i, count)));
        var engine = layers.Select(Run).ToList();
        using var harmony = Patched();
        var mine = layers.Select(Run).ToList();
        Assert.That(engine.Zip(mine, Difference), Is.All.Null);
    }

    // Three palette entries round up to four, two planes, but CompressAndCombine only packed floor(log2(3)) = one: the engine throws
    // InvalidDataException because 4096 decompressed bytes are fewer than two planes. The rewrite has to measure the decompressed
    // size, not the zstd buffer, which a previous 64-entry layer left at 24 KiB.
    [Test]
    public void ShortAndCorruptDataIsStillRejected()
    {
        var planes = new int[1024];
        var shortLayer = Compression.CompressAndCombine(planes, [1, 2, 3], 3);
        var full = Layer(7, 8);
        var truncated = full[..^8];
        var engineShort = Assert.Catch(() => Run(shortLayer));
        var engineTruncated = Assert.Catch(() => Run(truncated));
        using var harmony = Patched();
        _ = Run(Layer(8, 64));
        Assert.Multiple(() =>
        {
            Assert.That(engineShort, Is.TypeOf<InvalidDataException>());
            Assert.That(Assert.Catch(() => Run(shortLayer)), Is.TypeOf(engineShort!.GetType()));
            Assert.That(Assert.Catch(() => Run(truncated)), Is.TypeOf(engineTruncated!.GetType()));
            Assert.That(Run(full).Palette, Is.Not.Null, "the buffer is handed back for the next layer");
        });
    }

    // The slices are preallocated so that only what DecompressCombined itself allocates is counted: the palette and, before, the copy.
    // Same process: the engine as shipped, then patched with the rewrite on and off in turn, the least of the passes read.
    [Test]
    public void GarbagePerLayerBeforeAndAfter()
    {
        var layers = MeasuredPalettes.Select(count => Layer(count, count)).ToArray();
        var blocks = Enumerable.Range(0, 15).Select(_ => new int[1024]).ToArray();

        long Allocated(byte[] data)
        {
            var slices = blocks;
            var count = 0;
            for (var i = 0; i < 50; i++) _ = Unpack(data, ref slices, ref count, null);
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Calls; i++) _ = Unpack(data, ref slices, ref count, null);
            return (GC.GetAllocatedBytesForCurrentThread() - start) / Calls;
        }

        // The least of several passes: a one-off allocation of the runtime's (tiering, the thread's zstd buffer growing) only adds bytes
        long[] Least(long[] least, long[] pass) => least.Length == 0 ? pass : [.. least.Zip(pass, Math.Min)];

        long[] shipped = [];
        for (var pass = 0; pass < Passes; pass++) shipped = Least(shipped, [.. layers.Select(Allocated)]);
        using var harmony = Patched();
        long[] off = [], on = [];
        for (var pass = 0; pass < Passes; pass++)
        {
            DecompressScratch.Enabled = false;
            off = Least(off, [.. layers.Select(Allocated)]);
            DecompressScratch.Enabled = true;
            on = Least(on, [.. layers.Select(Allocated)]);
        }

        Assert.Multiple(() =>
        {
            Assert.That(off, Is.EqualTo(shipped),
                "switched off, the rewrite has to allocate exactly what the engine does");
            for (var i = 0; i < layers.Length; i++)
                Assert.That(shipped[i] - on[i], Is.GreaterThanOrEqualTo(4096L * (i + 1) * 2), "the plane copy is gone");
            Assert.That(on, Is.All.LessThanOrEqualTo(24 + 4 * 64), "only the palette array is left");
        });
    }

    // Several threads unpack at once, as the tesselator, the network thread and the server's chunk thread do: each hands out its own zstd
    // buffer and answers its own length check, so every thread unpacks what the engine unpacks and still rejects the short layer
    [Test]
    public void ThreadsUnpackTheirOwnLayers()
    {
        int[] counts = [3, 8, 19, 64];
        var layers = counts.Select((count, i) => Layer(300 + i, count)).ToList();
        var shortLayer = Compression.CompressAndCombine(new int[1024], [1, 2, 3], 3);
        var engine = layers.Select(Run).ToList();
        using var harmony = Patched();
        var failures = Threads.Run(4, t =>
        {
            for (var round = 0; round < 300; round++)
            {
                var i = (round + t) % layers.Count;
                if (Difference(engine[i], Run(layers[i])) is { } what)
                    return $"the {what} of layer {i} unpacked differently in round {round}";
                if (round % 7 == 0 && !Throws(shortLayer)) return $"the short layer passed in round {round}";
            }

            return null;
        });
        Assert.That(failures, Is.All.Null);
    }

    private static bool Throws(byte[] layer)
    {
        try
        {
            _ = Run(layer);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    // The buffer kept past the copy - stored away, or duplicated for later - would outlive the next decompression on this thread, which
    // overwrites it; the engine's copy allowed that, the thread's buffer does not, so such a shape stays the engine's. So does every
    // other shape, such as a length check without its dup or one that ByteToIntArrays never follows.
    [Test]
    public void AnotherShapeIsLeftAlone()
    {
        var original = PatchProcessor.GetOriginalInstructions(DecompressScratch.Target()!);
        var decompress = original.FindIndex(code =>
            code.opcode == OpCodes.Callvirt && code.operand is MethodInfo { Name: "Decompress" });
        // the length check passed
        var passed = original.FindIndex(decompress + 1, code => code.opcode == OpCodes.Throw) + 1;
        Assert.Multiple(() =>
        {
            Assert.That(DecompressScratch.Rewrite(original).Count(IsScratch), Is.EqualTo(2),
                "the engine's own shape is rewritten");
            Assert.That(decompress, Is.GreaterThan(0));
            Assert.That(passed, Is.GreaterThan(decompress + 4));
        });

        // Where the length check branches to, with only the buffer on the stack; the branch's label moves to the first new instruction
        List<CodeInstruction> Kept(params CodeInstruction[] keep)
        {
            var body = PatchProcessor.GetOriginalInstructions(DecompressScratch.Target()!);
            body.InsertRange(passed, keep);
            _ = keep[0].MoveLabelsFrom(body[passed + keep.Length]);
            return body;
        }

        // any static byte[] will do, the IL is only inspected
        var field = AccessTools.Field(typeof(CompressionZSTD), "reusableBuffer2");
        var call = AccessTools.Method(typeof(ICompression), nameof(ICompression.Decompress),
            [typeof(byte[]), typeof(int), typeof(int)]);
        List<CodeInstruction>[] shapes =
        [
            Kept(new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Stsfld, field)),
            Kept(new CodeInstruction(OpCodes.Stsfld, field), new CodeInstruction(OpCodes.Ldsfld, field)),
            [
                new CodeInstruction(OpCodes.Ldnull), new CodeInstruction(OpCodes.Ldnull),
                new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Callvirt, call),
                new CodeInstruction(OpCodes.Ldlen), new CodeInstruction(OpCodes.Conv_I4),
                new CodeInstruction(OpCodes.Pop), new CodeInstruction(OpCodes.Ret)
            ],
            [
                new CodeInstruction(OpCodes.Ldnull), new CodeInstruction(OpCodes.Ldnull),
                new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ldc_I4_0),
                new CodeInstruction(OpCodes.Callvirt, call),
                new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldlen),
                new CodeInstruction(OpCodes.Conv_I4), new CodeInstruction(OpCodes.Pop),
                new CodeInstruction(OpCodes.Throw) // no ByteToIntArrays
            ]
        ];
        Assert.That(field, Is.Not.Null);
        foreach (var shape in shapes)
        {
            var opcodes = shape.Select(code => code.opcode).ToList();
            var result = DecompressScratch.Rewrite(shape);
            Assert.Multiple(() =>
            {
                Assert.That(result.Count(IsScratch), Is.Zero);
                Assert.That(result.Select(code => code.opcode), Is.EqualTo(opcodes));
                Assert.That(DecompressScratch.Rewritten, Is.False);
            });
        }
    }

    private static bool IsScratch(CodeInstruction code) =>
        code.operand is MethodInfo { DeclaringType: var t } && t == typeof(DecompressScratch);

    private sealed record Unpacked(int[]? Palette, int Count, int[][]? Slices);

    private delegate int[]? Combined(byte[] data, ref int[][]? blocks, ref int refCount, Func<int[]>? newArray);
}
