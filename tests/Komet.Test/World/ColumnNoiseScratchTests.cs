using System.Collections.Concurrent;
using Komet.Core;
using Komet.World;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;
using ColumnNoise = Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise.ColumnNoise;

namespace Komet.Test.World;

// Golden tests against the engine's own terrain noise. Random noises of 1 to 14 octaves with amplitudes and thresholds that let anything
// from none to all octaves contribute, evaluated as GenTerra evaluates them - the bounds, then NoiseSign upwards through the column, whose
// cache of the previous evaluation makes the order part of the result - and Noise. Every value must be bit for bit what the shipped
// constructor gives: from the thread's arrays inside Column, from new ones outside it, on many threads at once, and in GenTerra's own
// OnChunkColumnGen, which runs the rewritten Parallel.For body over real landforms and records every block it writes.
public sealed class ColumnNoiseScratchTests
{
    private const int Inputs = 600, Heights = 254, Short = 48, Calls = 20000, Passes = 3, MapSizeY = 256,
        RegionSize = 512, Threads = 8;

    private static readonly FieldInfo Entries = AccessTools.Field(typeof(ColumnNoise), "orderedOctaveEntries");
    private static readonly FieldInfo Past = AccessTools.Field(typeof(ColumnNoise), "pastEvaluations");
    private static readonly FieldInfo Armed = AccessTools.Field(typeof(ColumnNoiseScratch), "_armed");
    private static readonly MethodInfo ColumnGen = AccessTools.Method(typeof(GenTerra), "OnChunkColumnGen");
    private static int _seen;

    [TearDown]
    public void Reset()
    {
        ColumnNoiseScratch.Enabled = true;
        Counting.Hud = false;
    }

    private static TestHarmony Patched(ILogger? logger = null)
    {
        _ = typeof(GenTerra).Assembly; // VSEssentials is found by name, so it has to be loaded first
        var harmony = new TestHarmony("komet-test-columnnoise");
        ColumnNoiseScratch.Install(harmony, logger);
        Assert.That(ColumnNoiseScratch.Rewritten, Is.True,
            "ForColumn, the ColumnNoise constructor or GenTerra's column body changed shape");
        return harmony;
    }

    // GenTerra has 9 octaves at the default world height and one more per 128 blocks above it; a threshold at or above an amplitude
    // silences that octave for the column, and the thresholders NoiseSign gets lie mostly within the column's bound, sometimes beyond
    private static Input Make(Random r)
    {
        var octaves = r.Next(1, 15);
        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(octaves, 0.0003 * (0.2 + r.NextDouble() * 5),
            0.5 + r.NextDouble() / 2, r.NextInt64());
        var amplitudes = new double[octaves];
        var thresholds = new double[octaves];
        var bound = 0.0;
        for (var i = 0; i < octaves; i++)
        {
            amplitudes[i] = r.Next(6) == 0 ? 0 : r.NextDouble() * 2.4 - 1.2;
            thresholds[i] = r.Next(3) == 0 ? 0 : r.NextDouble() * 0.9;
            bound += Math.Max(0, Math.Abs(amplitudes[i]) - thresholds[i]) * 1.2;
        }

        var thresholders = Enumerable.Range(0, 64).Select(_ =>
                r.Next(8) == 0 ? (r.NextDouble() - 0.5) * 8 * (bound + 1) : (r.NextDouble() - 0.5) * 2 * bound)
            .ToArray();
        var reach = r.Next(4) == 0 ? 2e7 : 2e5;
        return new Input(noise, 0.25 * (0.5 + r.NextDouble()), amplitudes, thresholds, (r.NextDouble() - 0.5) * reach,
            (r.NextDouble() - 0.5) * reach, thresholders);
    }

    private static Input Contributing(Random r) =>
        Enumerable.Range(0, 64).Select(_ => Make(r)).First(input => EntriesOf(input.Engine()).Length > 0);

    private static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

    private static long[] Trace(ColumnNoise column, Input input, int heights = Heights)
    {
        var trace = new List<long>(heights + 32)
            { Bits(column.BoundMin), Bits(column.BoundMax), Bits(column.UncurvedBound) };
        for (var y = 1; y <= heights; y++)
            trace.Add(Bits(column.NoiseSign(y, input.Thresholders[y % input.Thresholders.Length])));
        for (var y = 0; y < heights; y += 17) trace.Add(Bits(column.Noise(y)));
        return [.. trace];
    }

    private static Array EntriesOf(ColumnNoise column) => (Array)Entries.GetValue(column)!;

    private static Array PastOf(ColumnNoise column) => (Array)Past.GetValue(column)!;

