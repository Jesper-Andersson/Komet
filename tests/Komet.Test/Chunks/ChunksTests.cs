using Komet.Chunks;
using Komet.Core;

namespace Komet.Test.Chunks;

public sealed class ChunkBudgetTests
{
    private static readonly MethodInfo Limiter =
        AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.ChunkVerticesUploadRateLimiter));

    private static readonly MethodInfo Sort =
        AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<>.Sort));

    private static readonly MethodInfo Cap = AccessTools.Method(typeof(ChunkBudget), nameof(ChunkBudget.Cap));

    private static readonly MethodInfo Gate = AccessTools.Method(typeof(ChunkBudget), nameof(ChunkBudget.Priority));

    private static readonly MethodInfo PriorityCount =
        AccessTools.PropertyGetter(typeof(Queue<TesselatedChunk>), nameof(Queue<>.Count));

    [TearDown]
    public void Reset()
    {
        (ChunkBudget.CapMillis, Counting.Hud, RuntimeStats.chunksAwaitingPooling) = (ChunkBudget.DefaultCapMillis, false, 0);
        ChunkBudget.Yield = true;
        ChunkBudget.Begin();
    }

    // After frames reaching their uploads 2 ms in, one 12 ms in yields; the next yields never, however slow; off, none does
    [TestCase(true, ExpectedResult = "False True False False True")]
    [TestCase(false, ExpectedResult = "False False False False False")]
    public string ASlowFrameYieldsItsUploadsButNeverTwice(bool yield)
    {
        var (ms, start) = (System.Diagnostics.Stopwatch.Frequency / 1000, 1L << 40);
        bool Frame(int into)
        {
            start += 20 * ms;
            return ChunkBudget.Yielding(start + into * ms, start);
        }

        for (var i = 0; i < 64; i++) _ = Frame(2);
        ChunkBudget.Yield = yield;
        return string.Join(' ', ((int[])[2, 12, 12, 2, 12]).Select(Frame));
    }

    // A world-join backlog of finished meshes raises the cap until the queue is nearly empty again; flight-sized queues never do
    [TestCase(ChunkBudget.DefaultCapMillis, ExpectedResult = "False True True False")]
    [TestCase(ChunkBudget.Uncapped, ExpectedResult = "False True True False")]
    public string ABacklogBoostsTheCapWithHysteresis(int cap)
    {
        ChunkBudget.CapMillis = cap;
        return string.Join(' ', ((int[])[300, 2000, 150, 50]).Select(waiting =>
        {
            RuntimeStats.chunksAwaitingPooling = waiting;
            ChunkBudget.Begin();
            return ChunkBudget.Boosted;
        }));
    }

    [Test]
    public void InstallCapsTheUploadLoop()
    {
        using var harmony = new TestHarmony("komet-test-chunkbudget");
        ChunkBudget.Install(harmony, null);
        Assert.That(ChunkBudget.Capped, Is.True, "the transpiler found no single loop condition to cap");
        Assert.That(ChunkBudget.Ceiled, Is.True, "the transpiler found no single priority loop condition to gate");
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        Assert.That(Harmony.GetPatchInfo(frame)?.Prefixes.Select(p => p.PatchMethod.Name),
            Does.Contain(nameof(ChunkBudget.Begin)),
            "without the prefix no frame ever gets a deadline and the cap never ends the loop");
    }

    // The shape of OnBeforeFrame around num3: stored after the limiter read, read by the early return (a forward blt before the lock) and
    // by the loop condition after Sort() (a blt back into the body). Labels need a generator, a default Label would equal every other one.
    private static List<CodeInstruction> LoopBody(int loopSites = 1, bool sort = true, int loopLocal = 4, int reads = 1)
    {
        var il = new DynamicMethod("labels", null, null).GetILGenerator();
        var (resume, body) = (il.DefineLabel(), il.DefineLabel());
        var code = new List<CodeInstruction>();
        for (var i = 0; i < reads; i++)
            code.AddRange([new CodeInstruction(OpCodes.Call, Limiter), new CodeInstruction(OpCodes.Stloc_S, 4)]);
        code.AddRange([new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, 4),
            new CodeInstruction(OpCodes.Blt_S, resume),
            new CodeInstruction(OpCodes.Ret), new CodeInstruction(OpCodes.Ldnull) { labels = [resume] }]);
        if (sort) code.Add(new CodeInstruction(OpCodes.Callvirt, Sort));
        code.Add(new CodeInstruction(OpCodes.Nop) { labels = [body] });
        for (var i = 0; i < loopSites; i++)
            code.AddRange([new CodeInstruction(OpCodes.Ldloc_2), new CodeInstruction(OpCodes.Ldloc_S, loopLocal),
                new CodeInstruction(OpCodes.Blt, body)]);
        code.Add(new CodeInstruction(OpCodes.Ret));
        return code;
    }

    [Test]
    public void TheCapGoesIntoTheLoopConditionOnly()
    {
        var result = ChunkBudget.Rewrite(LoopBody());
        var at = result.FindIndex(code => code.Calls(Cap));
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Capped, Is.True);
            Assert.That(result.Count(code => code.Calls(Cap)), Is.EqualTo(1));
            Assert.That(result.Count(code => code.Calls(Limiter)), Is.EqualTo(1), "the setting is still the engine's");
            Assert.That(result[3].opcode, Is.EqualTo(OpCodes.Ldloc_S), "the early return reads num3 untouched");
            Assert.That(result[4].opcode, Is.EqualTo(OpCodes.Blt_S));
            Assert.That(result[at - 1].opcode, Is.EqualTo(OpCodes.Ldloc_S), "the cap consumes num3");
            Assert.That(result[at + 1].opcode, Is.EqualTo(OpCodes.Blt), "and feeds the loop's own branch");
            Assert.That(result[at].labels, Is.Empty);
        });
    }

    // Two loops on num3, none, no Sort(), a loop on another local, no read of the limiter or two: the method changed, the loop keeps the
    // engine's IL
    [TestCase(2, true, 4, 1)]
    [TestCase(0, true, 4, 1)]
    [TestCase(1, false, 4, 1)]
    [TestCase(1, true, 5, 1)]
    [TestCase(1, true, 4, 0)]
    [TestCase(1, true, 4, 2)]
    public void AnyOtherShapeKeepsTheEnginesLoop(int loopSites, bool sort, int loopLocal, int reads)
    {
        var body = LoopBody(loopSites, sort, loopLocal, reads);
        var result = ChunkBudget.Rewrite(body);
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Capped, Is.False);
            Assert.That(result.Count(code => code.Calls(Cap)), Is.Zero);
            Assert.That(result, Has.Count.EqualTo(body.Count));
        });
    }

    // On the game's own OnBeforeFrame: one cap, right between the loop condition's ldloc of num3 and its backward blt, after Sort()
    [Test]
    public void TheCapLandsInOnBeforeFramesLoopCondition()
    {
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        var result = ChunkBudget.Rewrite(PatchProcessor.GetOriginalInstructions(frame));
        var (at, sorted) = (result.FindIndex(code => code.Calls(Cap)), result.FindIndex(code => code.Calls(Sort)));
        var stored = result.FindIndex(result.FindIndex(code => code.Calls(Limiter)), code => code.IsStloc());
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Capped, Is.True);
            Assert.That(result.Count(code => code.Calls(Cap)), Is.EqualTo(1));
            Assert.That(at, Is.GreaterThan(sorted));
            Assert.That(result[at - 1].IsLdloc() && result[at - 1].LocalIndex() == result[stored].LocalIndex(), Is.True,
                "the cap takes num3, the local the limiter expression is stored into");
            Assert.That(result[at + 1].opcode == OpCodes.Blt || result[at + 1].opcode == OpCodes.Blt_S, Is.True);
            var target = (Label)result[at + 1].operand;
            var into = result.FindIndex(code => code.labels.Contains(target));
            Assert.That(into, Is.InRange(sorted + 1, at - 1), "the branch goes back into the loop body");
        });
    }

    // Uncapped, however long the frame runs, and a cap that has not run out keep the engine's budget
    [TestCase(ChunkBudget.Uncapped, 3)]
    [TestCase(ChunkBudget.MaxCapMillis, 0)]
    public void TheEnginesBudgetStandsUntilACapRunsOut(int cap, int spin)
    {
        ChunkBudget.CapMillis = cap;
        ChunkBudget.Begin();
        Busy.Spin(spin);
        Assert.That((ChunkBudget.Cap(1000), ChunkBudget.Cap(1000), ChunkBudget.Cap(1000)),
            Is.EqualTo((1000, 1000, 1000)));
    }

    // The deadline can be gone before the loop starts – the priority queue counts against it – and one chunk still goes through
    [Test]
    public void TheFirstChunkAlwaysGoesAndThenTheCapEndsTheLoop()
    {
        (ChunkBudget.CapMillis, Counting.Hud) = (1, true);
        var hits = ChunkBudget.CapHits;
        ChunkBudget.Begin();
        Busy.Spin(3);
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(1000), "the test in front of the first chunk");
            Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(int.MinValue),
                "uploaded < int.MinValue is false: the loop ends");
            Assert.That(ChunkBudget.CapHits - hits, Is.EqualTo(1));
        });
        ChunkBudget.Begin();
        Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(1000), "the next frame starts over");
    }

    // A burst of priority chunks past the ceiling: the first went, the rest waits for the next frame, which the normal queue leaves
    // to it; the engine's flag asks for the rest again
    [Test]
    public void APriorityBurstPastTheCeilingGoesOnNextFrameAheadOfTheNormalQueue()
    {
        (ChunkBudget.CapMillis, Counting.Hud) = (1, true);
        var cuts = ChunkBudget.PriorityCuts;
        var manager = (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
        ChunkBudget.Begin();
        Busy.Spin(ChunkBudget.CeilingMillis + 2);
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Priority(5), Is.EqualTo(5), "the test in front of the first priority chunk");
            Assert.That(ChunkBudget.Priority(4), Is.Zero, "past the ceiling the loop ends");
            Assert.That(ChunkBudget.PriorityCuts - cuts, Is.EqualTo(1));
            Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(int.MinValue), "not even one normal chunk");
        });
        ChunkBudget.End(manager);
        Assert.That(AccessTools.FieldRefAccess<ChunkTesselatorManager, bool>("processPrioQueue")(manager), Is.True);

        ChunkBudget.Begin();
        Assert.Multiple(() =>
        {
            Assert.That(ChunkBudget.Priority(4), Is.EqualTo(4), "the next frame starts with the rest");
            Assert.That(ChunkBudget.Priority(3), Is.EqualTo(3), "within the ceiling");
            Assert.That(ChunkBudget.Cap(1000), Is.EqualTo(1000));
        });
    }

    // A frame within the ceiling: the priority queue is the engine's, all of it in the frame, and the flag stays as the engine
    // left it
    [Test]
    public void WithinTheCeilingThePriorityQueueGoesWhole()
    {
        var manager = (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
        ChunkBudget.Begin();
        int[] counts = [3, 2, 1, 0];
        Assert.That(counts.Select(ChunkBudget.Priority), Is.EqualTo(counts));
        ChunkBudget.End(manager);
        Assert.That(AccessTools.FieldRefAccess<ChunkTesselatorManager, bool>("processPrioQueue")(manager), Is.False);
    }

    // The ceiling never ends the priority queue before the cap in force would: a boost raises it too
    [Test]
    public void TheCeilingIsNeverBelowTheCapInForce()
    {
        (ChunkBudget.CapMillis, RuntimeStats.chunksAwaitingPooling) = (ChunkBudget.CeilingMillis + 4, 0);
        ChunkBudget.Begin();
        Busy.Spin(ChunkBudget.CeilingMillis + 1);
        Assert.That((ChunkBudget.Priority(3), ChunkBudget.Priority(2)), Is.EqualTo((3, 2)), "past the ceiling, the cap stands");
    }

    // A frame's chunk work, OnBeforeFrame and what Vulkan's adoption added, booked against the cap at the next frame's start
    [Test]
    public void AFramePastTheCapIsCountedWithItsExcess()
    {
        (ChunkBudget.CapMillis, Counting.Hud) = (3, true);
        var (over, ticks) = (ChunkBudget.Over, ChunkBudget.OverTicks);
        var ms = Stopwatch.Frequency / 1000;
        ChunkBudget.Begin();
        ChunkBudget.Spent(2 * ms);
        ChunkBudget.Begin();
        Assert.That(ChunkBudget.Over - over, Is.Zero, "2 ms is within 3");
        ChunkBudget.Spent(2 * ms);
        ChunkBudget.Spent(3 * ms);
        ChunkBudget.Begin();
        Assert.That((ChunkBudget.Over - over, ChunkBudget.OverTicks - ticks), Is.EqualTo((1L, 2 * ms)));
        ChunkBudget.CapMillis = ChunkBudget.Uncapped;
        ChunkBudget.Begin();
        ChunkBudget.Spent(30 * ms);
        ChunkBudget.Begin();
        Assert.That(ChunkBudget.Over - over, Is.EqualTo(1), "no cap, nothing over it");
    }

    // On the game's own OnBeforeFrame: the gate takes the priority queue's count in the priority loop's condition, before the lock
    // of the normal queue, and the cap goes where it went
    [Test]
    public void TheGateLandsInThePriorityLoopsCondition()
    {
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        var result = ChunkBudget.Rewrite(PatchProcessor.GetOriginalInstructions(frame));
        var (at, cap, sorted) = (result.FindIndex(code => code.Calls(Gate)), result.FindIndex(code => code.Calls(Cap)),
            result.FindIndex(code => code.Calls(Sort)));
        Assert.Multiple(() =>
        {
            Assert.That((ChunkBudget.Capped, ChunkBudget.Ceiled), Is.EqualTo((true, true)));
            Assert.That(result.Count(code => code.Calls(Gate)), Is.EqualTo(1));
            Assert.That(at, Is.LessThan(sorted));
            Assert.That(result[at - 1].Calls(PriorityCount), Is.True, "the gate takes the queue's count");
            Assert.That(result[at - 2].operand, Is.EqualTo(AccessTools.Field(typeof(ChunkTesselatorManager),
                "tessChunksQueuePriority")));
            Assert.That((result[at + 1].opcode, result[at + 2].opcode), Is.EqualTo((OpCodes.Ldc_I4_0, OpCodes.Bgt)));
            var into = result.FindIndex(code => code.labels.Contains((Label)result[at + 2].operand));
            Assert.That(into, Is.LessThan(at), "the branch goes back into the loop body");
            Assert.That(cap, Is.GreaterThan(sorted));
        });
    }

    [Test]
    public void TheFingerprintGuardsTheInstall()
    {
        using var harmony = new TestHarmony("komet-test-chunkbudget-shape");
        ChunkBudget.Install(harmony, null, ChunkBudget.Fingerprint ^ 1);
        var frame = AccessTools.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame));
        Assert.Multiple(() =>
        {
            Assert.That((ChunkBudget.Capped, ChunkBudget.Ceiled), Is.EqualTo((false, false)));
            Assert.That(Harmony.GetPatchInfo(frame)?.Owners ?? [], Does.Not.Contain("komet-test-chunkbudget-shape"));
        });
    }
}

