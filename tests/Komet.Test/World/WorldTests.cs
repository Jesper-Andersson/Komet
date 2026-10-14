using Komet.Core;
using Komet.World;

namespace Komet.Test.World;

// The engine caches one interpolated climate map per region, ten deep, and evicts in insertion order. Past ten regions every lookup
// misses and allocates a megabyte, so the capacity has to follow the view distance rather than a constant.
public sealed class ClimateCacheTests
{
    private const int Region = 512; // MagicNum.ServerChunkSize * ChunkRegionSizeInChunks

    private static readonly FieldInfo Maps = AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMaps");
    private static readonly FieldInfo Capacity = AccessTools.Field(typeof(LimitedDictionary<long, int[]>), "capacity");

    [TestCase(Region, 256, ClimateCache.EngineCapacity)] // 2 per axis, below the engine's own ten
    [TestCase(Region, 512, ClimateCache.EngineCapacity)] // 3 per axis = 9, still below
    [TestCase(Region, 768, 16)]
    [TestCase(Region, 1024, 25)]
    [TestCase(Region, 1536, 49)] // the view distance this was found at
    [TestCase(Region, 1_000_000, ClimateCache.MaxCapacity)] // the memory ceiling
    [TestCase(0, 512, ClimateCache.EngineCapacity)] // nonsense falls back to the engine's ten
    [TestCase(512, 0, ClimateCache.EngineCapacity)]
    [TestCase(-1, -1, ClimateCache.EngineCapacity)]
    public void CapacityCoversEveryRegionInView(int region, int distance, int expected)
    {
        Assert.That(ClimateCache.CapacityFor(region, distance), Is.EqualTo(expected));
    }

    [TestCase(true, 1536, 49)]
    [TestCase(false, 1536, ClimateCache.EngineCapacity)]
    [TestCase(false, 0, ClimateCache.EngineCapacity)] // switched off the view distance is not read
    public void SwitchedOffItWantsTheEnginesTen(bool enabled, int distance, int expected)
    {
        Assert.That(ClimateCache.Wanted(enabled, Region, distance), Is.EqualTo(expected));
    }

    // The prefix is on the engine method and reaches the engine's private fields: the dictionary is swapped only for another capacity,
    // and back to the engine's ten when switched off
    [Test]
    public void InstallsAndSwapsTheEnginesDictionaryForAnotherCapacityOnly()
    {
        var load = AccessTools.Method(typeof(ClientWorldMap),
            nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread));
        var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
        AccessTools.Field(typeof(ClientWorldMap), "LerpedClimateMapsLock").SetValue(map, new object());
        Maps.SetValue(map, new LimitedDictionary<long, int[]>(ClimateCache.EngineCapacity));
        using var harmony = new TestHarmony("komet-test-climatecache");
        ClimateCache.Install(harmony);
        ClimateCache.Fit(map, ClimateCache.Wanted(true, Region, 1536));
        var sized = Maps.GetValue(map);
        ClimateCache.Fit(map, ClimateCache.Wanted(true, Region, 1536));
        var kept = Maps.GetValue(map);
        ClimateCache.Fit(map, ClimateCache.Wanted(false, Region, 1536));
        Assert.Multiple(() =>
        {
            Assert.That(Harmony.GetPatchInfo(load)?.Prefixes.Select(p => p.owner), Is.EqualTo([harmony.Id]));
            Assert.That(Capacity.GetValue(sized), Is.EqualTo(49));
            Assert.That(kept, Is.SameAs(sized), "the same capacity keeps the dictionary and its maps");
            Assert.That(Capacity.GetValue(Maps.GetValue(map)), Is.EqualTo(ClimateCache.EngineCapacity));
            Assert.That(ClimateCache.Capacity, Is.EqualTo(ClimateCache.EngineCapacity));
        });
    }
}