    [Test]
    public void ColumnsAreBitIdenticalToTheEngine()
    {
        var r = new Random(11);
        var inputs = Enumerable.Range(0, Inputs).Select(_ => Make(r)).ToList();
        var engine = inputs.Select(input => Trace(input.Engine(), input)).ToList();
        var contributing = inputs.Select(input => EntriesOf(input.Engine()).Length).ToList();
        using var harmony = Patched();
        Counting.Hud = true;
        var start = ColumnNoiseScratch.Saved;
        var scratch = inputs.Select(input => Trace(input.Scratch(), input)).ToList();
        var saved = ColumnNoiseScratch.Saved - start;
        var plain = inputs.Select(input => Trace(input.Engine(), input)).ToList();
        ColumnNoiseScratch.Enabled = false;
        var off = inputs.Select(input => Trace(input.Scratch(), input)).ToList();
        ColumnNoiseScratch.Enabled = true;
        var lengths = inputs.Select(input => EntriesOf(input.Scratch()).Length).ToList();
        Assert.Multiple(() =>
        {
            for (var i = 0; i < Inputs; i++)
            {
                Assert.That(scratch[i], Is.EqualTo(engine[i]), $"column {i} from the thread's arrays");
                Assert.That(plain[i], Is.EqualTo(engine[i]), $"column {i} built outside Column");
                Assert.That(off[i], Is.EqualTo(engine[i]), $"column {i} with the rewrite switched off");
            }

            Assert.That(lengths, Is.EqualTo(contributing), "every array exactly as long as the engine's");
            Assert.That(contributing.Distinct().Count(), Is.GreaterThanOrEqualTo(12),
                "the contributing octaves have to vary");
            Assert.That(contributing, Has.Some.EqualTo(0), "a column no octave contributes to");
            Assert.That(saved, Is.GreaterThan(Inputs * 100L), "the columns were built from the thread's arrays");
        });
    }

    // Column hands out the same arrays for the same lengths, zeroed, and only to the one column it builds: a ColumnNoise built without it
    // - a mod may keep several - owns new arrays, keeps them across any number of scratch columns and still evaluates as the engine's
    [Test]
    public void OnlyTheArmedColumnUsesTheThreadsArrays()
    {
        var r = new Random(12);
        var inputs = Enumerable.Range(0, 40).Select(_ => Make(r)).Where(input => EntriesOf(input.Engine()).Length > 0)
            .Take(8).ToList();
        var engine = inputs.Select(input => Trace(input.Engine(), input)).ToList();
        using var harmony = Patched();
        var kept = inputs.Select(input => input.Engine()).ToList();
        var first = inputs[0].Scratch();
        var (entries, past) = (EntriesOf(first), PastOf(first));
        _ = Trace(first, inputs[0]); // leaves evaluations in the arrays for the next column to find
        var second = inputs[0].Scratch();
        // before the next Column, as the body evaluates its column before the next body starts
        var reused = Trace(second, inputs[0]);
        var fresh = Enumerable.Range(0, 200).Select(_ => Make(r))
            .Count(input => Trace(input.Scratch(), input).Length > 0);
        Assert.Multiple(() =>
        {
            Assert.That(EntriesOf(second), Is.SameAs(entries), "the thread's array of that length is handed out again");
            Assert.That(PastOf(second), Is.SameAs(past));
            Assert.That(reused, Is.EqualTo(engine[0]), "the reused arrays start as new ones would");
            Assert.That(fresh, Is.EqualTo(200));
            for (var i = 0; i < kept.Count; i++)
            {
                Assert.That(EntriesOf(kept[i]), Is.Not.SameAs(entries), $"kept column {i} owns its arrays");
                Assert.That(Trace(kept[i], inputs[i]), Is.EqualTo(engine[i]),
                    $"kept column {i} after the scratch columns");
            }
        });
    }

    // The arm is for one constructor: the first that starts on the armed thread takes the arrays and disarms it, so a second column
    // built before Column's finally - by a patch in between, say - owns new arrays
    [Test]
    public void TheFirstConstructorTakesTheArm()
    {
        var input = Contributing(new Random(19));
        var engine = Trace(input.Engine(), input);
        var harmony = Patched();
        try
        {
            var threads = EntriesOf(input.Scratch());
            Armed.SetValue(null, true); // as Column leaves the thread for the constructor ForColumn runs
            var (first, second) = (input.Engine(), input.Engine());
            var after = (bool)Armed.GetValue(null)!;
            Assert.Multiple(() =>
            {
                Assert.That(EntriesOf(first), Is.SameAs(threads), "the first constructor takes the arm");
                Assert.That(EntriesOf(second), Is.Not.SameAs(threads), "the second one finds the thread disarmed");
                Assert.That(after, Is.False);
                Assert.That(Trace(second, input), Is.EqualTo(engine));
            });
        }
        finally
        {
            Armed.SetValue(null, false);
            harmony.UnpatchAll(harmony.Id);
        }
    }