// The mirror behind the patched ClientWorldMap.GetChunk follows the engine's dictionary through loads and unloads. A chunk that is not
// loaded is answered on the main thread without chunksLock, but only while the mirror holds every chunk the dictionary does: a chunk
// the mirror never saw sends the lookup back to the engine, and so does any other thread.
public sealed class ChunkLookupTests
{
    private static readonly MethodInfo Loaded = AccessTools.Method(typeof(ChunkLookup), "Loaded");
    private static readonly MethodInfo Unloading = AccessTools.Method(typeof(ChunkLookup), "Unloading");
    private int _mainThread;
    private ChunkRig _rig = null!;
    private TestHarmony _harmony = null!;
    private Dictionary<long, ClientChunk> _chunks = null!;
    private ClientChunk _chunk = null!;

    [SetUp]
    public void Install()
    {
        (_mainThread, _rig) = (RuntimeEnv.MainThreadId, new ChunkRig());
        (_chunks, _chunk) = ((Dictionary<long, ClientChunk>)ChunkRig.Get(_rig.Map, "chunks"),
            ClientChunk.CreateNew(_rig.Pool));
        _harmony = new TestHarmony("komet-test-chunklookup");
        ChunkLookup.Install(_harmony);
    }

    [TearDown]
    public void Uninstall()
    {
        _harmony.Dispose();
        ChunkLookup.Clear();
        _rig.Dispose();
        RuntimeEnv.MainThreadId = _mainThread;
    }

