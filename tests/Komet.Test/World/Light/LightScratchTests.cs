using Komet.Core;
using Komet.World;

namespace Komet.Test.World;

// Golden test against the engine: LightWorld's scenes lit by the engine's own ChunkIlluminator as shipped, then patched with the
// scratch off and on, in the same process. Every step's touched chunks, the light of the chunks it changed and at the end every light
// value of the world must match bit for bit - also with worlds taking turns on one thread, with illuminators on several threads at
// once, with a light update started from inside another one, on another world or on the same, and after updates that failed half way.
public sealed class LightScratchTests
{
    private const int Steps = 160, Calls = 20, Passes = 3;
    private static readonly int[] Seeds = [1, 2];

    // Any static object; only inspected
    private static readonly FieldInfo Sink = AccessTools.Field(typeof(Type), nameof(Type.Missing));

    private static readonly FieldInfo Visited = AccessTools.Field(typeof(ChunkIlluminator), "VisitedNodes");

    [TearDown]
    public void Reset() => LightScratch.Enabled = true;

    private static TestHarmony Patched()
    {
        var harmony = new TestHarmony("komet-test-lightscratch");
        LightScratch.Install(harmony);
        Assert.That(LightScratch.Rewritten, Is.True, "ChunkIlluminator no longer has the expected shape");
        return harmony;
    }

    private static Lit Result(LightWorld world) => new Lit(world.Touched, world.Hashes, world.Errors, world.Light());