    // The engine's exceptions stay the engine's - a null noise, amplitudes shorter than the octaves - and neither leaves the thread
    // armed for the next column built outside Column. A column past the table's 64 octaves gets new arrays and the engine's values.
    [Test]
    public void FailuresAndOversizedColumnsBehaveAsTheEngines()
    {
        var r = new Random(16);
        var input = Contributing(r);
        var engine = Trace(input.Engine(), input);
        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(9, 0.0003, 0.9, 5);
        double[] amplitudes = [0.5, 0.5], thresholds = [0, 0];
        var huge = input with
        {
            Noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(70, 0.0000003, 0.9, 6),
            Amplitudes = [.. Enumerable.Repeat(0.5, 70)],
            Thresholds = [.. Enumerable.Repeat(0.1, 70)]
        };
        var engineHuge = Trace(huge.Engine(), huge);
        Assert.That(() => noise.ForColumn(0.25, amplitudes, thresholds, 1, 2),
            Throws.TypeOf<IndexOutOfRangeException>(), "the engine");
        using var harmony = Patched();
        Assert.That(() => ColumnNoiseScratch.Column(null!, 0.25, input.Amplitudes, input.Thresholds, 1, 2),
            Throws.TypeOf<NullReferenceException>());
        var afterNull = input.Engine();
        Assert.That(() => ColumnNoiseScratch.Column(noise, 0.25, amplitudes, thresholds, 1, 2),
            Throws.TypeOf<IndexOutOfRangeException>());
        var afterThrow = input.Engine();
        var scratch = input.Scratch();
        var (hugeFirst, hugeSecond) = (huge.Scratch(), huge.Scratch());
        Assert.Multiple(() =>
        {
            Assert.That(EntriesOf(afterNull), Is.Not.SameAs(EntriesOf(scratch)),
                "the null noise left the thread armed");
            Assert.That(EntriesOf(afterThrow), Is.Not.SameAs(EntriesOf(scratch)),
                "the constructor's throw left the thread armed");
            Assert.That(Trace(scratch, input), Is.EqualTo(engine));
            Assert.That(EntriesOf(hugeFirst), Has.Length.EqualTo(70));
            Assert.That(EntriesOf(hugeSecond), Is.Not.SameAs(EntriesOf(hugeFirst)), "past the table: new arrays");
            Assert.That(Trace(hugeFirst, huge), Is.EqualTo(engineHuge));
        });
    }

    // Many threads at once, as Parallel.For runs GenTerra's body, the rewrite on and off in turn. Each column's arrays are registered
    // while the column is evaluated: a second column holding them before the first is done would be arrays shared while both are live.
    [Test]
    public void ParallelColumnsMatchTheEngine()
    {
        var r = new Random(13);
        var inputs = Enumerable.Range(0, 4096).Select(_ => Make(r)).ToArray();
        var engine = inputs.Select(input => Trace(input.Engine(), input, Short)).ToArray();
        using var harmony = Patched();
        var live = new ConcurrentDictionary<object, int>(ReferenceEqualityComparer.Instance);
        var shared = 0;
        var threads = new ConcurrentDictionary<int, bool>();
        Counting.Hud = true;
        var start = ColumnNoiseScratch.Saved;
        for (var round = 0; round < 2 * Passes; round++)
        {
            ColumnNoiseScratch.Enabled = round % 2 == 0;
            var results = new long[inputs.Length][];
            _ = Parallel.For(0, inputs.Length,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, i =>
                {
                    threads[Environment.CurrentManagedThreadId] = true;
                    var column = inputs[i].Scratch();
                    var entries = EntriesOf(column);
                    var mine = entries.Length > 0 && live.TryAdd(entries, i);
                    if (entries.Length > 0 && !mine) _ = Interlocked.Increment(ref shared);
                    results[i] = Trace(column, inputs[i], Short);
                    if (mine) _ = live.TryRemove(entries, out _);
                });
            Assert.Multiple(() =>
            {
                Assert.That(shared, Is.Zero, "two live columns shared their arrays");
                for (var i = 0; i < inputs.Length; i++)
                    Assert.That(results[i], Is.EqualTo(engine[i]), $"round {round}, column {i}");
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(threads, Has.Count.GreaterThan(1), "the loop has to run on several threads");
            Assert.That(ColumnNoiseScratch.Saved - start, Is.GreaterThan(Passes * inputs.Length * 100L),
                "the rounds with the rewrite on reused");
        });
    }

    // Same process, the least of the passes read: bytes per column from the shipped ForColumn, then through Column with the rewrite off
    // and on; and all allocation of one chunk column's generation on every thread, as shipped and with the rewrite off and on
    [Test]
    [Category("Slow")]
    public void GarbageBeforeAndAfter()
    {
        GameInstall.RequireAssets();
        var r = new Random(14);
        var inputs = Enumerable.Range(0, 64).Select(_ => Make(r)).ToArray();
        var gen = inputs.Select(input => input with
        {
            Noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(9, 0.00030618621784789723, 0.9, 7),
            Amplitudes = [.. Enumerable.Range(0, 9).Select(i => 0.3 + i * 0.07)],
            Thresholds = new double[9]
        }).ToArray(); // nine octaves that all contribute: GenTerra's column at the default world height

        static long Allocated(Input[] set, System.Func<Input, ColumnNoise> build)
        {
            double sink = 0;
            for (var i = 0; i < 200; i++) sink += build(set[i % set.Length]).BoundMax;
            var start = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Calls; i++) sink += build(set[i % set.Length]).BoundMax;
            Assert.That(sink, Is.Not.NaN);
            return (GC.GetAllocatedBytesForCurrentThread() - start) / Calls;
        }

        long shipped = long.MaxValue, shippedChunk = long.MaxValue;
        for (var pass = 0; pass < Passes; pass++)
            (shipped, shippedChunk) = (Math.Min(shipped, Allocated(gen, input => input.Engine())),
                Math.Min(shippedChunk, PerChunkColumn()));
        using var harmony = Patched();
        long off = long.MaxValue, on = long.MaxValue, mixedOn = long.MaxValue, offChunk = long.MaxValue,
            onChunk = long.MaxValue;
        for (var pass = 0; pass < Passes; pass++)
        {
            ColumnNoiseScratch.Enabled = false;
            (off, offChunk) = (Math.Min(off, Allocated(gen, input => input.Scratch())),
                Math.Min(offChunk, PerChunkColumn()));
            ColumnNoiseScratch.Enabled = true;
            (on, onChunk) = (Math.Min(on, Allocated(gen, input => input.Scratch())),
                Math.Min(onChunk, PerChunkColumn()));
            mixedOn = Math.Min(mixedOn, Allocated(inputs, input => input.Scratch()));
        }

        Assert.Multiple(() =>
        {
            Assert.That(off, Is.EqualTo(shipped));
            Assert.That(on, Is.Zero);
            Assert.That(mixedOn, Is.Zero, "1 to 14 octaves, random");
            // double[9] 96, int[9] 64, OctaveEntry[9] 600, PastEvaluation[9] 168
            Assert.That(shipped, Is.EqualTo(96 + 64 + 600 + 168));
            Assert.That(shippedChunk - onChunk, Is.GreaterThan(1024L * 500),
                "at least half a kilobyte less per block column");
            Assert.That(Math.Abs(offChunk - shippedChunk), Is.LessThan(1024L * 100),
                "switched off it allocates as shipped");
        });
    }