    // What loadChunkMT inserts, then its postfix stores
    private void Load(int x, int z)
    {
        _chunks[ChunkRig.Key(x, 0, z)] = _chunk;
        _ = Loaded.Invoke(null, [_rig.Map, new Packet_ServerChunk { X = x, Z = z }, _chunk]);
    }

    // More chunks in one unload packet than the old bound of 8192: the server sends a tick's unloads in one packet, however many
    [Test]
    public void AnUnloadPacketLeavesNoChunkBehind()
    {
        const int n = 10000;
        int[] xs = [.. Enumerable.Range(0, n).Select(i => i % 100)];
        int[] zs = [.. Enumerable.Range(0, n).Select(i => i / 100)];
        for (var i = 0; i < n; i++) Load(xs[i], zs[i]);
        Assert.That(_rig.Map.GetChunk(ChunkRig.Key(99, 0, 99)), Is.SameAs(_chunk));
        var packet = new Packet_UnloadServerChunk();
        (packet.X, packet.XCount, packet.Y, packet.YCount, packet.Z, packet.ZCount) = (xs, n, new int[n], n, zs, n);
        // the prefix, then the engine
        _ = Unloading.Invoke(null, [_rig.Game, new Packet_Server { Id = 11, UnloadChunk = packet }]);
        _chunks.Clear();
        var left = Enumerable.Range(0, n).Count(i => _rig.Map.GetChunk(ChunkRig.Key(xs[i], 0, zs[i])) is not null);
        Assert.That(left, Is.Zero, "an unloaded chunk was still answered from the mirror");
    }