    // One world lit up and scripted; a full hash every 40 steps on top of the per-step ones
    private static Lit Light(int seed, int steps = Steps)
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), seed);
        world.LightUp();
        foreach (var step in world.Script(seed * 7 + 1, steps))
            if (step % 40 == 0)
                world.Hashes.Add(world.Hash());
        return Result(world);
    }

    private static void Same(Lit engine, Lit mine, string what)
    {
        var touched = FirstDifference(engine.Touched, mine.Touched, (a, b) => a.SequenceEqual(b));
        var hashes = FirstDifference(engine.Hashes, mine.Hashes, (a, b) => a == b);
        var chunk = FirstDifference(engine.Values, mine.Values, (a, b) => a.SequenceEqual(b));
        Assert.Multiple(() =>
        {
            Assert.That(engine.Touched, Is.Not.Empty);
            Assert.That(touched, Is.EqualTo(-1), $"{what}: touched chunks differ from step {touched} on");
            Assert.That(hashes, Is.EqualTo(-1), $"{what}: light differs from record {hashes} on");
            Assert.That(chunk, Is.EqualTo(-1), $"{what}: the light of chunk {chunk} differs at the end");
            Assert.That(mine.Errors, Is.EqualTo(engine.Errors), $"{what}: errors");
        });
    }

    private static int FirstDifference<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, System.Func<T, T, bool> equal)
    {
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
            if (!equal(a[i], b[i]))
                return i;
        return a.Count == b.Count ? -1 : Math.Min(a.Count, b.Count);
    }

    [Test]
    [Category("Slow")]
    public void LightsExactlyWhatTheEngineLights()
    {
        var engine = Seeds.Select(seed => Light(seed)).ToList();
        using var harmony = Patched();
        LightScratch.Enabled = false;
        var off = Seeds.Select(seed => Light(seed)).ToList();
        LightScratch.Enabled = true;
        var on = Seeds.Select(seed => Light(seed)).ToList();
        for (var i = 0; i < Seeds.Length; i++)
        {
            Same(engine[i], off[i], $"seed {Seeds[i]} switched off");
            Same(engine[i], on[i], $"seed {Seeds[i]}");
        }
    }

    // Two worlds on one thread, a step each in turn: each illuminator's VisitedNodes still holds the keys the other's last update
    // reuses, until its own next update clears it
    private static (Lit, Lit) TakingTurns(int seed)
    {
        var (a, b) = (new LightWorld(LightWorld.MakeBlocks(), seed),
            new LightWorld(LightWorld.MakeBlocks(), seed + 10));
        a.LightUp();
        b.LightUp();
        using var first = a.Script(seed * 7 + 1, Steps).GetEnumerator();
        using var second = b.Script((seed + 10) * 7 + 1, Steps).GetEnumerator();
        for (var step = 0; step < Steps; step++) _ = first.MoveNext() && second.MoveNext();
        return (Result(a), Result(b));
    }

    [Test]
    [Category("Slow")]
    public void WorldsTakingTurnsOnOneThread()
    {
        var engine = TakingTurns(4);
        using var harmony = Patched();
        var mine = TakingTurns(4);
        Same(engine.Item1, mine.Item1, "the first world");
        Same(engine.Item2, mine.Item2, "the second world");
    }

    // Four threads light their own worlds at once, as the server's chunk thread, its main thread and the client's relight thread do,
    // each two worlds taking turns; every world has to come out as the engine lit it alone
    [Test]
    [Category("Slow")]
    public void ThreadsLightTheirOwnWorlds()
    {
        int[] seeds = [5, 6, 7, 8];
        var engine = seeds.Select(TakingTurns).ToList();
        using var harmony = Patched();
        var mine = new (Lit, Lit)?[seeds.Length];
        var failures = Threads.Run(seeds.Length, t =>
        {
            mine[t] = TakingTurns(seeds[t]);
            return null;
        });
        Assert.That(failures, Is.All.Null);
        for (var t = 0; t < seeds.Length; t++)
        {
            Same(engine[t].Item1, mine[t]!.Value.Item1, $"thread {t}, first world");
            Same(engine[t].Item2, mine[t]!.Value.Item2, $"thread {t}, second world");
        }
    }

    // A world whose hooked blocks step another script: on a second world (another illuminator on the same thread), or on the same
    // world, which clears VisitedNodes under the walk and can make the engine's enumerations throw - repeated, not repaired
    private static (Lit World, Lit Other, int Nested) Nested(int seed, bool same)
    {
        var (absorbing, lighting) = (new Hook(29, 40), new Hook(3, 40));
        var world = new LightWorld(LightWorld.MakeBlocks(new NestingBlock(absorbing), new NestingLightBlock(lighting)),
            seed)
        { Catching = true };
        var other = new LightWorld(LightWorld.MakeBlocks(), seed + 20) { Catching = true };
        other.LightUp();
        var nested = 0;
        using var inner = (same ? world : other).Script(seed * 11 + 3, 1000).GetEnumerator();
        absorbing.Step = lighting.Step = () => nested += inner.MoveNext() ? 1 : 0;
        world.LightUp();
        _ = world.Script(seed * 7 + 1, Steps).Count();

        return (Result(world), Result(other), nested);
    }

    [TestCase(false)]
    [TestCase(true)]
    [Category("Slow")]
    public void NestedUpdatesGetTheirOwnScratch(bool same)
    {
        var engine = Nested(9, same);
        using var harmony = Patched();
        var mine = Nested(9, same);
        Same(engine.World, mine.World, "the world");
        Same(engine.Other, mine.Other, "the other world");
        Assert.That(mine.Nested, Is.EqualTo(engine.Nested).And.GreaterThan(20), "nested steps");
    }

    private static LightWorld Measured()
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), 1) { Recording = false };
        world.LightUp();
        return world;
    }

    // The two operations measured: a lantern placed into the cluster on the chunk corner and removed again, and a block put into its
    // light and taken out
    private static Action[] Operations(LightWorld world)
    {
        var illuminator = world.Illuminator;
        var lantern = world.Blocks[LightWorld.Lantern].GetLightHsv(null!, null!);
        var (air, stone) = (world.Blocks[LightWorld.Air].LightAbsorption,
            world.Blocks[LightWorld.Stone].LightAbsorption);
        var (lx, ly, lz) = Open(world, 33, 45, 33);
        var (bx, by, bz) = Open(world, 31, 46, 32);
        return
        [
            () =>
            {
                world.Set(lx, ly, lz, LightWorld.Lantern);
                _ = illuminator.PlaceBlockLight(lantern, lx, ly, lz);
                world.Set(lx, ly, lz, LightWorld.Air);
                _ = illuminator.RemoveBlockLight(lantern, lx, ly, lz);
            },
            () =>
            {
                world.Set(bx, by, bz, LightWorld.Stone);
                _ = illuminator.UpdateBlockLight(air, stone, bx, by, bz);
                world.Set(bx, by, bz, LightWorld.Air);
                _ = illuminator.UpdateBlockLight(stone, air, bx, by, bz);
            }
        ];
    }

    private static (int X, int Y, int Z) Open(LightWorld world, int x, int y, int z)
    {
        var free = Enumerable.Range(y, LightWorld.SizeY - y)
            .First(h => world.BlockAt(x, h, z) == LightWorld.Air && world.Fluid(x, h, z) == 0);
        return (x, free, z);
    }

    private static long Allocated(Action operation)
    {
        for (var i = 0; i < 3; i++) operation();
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++) operation();
        return (GC.GetAllocatedBytesForCurrentThread() - start) / Calls;
    }

    // The least of several passes: a one-off allocation of the runtime's (tiering, a palette growing) only adds bytes
    private static long[] Least(long[] least, long[] pass) => least.Length == 0 ? pass : [.. least.Zip(pass, Math.Min)];

    // Every pass on a fresh world, so that every variant goes through the same states: what is left between them is the runtime's own
    private static long[] Measure()
    {
        long[] least = [];
        for (var pass = 0; pass < Passes; pass++) least = Least(least, [.. Operations(Measured()).Select(Allocated)]);
        return least;
    }

    // Same process: the engine's own IL through Harmony, which is what the scratch switched off is compared with (a method Harmony has
    // patched no longer runs the code it was first compiled to); then the scratch off and on
    [Test]
    [Category("Slow")]
    public void GarbagePerUpdateBeforeAndAfter()
    {
        string[] methods = ["CollectLightValuesForLightSource", "UpdateLightAt", "SpreadDarkness"];
        var identity = new Harmony("komet-test-lightscratch-identity");
        foreach (var method in methods)
            _ = identity.Patch(LightScratch.Method(method), transpiler: Foreign.Transpiler);
        var engine = Measure();
        identity.UnpatchAll(identity.Id);
        using var harmony = Patched();
        LightScratch.Enabled = false;
        var off = Measure();
        LightScratch.Enabled = true;
        var on = Measure();
        Assert.Multiple(() =>
        {
            // within a kilobyte of 4.5 MB: the runtime's own (tiering) lands on the thread now and then, ~100 bytes
            Assert.That(off, Is.EqualTo(engine).Within(1024),
                "switched off, the rewrite has to allocate what the engine does");
            for (var i = 0; i < on.Length; i++)
                Assert.That(on[i], Is.LessThan(engine[i] / 10), $"operation {i} kept most of its garbage");
        });
    }

    // A world whose hooked blocks fail: updates stop half way, leaving a queue with entries and nodes in
    // VisitedNodes behind, and the next update on the thread has to come out as the engine's does after the same failure
    private static Lit Failing(int seed)
    {
        var world = new LightWorld(LightWorld.MakeBlocks(new FailingBlock(11)), seed) { Catching = true };
        world.LightUp();
        _ = world.Script(seed * 7 + 1, Steps).Count();
        return Result(world);
    }

    [Test]
    [Category("Slow")]
    public void FailedUpdatesLeaveNothingBehind()
    {
        var engine = Failing(12);
        using var harmony = Patched();
        var mine = Failing(12);
        Same(engine, mine, "the failing world");
        Assert.That(engine.Errors, Has.Count.GreaterThan(5), "failed updates");
    }

    private static List<CodeInstruction> Original(string name) =>
        PatchProcessor.GetOriginalInstructions(LightScratch.Method(name)!);

    private static List<CodeInstruction> Escaping(List<CodeInstruction> code, CodeInstruction store)
    {
        var local = Il.Local(store);
        Assert.That(store.IsStloc() && local >= 0, Is.True);
        code.InsertRange(0, [new CodeInstruction(OpCodes.Ldloc, local), new CodeInstruction(OpCodes.Stsfld, Sink)]);
        return code;
    }

    private static bool IsScratch(CodeInstruction code) =>
        code.operand is MethodInfo { DeclaringType: var t } && t == typeof(LightScratch);

    // Every other shape has to hand the engine its own IL back untouched: here the key, the node, VisitedNodes and the queue each escape
    // into a static field
    [Test]
    public void AnotherShapeIsLeftAlone()
    {
        var collect = Original("CollectLightValuesForLightSource");
        var keyLocal = collect[collect.FindIndex(c => Creates(c, typeof(Vec3i))) + 1];
        var nodeLocal = collect[collect.FindIndex(c => Creates(c, typeof(LightSourcesAtBlock))) + 2]; // after the dup
        var nodes = Original("CollectLightValuesForLightSource"); // VisitedNodes itself kept
        nodes.InsertRange(0,
        [
            new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, Visited),
            new CodeInstruction(OpCodes.Stsfld, Sink)
        ]);
        var queueLocal = collect[collect.FindIndex(c => Creates(c, typeof(QueueOfInt))) + 1];
        List<CodeInstruction>[] shapes =
        [
            Escaping(Original("CollectLightValuesForLightSource"), keyLocal),
            Escaping(Original("CollectLightValuesForLightSource"), queueLocal),
            Escaping(Original("CollectLightValuesForLightSource"), nodeLocal), nodes
        ];
        Assert.Multiple(() =>
        {
            Assert.That(LightScratch.RewriteCollect(Original("CollectLightValuesForLightSource")).Count(IsScratch),
                Is.EqualTo(7), "the engine's own shape is rewritten");
            foreach (var body in shapes)
            {
                var opcodes = body.Select(c => c.opcode).ToList();
                var result = LightScratch.RewriteCollect(body);
                Assert.That(result.Count(IsScratch), Is.Zero);
                Assert.That(result.Select(c => c.opcode), Is.EqualTo(opcodes));
            }
        });
    }

    // The proofs read the shipped IL: another mod's patch on any of the four methods, found at install, leaves all of them the engine's
    // and says which one in the log
    [TestCase("UpdateLightAt")]
    [TestCase("CollectLightValuesForLightSource")]
    [TestCase("SpreadDarkness")]
    [TestCase("RecalcBlockLightAtPos")]
    public void AnotherModsPatchAtInstallLeavesTheEngineAlone(string seam)
    {
        using var other = new TestHarmony("komet-test-another-mod");
        using var harmony = new TestHarmony("komet-test-lightscratch");
        var logger = new CapturingLogger();
        _ = other.Patch(LightScratch.Method(seam), postfix: Foreign.Postfix);
        LightScratch.Install(harmony, logger);
        Assert.Multiple(() =>
        {
            Assert.That(LightScratch.Rewritten, Is.False);
            foreach (var name in (string[])["UpdateLightAt", "CollectLightValuesForLightSource", "SpreadDarkness"])
                Assert.That(Harmony.GetPatchInfo(LightScratch.Method(name)!)?.Owners ?? [],
                    Does.Not.Contain(harmony.Id), name);
            Assert.That(logger.Lines, Has.Count.EqualTo(1));
            Assert.That(logger.Lines,
                Has.All.StartsWith("Notification").And.All.Contains("ChunkIlluminator." + seam + ";"));
        });
    }

    private static List<CodeInstruction> After(List<CodeInstruction> code, string name, Type type,
        params CodeInstruction[] extra)
    {
        var at = code.FindIndex(c =>
            c.operand is MethodInfo { Name: var n, ReturnType: var r } && n == name && r == type);
        Assert.That(at, Is.GreaterThanOrEqualTo(0), name);
        code.InsertRange(at + 1, extra);
        return code;
    }

    // UpdateLightAt passes its IL through, so the scratch is only armed when it clears VisitedNodes first and keeps no key, node or
    // entry; RecalcBlockLightAtPos, which gets every key and node, has to only read them. Each broken shape disarms, the engine's own
    // arms again, all while installed.
    [Test]
    public void ReuseArmsOnlyWhenNothingKeepsAKeyOrNode()
    {
        var entry = typeof(KeyValuePair<Vec3i, LightSourcesAtBlock>);
        var update = Original("UpdateLightAt");
        var stored = update[
            update.FindIndex(c => c.operand is MethodInfo { Name: "get_Current" } m && m.ReturnType == entry) + 1];
        var noClear = Original("UpdateLightAt");
        noClear.RemoveRange(0, 3);
        CodeInstruction[] kept = [new(OpCodes.Dup), new(OpCodes.Stsfld, Sink)];
        List<CodeInstruction>[] updates =
        [
            noClear,
            After(Original("UpdateLightAt"), "get_Key", typeof(Vec3i), kept),
            After(Original("UpdateLightAt"), "get_Value", typeof(LightSourcesAtBlock),
                [.. kept.Select(c => c.Clone())]),
            After(Original("UpdateLightAt"), "get_Current", entry, new CodeInstruction(OpCodes.Dup),
                new CodeInstruction(OpCodes.Box, entry),
                new CodeInstruction(OpCodes.Stsfld, Sink)),
            Escaping(Original("UpdateLightAt"), stored)
        ];
        var recalc = PatchProcessor.GetOriginalInstructions(LightScratch.Method("RecalcBlockLightAtPos")!);
        var hsvs = AccessTools.Field(typeof(LightSourcesAtBlock), nameof(LightSourcesAtBlock.lightHsvs));
        List<CodeInstruction>[] recalcs =
        [
            [new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Stsfld, Sink), .. recalc],
            [new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Stsfld, Sink), .. recalc],
            [
                new CodeInstruction(OpCodes.Ldarg_2), new CodeInstruction(OpCodes.Ldfld, hsvs),
                new CodeInstruction(OpCodes.Stsfld, Sink), .. recalc
            ]
        ];
        using var harmony = Patched();
        Assert.Multiple(() =>
        {
            Assert.That(LightScratch.OnlyRead(recalc), Is.True, "the engine's RecalcBlockLightAtPos");
            foreach (var body in recalcs)
                Assert.That(LightScratch.OnlyRead(body), Is.False, "RecalcBlockLightAtPos keeping an argument");
            for (var i = 0; i < updates.Length; i++)
            {
                _ = LightScratch.CheckUpdate(updates[i]);
                Assert.That(LightScratch.Rewritten, Is.False, $"UpdateLightAt shape {i}");
                _ = LightScratch.CheckUpdate(Original("UpdateLightAt"));
                Assert.That(LightScratch.Rewritten, Is.True, "the engine's UpdateLightAt again");
            }
        });
    }

    private static bool Creates(CodeInstruction code, Type type) =>
        code.opcode == OpCodes.Newobj && code.operand is ConstructorInfo { DeclaringType: var t } && t == type;

    private sealed record Lit(List<long[]> Touched, List<ulong> Hashes, List<string> Errors, int[][] Values);

    private sealed class Hook(int every, int budget)
    {
        private int _calls, _left = budget;
        private bool _inside;

        public Action? Step { get; set; }

        public void Fire()
        {
            if (Step == null || _inside || _left == 0 || ++_calls % every != 0) return;
            (_inside, _left) = (true, _left - 1);
            try
            {
                Step();
            }
            finally
            {
                _inside = false;
            }
        }
    }

    // Light absorption a mod computes, and which starts a light update while the illuminator asks for it
    private sealed class NestingBlock(Hook hook) : Block
    {
        public override int GetLightAbsorption(IWorldChunk chunk, BlockPos pos)
        {
            hook.Fire();
            return LightAbsorption;
        }
    }

    // A light whose hsv a mod computes, and which starts a light update while the illuminator walks the nearby sources; the world's
    // own calls pass no position and are left alone
    private sealed class NestingLightBlock(Hook hook) : Block
    {
        public override byte[] GetLightHsv(IBlockAccessor blockAccessor, BlockPos pos, ItemStack? stack = null)
        {
            if (pos != null) hook.Fire();
            return LightHsv;
        }
    }

    // A block whose light absorption a mod computes and which fails every so many calls, in the middle of whatever walk asks
    private sealed class FailingBlock(int every) : Block
    {
        private int _calls;

        public override int GetLightAbsorption(IWorldChunk chunk, BlockPos pos)
        {
            if (++_calls % every == 0) throw new InvalidOperationException("absorption failed");
            return LightAbsorption;
        }
    }
}