    private static long PerChunkColumn()
    {
        var world = World(5);
        _ = Generate(world, 5, 5, false); // the region's landform map and the threads' temporary arrays
        var start = GC.GetTotalAllocatedBytes(true);
        for (var z = 0; z < 4; z++) _ = Generate(world, 5, 6 + z, false);
        return (GC.GetTotalAllocatedBytes(true) - start) / 4;
    }

    // GenTerra's own chunk column generation, as the server calls it for a new chunk column: initWorldGen against a stand-in server with
    // the game's landforms.json, then OnChunkColumnGen for chunk columns across a region, with and without border smoothing. Several
    // generate at once, each on its own GenTerra with its own Parallel.For: the pool threads take bodies of one chunk column and then of
    // another, so a thread's arrays pass between generations. Every block and fluid written, both height maps and YMax must be what the
    // shipped body produces, with the rewrite on and off.
    [Test]
    [Category("Slow")]
    public void GenTerraGeneratesTheSameTerrain()
    {
        GameInstall.RequireAssets();
        (int Seed, int X, int Z, bool Smooth)[] jobs =
        [
            (1, 3, 4, false), (1, 9, 12, true), (2, 0, 0, false), (2, 15, 15, true), (3, 6, 1, false), (3, 7, 1, true),
            (4, 12, 8, false), (4, 2, 11, true)
        ];
        var engine = jobs.Select(j => Fresh(j.Seed, j.X, j.Z, j.Smooth)).ToList();
        using var harmony = Patched();
        Counting.Hud = true;
        var saved = new long[2];
        for (var round = 0; round < 2; round++)
        {
            ColumnNoiseScratch.Enabled = round == 0;
            var start = ColumnNoiseScratch.Saved;
            // one after another: LoadLandforms fills a static
            var worlds = jobs.Select(j => World(j.Seed)).ToArray();
            var results = new Generated[jobs.Length];
            _ = Parallel.For(0, jobs.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                i => results[i] = Generate(worlds[i], jobs[i].X, jobs[i].Z, jobs[i].Smooth));
            saved[round] = ColumnNoiseScratch.Saved - start;
            Assert.Multiple(() =>
            {
                for (var i = 0; i < jobs.Length; i++)
                {
                    Assert.That(results[i].Writes, Is.EqualTo(engine[i].Writes),
                        $"blocks of chunk column {i}, round {round}");
                    Assert.That(results[i].HeightMaps, Is.EqualTo(engine[i].HeightMaps),
                        $"height maps of chunk column {i}, round {round}");
                }
            });
        }

        ColumnNoiseScratch.Enabled = true;
        Assert.Multiple(() =>
        {
            Assert.That(engine.Select(c => c.Writes.Count(w => w.Op == Write.Block)), Is.All.GreaterThan(1000),
                "every chunk column has terrain");
            Assert.That(engine.SelectMany(c => c.Writes).Count(w => w.Op == Write.Fluid), Is.GreaterThan(1000),
                "some columns lie under water");
            Assert.That(engine.Select(c => c.HeightMaps[^1]).Distinct().Count(), Is.GreaterThan(2),
                "the chunk columns differ in height");
            Assert.That(saved[0], Is.GreaterThan(jobs.Length * 1024L * 500),
                "every block column was built from the thread's arrays");
            Assert.That(saved[1], Is.Zero, "switched off");
        });
    }