    [Test]
    [Category("Slow")]
    public void AnAbsentChunkIsAnsweredOnlyFromAWholeMirror()
    {
        var gate = ChunkRig.Get(_rig.Map, "chunksLock");
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        for (var x = 0; x < 3; x++) Load(x, 0);
        var (absent, unseen) = (ChunkRig.Key(3, 0, 3), ChunkRig.Key(2, 0, 2));
        Assert.That(Held(gate, () => _rig.Map.GetChunk(absent)), Is.Null, "a whole mirror answers an absent chunk");
        _chunks[unseen] = _chunk; // inserted without the mirror seeing it
        Assert.That(Held(gate, () => _rig.Map.GetChunk(unseen)), Is.EqualTo("waited"),
            "a mirror short of a chunk sends the lookup to the engine");
        Assert.That(_rig.Map.GetChunk(unseen), Is.SameAs(_chunk));
        _ = _chunks.Remove(unseen);
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId + 1;
        Assert.That(Held(gate, () => _rig.Map.GetChunk(absent)), Is.EqualTo("waited"),
            "off the main thread the engine answers");
    }

    // The lookup's answer, made on this thread while another one holds chunksLock for a while; "waited" when it had to wait for it
    private static object? Held(object gate, Func<object?> lookup)
    {
        using var taken = new ManualResetEventSlim();
        var released = 0;
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                taken.Set();
                Thread.Sleep(300);
                Volatile.Write(ref released, 1);
            }
        });
        holder.Start();
        taken.Wait();
        var answer = lookup();
        var waited = Volatile.Read(ref released) == 1;
        holder.Join();
        return waited ? "waited" : answer;
    }
}