// The rewrite runs against the game's real IL: installing it makes Harmony compile the replacement, so a malformed rewrite fails here
// instead of in a world, and a game update that changes the method's shape turns the feature off here first.
public sealed class CloudTileScratchTests
{
    [OneTimeSetUp]
    public void LoadEssentials()
    {
        // Komet finds the cloud renderer by name, which only works once the game has loaded VSEssentials; here the test does it
        var essentials = Assembly.Load("VSEssentials");
        Assert.That(AccessTools.TypeByName("FluffyClouds.CloudRendererMap"), Is.Not.Null, essentials.FullName);
    }

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-cloudtilescratch");
        CloudTileScratch.Install(harmony);
        Assert.That(CloudTileScratch.Rewritten, Is.True,
            "UpdateCloudTilesOffThread no longer builds exactly one Vec3d");
    }

    // A vector the method could keep - stored in a static, or handed to anything but its fields and the weather readers - leaves the
    // engine's IL untouched
    [Test]
    public void AVectorThatCouldEscapeIsLeftAlone()
    {
        var (target, ctor) = (CloudTileScratch.Target()!, CloudTileScratch.Constructor());
        var store = PatchProcessor.GetOriginalInstructions(target)
            .FindIndex(c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor)) + 1;

        List<CodeInstruction> Variant(params CodeInstruction[] inserted)
        {
            var code = PatchProcessor.GetOriginalInstructions(target);
            code.InsertRange(store + 1, [new CodeInstruction(OpCodes.Ldloc, code[store].operand), .. inserted]);
            return code;
        }

        var stored =
            Variant(new CodeInstruction(OpCodes.Stsfld, AccessTools.Field(typeof(CloudTileScratch), "_scratch")));
        var cloned = Variant(
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Vec3d), nameof(Vec3d.Clone))),
            new CodeInstruction(OpCodes.Pop));
        Assert.Multiple(() =>
        {
            foreach (var (name, code) in new[] { ("stored", stored), ("handed on", cloned) })
            {
                var result = CloudTileScratch.Substitute(code);
                Assert.That(CloudTileScratch.Rewritten, Is.False, name);
                Assert.That(result.Count(c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor)), Is.EqualTo(1),
                    name);
            }
        });
    }

    [Test]
    public void ScratchIsReusedPerThread()
    {
        var first = CloudTileScratch.At(1, 2, 3);
        var second = CloudTileScratch.At(4, 5, 6);
        Vec3d? other = null;
        var thread = new Thread(() => other = CloudTileScratch.At(7, 8, 9));
        thread.Start();
        thread.Join();
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That((second.X, second.Y, second.Z), Is.EqualTo((4d, 5d, 6d)));
            Assert.That(other, Is.Not.SameAs(first), "the main thread and the cloud thread must not share the vector");
        });
    }
}

// Particle light on the render thread: it never waits for a chunk lock another thread holds and never unpacks a packed chunk,
// and a lock it takes is released once, also when the engine's read throws
public sealed class ParticleLightTests
{
    private const int Light = 0x123456;
    private long _reads, _busy, _packed; // the totals when the test began

    [SetUp]
    public void Clear()
    {
        ParticleLight.Forget();
        Counting.Hud = true;
        (_reads, _busy, _packed) = (ParticleLight.Reads, ParticleLight.Busy, ParticleLight.Packed);
    }