    private static MethodBase Seam(int seam) => seam switch
    {
        0 => ColumnNoiseScratch.ForColumn()!,
        1 => ColumnNoiseScratch.Constructor()!,
        2 => ColumnNoiseScratch.ColumnBody()!,
        _ => AccessTools.Method(typeof(ColumnNoise), nameof(ColumnNoise.NoiseSign))
    };

    private static void SeenByAnotherMod() => _ = Interlocked.Increment(ref _seen);

    // A patch another mod adds later on ForColumn, the constructor or the body stands the arm down, and says so once: every column owns
    // new arrays again, with the engine's values
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    public void AnotherModsLaterPatchStandsTheArmDown(int seam)
    {
        _ = typeof(GenTerra).Assembly;
        var input = Contributing(new Random(17));
        var engine = Trace(input.Engine(), input);
        var logger = new CapturingLogger();
        using var harmony = Patched(logger);
        using var other = new TestHarmony("komet-test-another-mod");
        var before = EntriesOf(input.Scratch());
        Assert.That(EntriesOf(input.Scratch()), Is.SameAs(before), "armed: the thread's array again");
        _ = other.Patch(Seam(seam),
            postfix: new HarmonyMethod(typeof(ColumnNoiseScratchTests), nameof(SeenByAnotherMod)));
        Counting.Hud = true;
        var (seen, start) = (_seen, ColumnNoiseScratch.Saved);
        var (first, second) = (input.Scratch(), input.Scratch());
        Assert.Multiple(() =>
        {
            Assert.That(ColumnNoiseScratch.Rewritten, Is.False, "stood down");
            Assert.That(EntriesOf(first), Is.Not.SameAs(before));
            Assert.That(EntriesOf(second), Is.Not.SameAs(EntriesOf(first)), "every column owns new arrays");
            Assert.That(ColumnNoiseScratch.Saved, Is.EqualTo(start));
            Assert.That(Trace(first, input), Is.EqualTo(engine));
            if (seam < 2) Assert.That(_seen, Is.GreaterThan(seen), "the other mod's patch ran");
            Assert.That(string.Join('\n', logger.Lines), Is.EqualTo(
                "Notification Komet ColumnNoiseScratch stands down: another mod patched " +
                seam switch
                {
                    0 => "NewNormalizedSimplexFractalNoise.ForColumn",
                    1 => "the ColumnNoise constructor",
                    _ => "GenTerra's column body"
                } +
                " after install; terrain columns allocate as shipped"), "once");
        });
    }

    // One found at Install - here on NoiseSign, which the body calls with its column - leaves every method as shipped, and says so
    [Test]
    public void AnotherModsPatchAtInstallLeavesTheEngineAlone()
    {
        _ = typeof(GenTerra).Assembly;
        using var other = new TestHarmony("komet-test-another-mod");
        using var harmony = new TestHarmony("komet-test-columnnoise");
        var logger = new CapturingLogger();
        _ = other.Patch(Seam(3), new HarmonyMethod(typeof(ColumnNoiseScratchTests), nameof(SeenByAnotherMod)));
        ColumnNoiseScratch.Install(harmony, logger);
        var input = Contributing(new Random(18));
        var (first, second) = (input.Scratch(), input.Scratch());
        Assert.Multiple(() =>
        {
            Assert.That(ColumnNoiseScratch.Rewritten, Is.False);
            for (var i = 0; i < 3; i++)
                Assert.That(Harmony.GetPatchInfo(Seam(i))?.Owners ?? [], Does.Not.Contain(harmony.Id), $"seam {i}");
            Assert.That(EntriesOf(second), Is.Not.SameAs(EntriesOf(first)), "every column owns new arrays");
            Assert.That(string.Join('\n', logger.Lines), Is.EqualTo(
                "Notification Komet ColumnNoiseScratch stands down: another mod patches ColumnNoise.NoiseSign; terrain columns allocate as shipped"));
        });
    }

    // ForColumn is only watched: its IL goes back as it came
    [Test]
    public void ForColumnIsWatchedNotChanged()
    {
        var forColumn = ColumnNoiseScratch.ForColumn();
        Assert.That(forColumn, Is.Not.Null);
        var original = PatchProcessor.GetOriginalInstructions(forColumn!);
        var watched = ColumnNoiseScratch.WatchForColumn(PatchProcessor.GetOriginalInstructions(forColumn!));
        Assert.Multiple(() =>
        {
            Assert.That(watched.Select(c => (c.opcode, c.operand)),
                Is.EqualTo(original.Select(c => (c.opcode, c.operand))));
            Assert.That(original.Count(c => c.opcode == OpCodes.Newobj), Is.EqualTo(1));
        });
    }