// The rewrite runs against the game's real IL: installing it makes Harmony compile the replacement, so a malformed rewrite fails here
// instead of in a world, and a game update that changes the method's shape turns the feature off here first.
public sealed class ChunkThreadClosureTests
{
    [Test]
    public void ChunkThreadClosureInstalls()
    {
        using var harmony = new TestHarmony("komet-test-chunkthreadclosure");
        ChunkThreadClosure.Install(harmony);
        Assert.That(ChunkThreadClosure.Rewritten, Is.True, "the closure prologue or its uses no longer match");
    }

    // The closure is built only where the delegate needs it, and nothing reads chunkRequest back out of it any more
    [Test]
    public void ChunkThreadClosureIsBuiltOnlyForTheDelegate()
    {
        var target = ChunkThreadClosure.Target();
        Assert.That(target, Is.Not.Null);
        var code = ChunkThreadClosure.Rewrite(PatchProcessor.GetOriginalInstructions(target));
        var builds = code.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.opcode == OpCodes.Newobj &&
                           pair.instruction.operand is ConstructorInfo { DeclaringType.Name: var name } &&
                           name.Contains("DisplayClass", StringComparison.Ordinal))
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(ChunkThreadClosure.Rewritten, Is.True);
            Assert.That(builds, Has.Count.EqualTo(1));
            Assert.That(builds[0].index, Is.GreaterThan(100), "the closure is still built in the prologue");
            Assert.That(code[builds[0].index + 7].opcode, Is.EqualTo(OpCodes.Ldftn),
                "the fresh closure must go straight into the delegate");
            Assert.That(code.Count(instruction => instruction.operand is FieldInfo { Name: "chunkRequest" } &&
                instruction.opcode == OpCodes.Ldfld), Is.Zero);
        });
    }
}