    [TearDown]
    public void Stop() => Counting.Hud = false;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-particlelight");
        ParticleLight.Install(harmony);
        Assert.That(ParticleLight.Installed, Is.True);
    }

    [Test]
    public void AFreeLockLetsTheEngineReadWhileHoldingIt()
    {
        var chunk = new TestChunk();
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(state, Is.SameAs(chunk));
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.True, "held across the original");
        });
        Assert.That(ParticleLight.Finalizer(null, state, Light), Is.Null);
        Assert.Multiple(() =>
        {
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.False, "released by the finalizer");
            Assert.That(OtherThreadCanEnter(chunk.Gate), Is.True);
            Assert.That(ParticleLight.Reads - _reads, Is.EqualTo(1));
        });
    }

    [Test]
    public void ABusyLockServesTheLastLightWithoutWaiting()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        using var held = HoldElsewhere(chunk);
        var result = 0;
        var clock = Stopwatch.StartNew();
        var engine = ParticleLight.Enter(chunk, ref result, out var state);
        Assert.Multiple(() =>
        {
            Assert.That(engine, Is.False);
            Assert.That(result, Is.EqualTo(Light));
            Assert.That(state, Is.Null);
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(100), "never waits");
            Assert.That(ParticleLight.Busy - _busy, Is.EqualTo(1));
        });
    }

    // TryEnter spins before it gives up, so a chunk found busy is left alone for the backoff, even once its lock is free again
    [Test]
    public void ABusyChunkIsNotTriedAgainWithinTheBackoff()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        var result = 0;
        using (HoldElsewhere(chunk))
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out _), Is.False);
        }

        result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.False, "still backing off");
            Assert.That((result, state), Is.EqualTo((Light, (WorldChunk?)null)));
            Assert.That(ParticleLight.Busy - _busy, Is.EqualTo(2));
        });
        _ = SpinWait.SpinUntil(() => false, ParticleLight.BackoffMs + 30); // let the backoff run out
        Assert.That(ParticleLight.Enter(chunk, ref result, out var taken), Is.True, "tried again after the backoff");
        _ = ParticleLight.Finalizer(null, taken, Light);
    }

    [Test]
    public void EachChunkServesItsOwnLight()
    {
        TestChunk shaded = new(), sunlit = new();
        Remember(shaded, 1);
        Remember(sunlit, Light);
        var result = 0;
        using var held = HoldElsewhere(shaded);
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(shaded, ref result, out _), Is.False);
            Assert.That(result, Is.EqualTo(1), "the chunk's own light, not the last one read anywhere");
        });
    }

    [Test]
    public void WithoutALastLightTheEngineWaitsAsBefore()
    {
        var chunk = new TestChunk();
        using var held = HoldElsewhere(chunk);
        var result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
            Assert.That(state, Is.Null, "nothing taken, nothing to release");
        });
    }

    [Test]
    public void APackedChunkStaysPacked()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        chunk.Unpack(); // marks potential changes, so Pack compresses rather than reuse data it never had
        chunk.TryPackAndCommit(0);
        Assert.That(chunk.IsPacked(), Is.True, "precondition");
        var result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.False);
            Assert.That(result, Is.EqualTo(Light));
            Assert.That(state, Is.Null);
            Assert.That(chunk.IsPacked(), Is.True, "not unpacked on the render thread");
            Assert.That(ParticleLight.Packed - _packed, Is.EqualTo(1));
        });
    }

    [Test]
    public void TheLockIsReleasedWhenTheEngineThrows()
    {
        var chunk = new TestChunk();
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        var thrown = new InvalidOperationException("engine");
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Finalizer(thrown, state, 0), Is.SameAs(thrown));
            Assert.That(Monitor.IsEntered(chunk.Gate), Is.False);
        });
        using var held = HoldElsewhere(chunk); // the failed read left no last light behind
        Assert.That(ParticleLight.Enter(chunk, ref result, out _), Is.True);
    }

    // Leaving the world forgets every chunk and the last light: the next read of a busy chunk waits as the first one did
    [Test]
    public void ForgetDropsEveryChunk()
    {
        var chunk = new TestChunk();
        Remember(chunk, Light);
        ParticleLight.Forget();
        using var held = HoldElsewhere(chunk);
        var result = 0;
        Assert.Multiple(() =>
        {
            Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
            Assert.That((result, state), Is.EqualTo((0, (WorldChunk?)null)));
        });
    }

    private static void Remember(TestChunk chunk, int light)
    {
        var result = 0;
        Assert.That(ParticleLight.Enter(chunk, ref result, out var state), Is.True);
        _ = ParticleLight.Finalizer(null, state, light);
    }

    private static bool OtherThreadCanEnter(object gate) => Task.Run(() =>
    {
        if (!Monitor.TryEnter(gate)) return false;
        Monitor.Exit(gate);
        return true;
    }).Result;

    // The chunk's lock held by another thread until disposed, the way the visibility calculation or the compressor holds it
    private static Holder HoldElsewhere(TestChunk chunk) => new Holder(chunk.Gate);

    private sealed class Holder : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(), _release = new();
        private readonly Thread _thread;

        public Holder(object gate)
        {
            _thread = new Thread(() =>
            {
                lock (gate)
                {
                    _entered.Set();
                    _release.Wait();
                }
            }) { IsBackground = true };
            _thread.Start();
            _entered.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private sealed class TestChunk : WorldChunk
    {
        private static readonly ChunkDataPool Pool = new(GlobalConstants.ChunkSize, null!);

        public TestChunk()
        {
            datapool = Pool;
            chunkdata = ChunkData.CreateNew(GlobalConstants.ChunkSize, Pool);
            MaybeBlocks = chunkdata;
        }

        public object Gate => PackUnpackLock(this);
        public override IMapChunk MapChunk => null!;
        public override HashSet<int> LightPositions { get; set; } = [];
        public override Dictionary<string, byte[]> ModData { get; set; } = [];

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packUnpackLock")]
        private static extern ref object PackUnpackLock(WorldChunk chunk);
    }
}