    // The ColumnNoise local copied by value - the arrays could then outlive the body's use of them - leaves the engine's IL untouched,
    // and so does an address of it that goes anywhere but ColumnNoise's own members
    [Test]
    public void AColumnThatCouldEscapeTheBodyIsLeftAlone()
    {
        _ = typeof(GenTerra).Assembly;
        var body = ColumnNoiseScratch.ColumnBody();
        Assert.That(body, Is.Not.Null);
        var original = PatchProcessor.GetOriginalInstructions(body!);
        var forColumn = ColumnNoiseScratch.ForColumn()!;
        var store = original.FindIndex(code => code.Calls(forColumn)) + 1;
        Assert.Multiple(() =>
        {
            Assert.That(ColumnNoiseScratch.RewriteBody(original).Count(IsMine), Is.EqualTo(1),
                "the engine's own shape is rewritten");
            Assert.That(original[store].opcode, Is.EqualTo(OpCodes.Stloc_S), "the column goes to a local");
        });
        var local = original[store].operand;

        List<CodeInstruction> Variant(params CodeInstruction[] inserted)
        {
            var code = PatchProcessor.GetOriginalInstructions(body!);
            code.InsertRange(store + 1, inserted);
            return code;
        }

        var copied = Variant(new CodeInstruction(OpCodes.Ldloc_S, local), new CodeInstruction(OpCodes.Pop));
        var boxed = Variant(new CodeInstruction(OpCodes.Ldloca_S, local),
            new CodeInstruction(OpCodes.Ldobj, typeof(ColumnNoise)), new CodeInstruction(OpCodes.Pop));
        var stored = Variant(new CodeInstruction(OpCodes.Ldloca_S, local),
            new CodeInstruction(OpCodes.Initobj, typeof(ColumnNoise)));
        var built = Variant(new CodeInstruction(OpCodes.Ldloca_S, local), new CodeInstruction(OpCodes.Ldnull),
            new CodeInstruction(OpCodes.Ldc_R8, 0.0),
            new CodeInstruction(OpCodes.Ldnull), new CodeInstruction(OpCodes.Ldnull),
            new CodeInstruction(OpCodes.Ldc_R8, 0.0),
            new CodeInstruction(OpCodes.Ldc_R8, 0.0),
            new CodeInstruction(OpCodes.Call, ColumnNoiseScratch.Constructor()));
        Assert.Multiple(() =>
        {
            foreach (var (name, code) in new[]
                     {
                         ("copied", copied), ("loaded through its address", boxed), ("overwritten", stored),
                         ("built again", built)
                     })
            {
                Assert.That(ColumnNoiseScratch.RewriteBody(code).Count(IsMine), Is.Zero, name);
                Assert.That(ColumnNoiseScratch.Rewritten, Is.False, name);
            }
        });
    }

    // An array of the constructor handed to a call, kept in a static, put into an array of references or stored through an address
    // would outlive the constructor; so would one of a fifth newarr
    [Test]
    public void AnArrayThatCouldLeaveTheConstructorIsLeftAlone()
    {
        var ctor = ColumnNoiseScratch.Constructor();
        Assert.That(ctor, Is.Not.Null);
        var original = PatchProcessor.GetOriginalInstructions(ctor!);
        var generator = new DynamicMethod("komet-probe", typeof(void), Type.EmptyTypes).GetILGenerator();
        var first = original.FindIndex(code => code.opcode == OpCodes.Newarr) + 2; // after newarr double; stloc.1
        Assert.That(ColumnNoiseScratch.RewriteConstructor(original, generator).Count(IsMine), Is.EqualTo(5),
            "Take and four Rent");
        var stored = original.FindIndex(code => code.opcode == OpCodes.Stfld && Equals(code.operand, Past)) + 1;

        List<CodeInstruction> At(int index, params CodeInstruction[] inserted)
        {
            var code = PatchProcessor.GetOriginalInstructions(ctor!);
            code.InsertRange(index, inserted);
            return code;
        }

        var keeper = AccessTools.Field(typeof(Keeper), nameof(Keeper.Kept));
        var handed = At(first, new CodeInstruction(OpCodes.Ldloc_1),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(GC), nameof(GC.KeepAlive))));
        var kept = At(first, new CodeInstruction(OpCodes.Ldloc_1), new CodeInstruction(OpCodes.Stsfld, keeper));
        var element = At(first,
            new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Keeper), nameof(Keeper.Many))),
            new CodeInstruction(OpCodes.Ldc_I4_0), new CodeInstruction(OpCodes.Ldloc_1),
            new CodeInstruction(OpCodes.Stelem_Ref));
        var address = At(first, new CodeInstruction(OpCodes.Ldsflda, keeper), new CodeInstruction(OpCodes.Ldloc_1),
            new CodeInstruction(OpCodes.Stind_Ref));
        var extra = At(first, new CodeInstruction(OpCodes.Ldc_I4_1),
            new CodeInstruction(OpCodes.Newarr, typeof(double)), new CodeInstruction(OpCodes.Pop));
        var member = At(first, new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldc_R8, 1.0),
            new CodeInstruction(OpCodes.Ldc_R8, 0.0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ColumnNoise), nameof(ColumnNoise.NoiseSign))),
            new CodeInstruction(OpCodes.Pop));
        var late = At(stored, new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldc_R8, 0.0),
            new CodeInstruction(OpCodes.Call,
                AccessTools.PropertySetter(typeof(ColumnNoise), nameof(ColumnNoise.BoundMin))));
        Assert.Multiple(() =>
        {
            foreach (var (name, code) in new[]
                     {
                         ("handed to a call", handed), ("kept in a static", kept), ("put into an array", element),
                         ("stored through an address", address),
                         ("a fifth array", extra), ("`this` handed to a member that is no bound setter", member),
                         ("a bound setter once `this` holds the arrays", late)
                     })
            {
                Assert.That(ColumnNoiseScratch.RewriteConstructor(code, generator).Count(IsMine), Is.Zero, name);
                Assert.That(ColumnNoiseScratch.Rewritten, Is.False, name);
            }
        });
    }

    // Rent itself. The thread's array of a slot and length comes back the same and zeroed, whatever the last column left in it. Another
    // slot, length or element type is another array, and so is every one without the thread's arrays or past the table. A negative
    // length throws as newarr's does.
    [Test]
    public void RentHandsBackTheSameArrayZeroed()
    {
        var arrays = new Array?[4 * 64];
        var first = ColumnNoiseScratch.Rent<long>(9, arrays, 1);
        first.AsSpan().Fill(-1);
        var again = ColumnNoiseScratch.Rent<long>(9, arrays, 1);
        var zeroed = Array.TrueForAll(again, value => value == 0); // read before anything else writes it
        var (slot, length) = (ColumnNoiseScratch.Rent<long>(9, arrays, 2), ColumnNoiseScratch.Rent<long>(8, arrays, 1));
        var (unarmed, huge) = (ColumnNoiseScratch.Rent<long>(9, null, 1), ColumnNoiseScratch.Rent<long>(64, arrays, 1));
        var typed = ColumnNoiseScratch.Rent<double>(9, arrays, 1);
        var evicted = ColumnNoiseScratch.Rent<long>(9, arrays, 1);
        Assert.Multiple(() =>
        {
            Assert.That(again, Is.SameAs(first));
            Assert.That(zeroed, Is.True, "handed back zeroed, whatever the last column left in it");
            Assert.That(slot, Is.Not.SameAs(first).And.Length.EqualTo(9));
            Assert.That(length, Is.Not.SameAs(first).And.Length.EqualTo(8));
            Assert.That(typed, Has.Length.EqualTo(9));
            Assert.That(evicted, Is.Not.SameAs(first), "a double[] took the slot, the long[] after it is new");
            Assert.That(unarmed, Is.Not.SameAs(first).And.Length.EqualTo(9));
            Assert.That(huge, Has.Length.EqualTo(64).And.Not.SameAs(ColumnNoiseScratch.Rent<long>(64, arrays, 1)));
            Assert.That(() => ColumnNoiseScratch.Rent<long>(-1, arrays, 1), Throws.TypeOf<OverflowException>());
        });
    }

    private static bool IsMine(CodeInstruction code) =>
        code.operand is MethodInfo { DeclaringType: var t } && t == typeof(ColumnNoiseScratch);

    private static Generated Fresh(int seed, int chunkX, int chunkZ, bool smooth) =>
        Generate(World(seed), chunkX, chunkZ, smooth);

    // One region of one world: the four maps GenTerra reads, drawn at random but fixed by the seed, and a GenTerra set up by its own
    // initWorldGen. The landform map is the region's, 32 pixels plus the engine's padding of 4, each pixel a landform of the game's.
    // initWorldGen sizes the Parallel.For by the machine's cores; eight threads make every machine run the body concurrently.
    private static Region World(int seed)
    {
        var api = Server(seed);
        NoiseLandforms.LoadLandforms(api);
        var terra = new GenTerra();
        AccessTools.Field(typeof(GenTerra), "api").SetValue(terra, api);
        terra.initWorldGen();
        AccessTools.Field(typeof(GenTerra), "maxThreads").SetValue(terra, Threads);
        var r = new Random(seed);
        var count = NoiseLandforms.landforms.LandFormsByIndex.Length;
        return new Region(terra, Answers.Of<IMapRegion>(new()
        {
            ["get_ClimateMap"] = Fixed(Map(r, 16, 2, x => x.Next(0x1000000))),
            ["get_OceanMap"] = Fixed(Map(r, 16, 2, x => x.Next(3) == 0 ? x.Next(0, 255) : 0)),
            ["get_UpheavelMap"] = Fixed(Map(r, 16, 2, x => x.Next(0, 255))),
            ["get_LandformMap"] = Fixed(Map(r, 32, 4, x => x.Next(count)))
        }));
    }

    private static System.Func<object?[]?, object?> Fixed(object value) => _ => value;

    private static IntDataMap2D Map(Random r, int inner, int padding, System.Func<Random, int> value)
    {
        var size = inner + 2 * padding;
        return new IntDataMap2D
        {
            Data = [.. Enumerable.Range(0, size * size).Select(_ => value(r))], Size = size, TopLeftPadding = padding,
            BottomRightPadding = padding
        };
    }

    // The chunk column request: eight chunks whose blocks record every write in order, one map chunk with the two height maps, and
    // for border smoothing the neighbours' terrain heights on some sides
    private static Generated Generate(Region region, int chunkX, int chunkZ, bool smooth)
    {
        var writes = new List<(Write Op, int Chunk, int Index, int Value)>();
        var (rain, terrain) = (new ushort[1024], new ushort[1024]);
        var yMax = new int[1];
        var map = Answers.Of<IMapChunk>(new()
        {
            ["get_MapRegion"] = Fixed(region.Maps), ["get_RainHeightMap"] = Fixed(rain),
            ["get_WorldGenTerrainHeightMap"] = Fixed(terrain),
            ["set_YMax"] = args => yMax[0] = (ushort)args![0]!
        });
        var chunks = Enumerable.Range(0, MapSizeY / 32).Select(chunk =>
        {
            var blocks = Answers.Of<IChunkBlocks>(new()
            {
                ["SetBlockBulk"] = args => Log(writes, Write.Bulk, chunk, (int)args![0]!,
                    (int)args[3]! ^ ((int)args[1]! << 16) ^ ((int)args[2]! << 24)),
                ["set_Item"] = args => Log(writes, Write.Block, chunk, (int)args![0]!, (int)args[1]!),
                ["SetFluid"] = args => Log(writes, Write.Fluid, chunk, (int)args![0]!, (int)args[1]!)
            });
            return Answers.Of<IServerChunk>(new()
            { ["get_Data"] = Fixed(blocks), ["get_MapChunk"] = Fixed(map) });
        }).ToArray();
        var r = new Random(chunkX * 31 + chunkZ);
        var neighbours = new ushort[8][];
        for (var i = 0; i < neighbours.Length; i++)
            if (smooth && r.Next(3) > 0)
                neighbours[i] = [.. Enumerable.Range(0, 1024).Select(_ => (ushort)r.Next(80, 170))];
        var request = Answers.Of<IChunkColumnGenerateRequest>(new()
        {
            ["get_Chunks"] = Fixed(chunks), ["get_ChunkX"] = Fixed(chunkX), ["get_ChunkZ"] = Fixed(chunkZ),
            ["get_NeighbourTerrainHeight"] = Fixed(neighbours), ["get_RequiresChunkBorderSmoothing"] = Fixed(smooth)
        });
        _ = ColumnGen.Invoke(region.Terra, [request]);
        return new Generated(writes, [.. rain.Select(h => (int)h), .. terrain.Select(h => (int)h), yMax[0]]);
    }

    private static object? Log(List<(Write Op, int Chunk, int Index, int Value)> writes, Write op, int chunk, int index,
        int value)
    {
        writes.Add((op, chunk, index, value)); // after the Parallel.For, on the generating thread only
        return null;
    }

    // Just enough of a server: initWorldGen reads the world's size and seed and the thread limits, GlobalConfig.GetInstance finds the
    // block ids in the API's object cache before it would look anything up, and LoadLandforms reads the game's own landforms.json
    private static ICoreServerAPI Server(int seed)
    {
        var world = Answers.Of<IWorldManagerAPI>(new()
        {
            ["get_MapSizeX"] = Fixed(1024000), ["get_MapSizeY"] = Fixed(MapSizeY), ["get_MapSizeZ"] = Fixed(1024000),
            ["get_RegionSize"] = Fixed(RegionSize), ["get_Seed"] = Fixed(seed)
        });
        var config = new GlobalConfig
        { defaultRockId = 1, waterBlockId = 2, saltWaterBlockId = 3, lakeIceBlockId = 4, mantleBlockId = 5 };
        var cache = new Dictionary<string, object> { [GlobalConfig.cacheKey] = config };
        var landforms = Path.Combine(GameInstall.Assets, "survival", "worldgen", "landforms.json");
        var assets = Answers.Of<IAssetManager>(new()
        { ["Get"] = args => new Asset(File.ReadAllBytes(landforms), (AssetLocation)args![0]!, null) });
        var server = Answers.Of<IServerAPI>(new()
        { ["get_Config"] = Fixed(Answers.Of<IServerConfig>([])) });
        return Answers.Of<ICoreServerAPI>(new()
        {
            ["get_WorldManager"] = Fixed(world),
            ["get_World"] =
                Fixed(Answers.Of<IServerWorldAccessor>(new()
                { ["get_Seed"] = Fixed(seed) })),
            ["get_Server"] = Fixed(server), ["get_ObjectCache"] = Fixed(cache), ["get_Assets"] = Fixed(assets),
            ["get_Event"] = Fixed(Answers.Of<IServerEventAPI>([])),
            ["get_ModLoader"] = Fixed(Answers.Of<IModLoader>([]))
        });
    }

    private sealed record Input(NewNormalizedSimplexFractalNoise Noise, double YFrequency, double[] Amplitudes,
        double[] Thresholds, double X, double Z, double[] Thresholders)
    {
        public ColumnNoise Engine() => Noise.ForColumn(YFrequency, Amplitudes, Thresholds, X, Z);

        public ColumnNoise Scratch() => ColumnNoiseScratch.Column(Noise, YFrequency, Amplitudes, Thresholds, X, Z);
    }

    private static class Keeper
    {
        public static object? Kept = string.Empty;

        public static object?[] Many = [null];
    }

    private enum Write
    {
        Bulk,
        Block,
        Fluid
    }

    private sealed record Generated(List<(Write Op, int Chunk, int Index, int Value)> Writes, int[] HeightMaps);

    private sealed record Region(GenTerra Terra, IMapRegion Maps);
}
