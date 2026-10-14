using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

internal sealed class NoMesh : MeshRef
{
    public override bool Initialized => true;
}

// A location type the engine never makes: List.Remove would call its own Equals, so Komet has to leave such a pool to the engine
internal sealed class ForeignLocation : ModelDataPoolLocation
{
    public override string ToString() => "foreign";
}

// Golden test: the engine's own MeshDataPool code drives one pool with Komet switched off, Komet drives its twin, and after every step
// both hold the same list in the same order, the same positions, and bit for bit the same UsedVertices and CurrentFragmentation. The
// steps are the engine's entry points, TryAdd and RemoveLocation, so squeezes, appends and the squeeze skip take their in-game routes.
[NonParallelizable]
public sealed class MeshPoolTests
{
    private const int Seeds = 12, Steps = 2500, MaxParts = 400, PoolVertices = 150_000, Huge = 100_000_000;
    private static readonly FieldInfo LocationsField = AccessTools.Field(typeof(MeshDataPool), "poolLocations");
    private static readonly FieldInfo IdField = AccessTools.Field(typeof(MeshDataPool), "poolId");
    private static readonly FieldInfo ModelField = AccessTools.Field(typeof(MeshDataPool), "modelRef");
    private static readonly FieldInfo VersionField = AccessTools.Field(typeof(List<ModelDataPoolLocation>), "_version");
    private static readonly MethodInfo Squeeze = AccessTools.Method(typeof(MeshDataPool), "TrySqueezeInbetween");
    private static readonly ICoreClientAPI Api = Answers.NullClient;
    private static readonly NoMesh Model = new(); // InsertAt only checks that the pool has one
    private Harmony? _harmony;

    [OneTimeSetUp]
    public void Patch()
    {
        _harmony = new Harmony("komet-test-meshpool");
        MeshPool.Install(_harmony);
        // FrustumSweepTests fills real pools earlier in this process and can leave TryAdd tier-1 compiled with the unpatched
        // TrySqueezeInbetween inlined, which no patch reaches. A patch on TryAdd regenerates it; the game installs before any pool fills.
        _ = _harmony.Patch(AccessTools.Method(typeof(MeshDataPool), "TryAdd"), postfix: Foreign.Postfix);
    }

    [OneTimeTearDown]
    public void Unpatch()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        (MeshPool.Enabled, Counting.Hud) = (true, false);
    }

    [TearDown]
    public void Defaults() => (MeshPool.Enabled, Counting.Hud) = (true, false);

    private static void Komet(bool on) => MeshPool.Enabled = on;

    private static MeshDataPool Pool(int id, int vertices = PoolVertices, int maxParts = MaxParts)
    {
        var pool = (MeshDataPool)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPool));
        LocationsField.SetValue(pool, new List<ModelDataPoolLocation>());
        IdField.SetValue(pool, id);
        ModelField.SetValue(pool, Model);
        (pool.MaxPartsPerPool, pool.VerticesPoolSize, pool.IndicesPoolSize) = (maxParts, vertices, vertices * 3 / 2);
        (pool.indicesStartsByte, pool.indicesSizes) = (new int[2 * maxParts], new int[maxParts]);
        return pool;
    }

    private static List<ModelDataPoolLocation> Locations(MeshDataPool pool) =>
        (List<ModelDataPoolLocation>)LocationsField.GetValue(pool)!;

    private static int Version(MeshDataPool pool) => (int)VersionField.GetValue(Locations(pool))!;

    private static MeshData Mesh(int vertices, int indices) => new(false)
    {
        VerticesCount = vertices, IndicesCount = indices, xyz = new float[3 * vertices], Indices = new int[indices]
    };

    // Mostly chunk-sized meshes in the engine's 2:3 ratio, some empty ones, some large, and some whose index count disagrees with the
    // vertex count, so that the widest index gap and the widest vertex gap sit at different places
    private static (int Vertices, int Indices) Size(Random r)
    {
        var v = r.Next(10) switch { 0 => 0, < 5 => r.Next(1, 400), < 9 => r.Next(400, 2500), _ => r.Next(2500, 9000) };
        return (v, r.Next(6) == 0 ? r.Next(0, 2 * v + 2) : v * 3 / 2);
    }

    private static ModelDataPoolLocation? Add(MeshDataPool pool, (int Vertices, int Indices) size, bool komet)
    {
        Komet(komet);
        return pool.TryAdd(Api, Mesh(size.Vertices, size.Indices), null, 0, default);
    }

    private static void Remove(MeshDataPool pool, int index, bool komet)
    {
        Komet(komet);
        pool.RemoveLocation(Locations(pool)[index]);
    }

    private static void AddBoth(MeshDataPool engine, MeshDataPool komet, (int Vertices, int Indices) size)
    {
        _ = Add(engine, size, false);
        _ = Add(komet, size, true);
    }

    private static void RemoveBoth(MeshDataPool engine, MeshDataPool komet, int index)
    {
        Remove(engine, index, false);
        Remove(komet, index, true);
    }

    private static string Describe(ModelDataPoolLocation? l) =>
        l == null ? "null" : $"[{l.IndicesStart}..{l.IndicesEnd}) [{l.VerticesStart}..{l.VerticesEnd})";

    // Assert.That per field and step would dominate the run time, so the comparison is plain code that fails with the first difference
    private static void AssertSame(MeshDataPool engine, MeshDataPool komet, string context)
    {
        var (a, b) = (Locations(engine), Locations(komet));
        var state =
            $"positions {engine.indicesPosition}/{engine.verticesPosition} vs {komet.indicesPosition}/{komet.verticesPosition}, " +
            $"used {engine.UsedVertices} vs {komet.UsedVertices}, fragmentation {engine.CurrentFragmentation:R} vs " +
            $"{komet.CurrentFragmentation:R}, count {a.Count} vs {b.Count}";
        if (engine.indicesPosition != komet.indicesPosition || engine.verticesPosition != komet.verticesPosition ||
            engine.UsedVertices != komet.UsedVertices || a.Count != b.Count ||
            BitConverter.SingleToInt32Bits(engine.CurrentFragmentation) !=
            BitConverter.SingleToInt32Bits(komet.CurrentFragmentation))
            Assert.Fail($"{context}: {state}");
        for (var i = 0; i < a.Count; i++)
            if (Describe(a[i]) != Describe(b[i]) || a[i].GetType() != b[i].GetType())
                Assert.Fail($"{context}: location {i} is {Describe(a[i])} vs {Describe(b[i])}");
    }

    // Mischief is everything that changes Komet's pool without Komet
    private static string Step(Random r, MeshDataPool engine, MeshDataPool komet, Tally tally, bool mischief)
    {
        var count = Locations(engine).Count;
        var range = mischief ? 100 : 94;
        var roll = count == 0 ? 0 : r.Next(range);
        if (roll is < 55 or >= 97)
        {
            var size = Size(r);
            // now and then Komet's pool goes the engine's way too, which leaves its state behind
            var engineOn = roll >= 97;
            var (before, version) = (count, Version(komet));
            var expected = Add(engine, size, false);
            var actual = Add(komet, size, !engineOn);
            if (Describe(expected) != Describe(actual))
                Assert.Fail($"add {size}: {Describe(expected)} vs {Describe(actual)}");
            if (expected == null) return $"add {size} refused";
            if (Version(komet) == version)
                Assert.Fail("an insert did not move List._version, FrustumSweep would not see it");
            if (ReferenceEquals(Locations(engine)[^1], expected)) tally.Appended++;
            else tally.Squeezed++;
            return $"add {size} at {Locations(engine).Count - before}";
        }

        if (roll < 92)
        {
            var index = r.Next(5) == 0 ? count - 1 : r.Next(count);
            if (index == count - 1) tally.Tails++;
            if (Locations(engine)[index].VerticesEnd == Locations(engine)[index].VerticesStart) tally.Empty++;
            var version = Version(komet);
            RemoveBoth(engine, komet, index);
            if (Version(komet) == version)
                Assert.Fail("a removal did not move List._version, FrustumSweep would not see it");
            tally.Removed++;
            return $"remove {index} of {count}";
        }

        if (roll < 94)
        {
            for (var left = count; left > 0; left--) RemoveBoth(engine, komet, r.Next(left));

            tally.Drains++;
            return $"drain {count}";
        }

        return BehindTheBack(r, engine, komet, tally);
    }

    // What another mod might do: change the list directly. The state must notice by List._version and count again.
    private static string BehindTheBack(Random r, MeshDataPool engine, MeshDataPool komet, Tally tally)
    {
        var count = Locations(engine).Count;
        var index = r.Next(count);
        var kind = r.Next(3);
        foreach (var pool in (MeshDataPool[])[engine, komet])
        {
            var list = Locations(pool);
            var old = list[index];
            if (kind == 0) list.RemoveAt(index);
            else
                list[index] = kind == 1
                    ? new ModelDataPoolLocation
                    {
                        PoolId = old.PoolId, IndicesStart = old.IndicesStart, IndicesEnd = old.IndicesStart,
                        VerticesStart = old.VerticesStart,
                        VerticesEnd = old.VerticesStart
                    } // shrunk to nothing: the gap behind it grows
                    : new ForeignLocation
                    {
                        PoolId = old.PoolId, IndicesStart = old.IndicesStart, IndicesEnd = old.IndicesEnd,
                        VerticesStart = old.VerticesStart,
                        VerticesEnd = old.VerticesEnd
                    };
        }

        tally.Behind++;
        if (kind == 2) tally.Foreign++;
        return $"behind the back {kind} at {index} of {count}";
    }

    private static Tally Run(int firstSeed, bool mischief)
    {
        var tally = new Tally();
        Counting.Hud = true;
        var recounts = MeshPool.Recounts;
        for (var seed = firstSeed; seed < firstSeed + Seeds; seed++)
        {
            var r = new Random(seed);
            var (engine, komet) = (Pool(seed % 3), Pool(seed % 3));
            for (var step = 0; step < Steps; step++)
            {
                var did = Step(r, engine, komet, tally, mischief);
                AssertSame(engine, komet, $"seed {seed} step {step} ({did})");
            }
        }

        tally.Recounts = MeshPool.Recounts - recounts;
        return tally;
    }

    [Test]
    public void EverySequenceLeavesThePoolAsTheEngineDoes()
    {
        var tally = Run(0, true);

        TestContext.Out.WriteLine(
            $"squeezed {tally.Squeezed}, appended {tally.Appended}, removed {tally.Removed} ({tally.Tails} tails, " +
            $"{tally.Empty} empty), drained {tally.Drains}, behind the back {tally.Behind} ({tally.Foreign} foreign)");
        Assert.Multiple(() =>
        {
            Assert.That(tally.Squeezed, Is.GreaterThan(500), "the sequences must exercise squeezes");
            Assert.That(tally.Tails, Is.GreaterThan(500));
            Assert.That(tally.Empty, Is.GreaterThan(100), "zero-size meshes share their start with a neighbour");
            Assert.That(tally.Drains, Is.GreaterThan(100), "emptying the pool resets the positions");
            Assert.That(tally.Foreign, Is.GreaterThan(50));
            Assert.That(MeshPool.Skips, Is.GreaterThan(1000), "the bounds must answer some squeezes on their own");
        });
    }

    // Left to itself, a pool is counted in full once, when Komet first sees it; every change after that is incremental
    [Test]
    public void WithoutOutsideChangesThePoolIsCountedOnce()
    {
        var tally = Run(100, false);
        TestContext.Out.WriteLine(
            $"squeezed {tally.Squeezed}, appended {tally.Appended}, removed {tally.Removed}, drained {tally.Drains}");
        Assert.Multiple(() =>
        {
            Assert.That(tally.Behind, Is.Zero);
            Assert.That(tally.Removed, Is.GreaterThan(5000));
            Assert.That(tally.Recounts, Is.EqualTo(Seeds), "one full pass per pool");
        });
    }

    // After a change Komet did not make, exactly one full pass, and the result is still the engine's
    [Test]
    public void AListChangedBehindTheStatesBackIsCountedOnceAgain()
    {
        var r = new Random(7);
        var (engine, komet) = (Pool(1), Pool(1));
        for (var i = 0; i < 200; i++)
        {
            var v = r.Next(100, 2000);
            AddBoth(engine, komet, (v, v * 3 / 2));
        }

        for (var i = 0; i < 40; i++) RemoveBoth(engine, komet, r.Next(Locations(komet).Count));

        AssertSame(engine, komet, "before");
        Counting.Hud = true;
        var recounts = MeshPool.Recounts;
        foreach (var pool in (MeshDataPool[])[engine, komet]) Locations(pool).RemoveAt(17);
        RemoveBoth(engine, komet, 3);
        AssertSame(engine, komet, "after the list changed behind the state's back");
        RemoveBoth(engine, komet, 50);
        AssertSame(engine, komet, "one more");
        Assert.That(MeshPool.Recounts - recounts, Is.EqualTo(1));
    }

    // List.Remove takes the first copy of a location. A list some other code put out of IndicesStart order, holding one location twice,
    // must not send the binary search to the later copy.
    [Test]
    public void AnUnsortedListLosesTheFirstCopyOfALocationAsTheEngineDoes()
    {
        var (engine, komet) = (Pool(1, Huge), Pool(1, Huge));
        foreach (var pool in (MeshDataPool[])[engine, komet])
        {
            var twice = new ModelDataPoolLocation
            { PoolId = 1, IndicesStart = 500, IndicesEnd = 600, VerticesStart = 500, VerticesEnd = 600 };
            var list = Locations(pool);
            list.Add(twice);
            list.Add(new ModelDataPoolLocation
            { PoolId = 1, IndicesStart = 0, IndicesEnd = 100, VerticesStart = 0, VerticesEnd = 100 });
            list.Add(twice);
            list.Add(new ModelDataPoolLocation
            { PoolId = 1, IndicesStart = 700, IndicesEnd = 800, VerticesStart = 700, VerticesEnd = 800 });
            (pool.verticesPosition, pool.indicesPosition) = (800, 800);
            pool.CalcFragmentation();
        }

        RemoveBoth(engine, komet, 0);
        AssertSame(engine, komet, "after removing the location held twice");
        Assert.That(Describe(Locations(komet)[0]), Is.EqualTo("[0..100) [0..100)"), "the first copy went");
    }

    private static (bool Sorted, bool Plain)? StateOf(MeshDataPool pool)
    {
        var table = AccessTools.Field(typeof(MeshPool), "States").GetValue(null)!;
        object?[] args = [pool, null];
        if (!(bool)AccessTools.Method(table.GetType(), "TryGetValue").Invoke(table, args)!) return null;
        var state = AccessTools.Inner(typeof(MeshPool), "State");
        return ((bool)AccessTools.Field(state, "Sorted").GetValue(args[1])!,
            (bool)AccessTools.Field(state, "Plain").GetValue(args[1])!);
    }

    // The engine's own inserts and removals keep the list in IndicesStart order, so Remove keeps bisecting: Find answers the same
    // unsorted, only by walking every location
    [Test]
    public void TheEnginesOrderKeepsTheBinarySearch()
    {
        var r = new Random(11);
        var pool = Pool(1);
        Counting.Hud = true;
        var (removed, recounts) = (MeshPool.Removed, MeshPool.Recounts);
        for (var step = 0; step < 3000; step++)
        {
            var count = Locations(pool).Count;
            if (count == 0 || r.Next(100) < 55) _ = Add(pool, Size(r), true);
            else Remove(pool, r.Next(count), true);
        }

        Assert.Multiple(() =>
        {
            Assert.That(MeshPool.Removed - removed, Is.GreaterThan(1000));
            Assert.That(MeshPool.Recounts - recounts, Is.EqualTo(1),
                "kept sorted by the updates, not by counting again");
            Assert.That(StateOf(pool), Is.EqualTo((Sorted: true, Plain: true)));
        });
    }

    // The first mesh in an empty pool is counted before TryAppend advances verticesPosition, which is still 0: UsedVertices stays 0
    [Test]
    public void TheFirstMeshKeepsTheEnginesStalePosition()
    {
        var (engine, komet) = (Pool(0), Pool(0));
        AddBoth(engine, komet, (1000, 1500));
        Assert.Multiple(() =>
        {
            Assert.That(komet.UsedVertices, Is.Zero);
            Assert.That(komet.verticesPosition, Is.EqualTo(1000));
        });
        AddBoth(engine, komet, (500, 750));
        AssertSame(engine, komet, "second mesh");
        Assert.That(komet.UsedVertices, Is.EqualTo(1500));
    }

    // A location of another pool, one the pool does not hold (a copy of one it does, with the same start) and null all throw exactly what
    // the engine throws, and leave the pool alone
    [Test]
    public void BadRemovalsThrowWhatTheEngineThrows()
    {
        var (engine, komet) = (Pool(2), Pool(2));
        foreach (var size in (int[])[300, 0, 0, 700, 1200]) AddBoth(engine, komet, (size, size * 3 / 2));

        System.Func<MeshDataPool, ModelDataPoolLocation?>[] bad =
        [
            _ => null,
            _ => new ModelDataPoolLocation { PoolId = 5 },
            pool => new ModelDataPoolLocation
            {
                PoolId = 2, IndicesStart = Locations(pool)[1].IndicesStart, IndicesEnd = Locations(pool)[1].IndicesEnd,
                VerticesStart = Locations(pool)[1].VerticesStart, VerticesEnd = Locations(pool)[1].VerticesEnd
            }
        ];
        foreach (var location in bad)
        {
            Komet(false);
            var expected = Assert.Catch(() => engine.RemoveLocation(location(engine)!));
            Komet(true);
            var actual = Assert.Catch(() => komet.RemoveLocation(location(komet)!));
            Assert.Multiple(() =>
            {
                Assert.That(actual.GetType(), Is.EqualTo(expected.GetType()));
                Assert.That(actual.Message, Is.EqualTo(expected.Message));
            });
            AssertSame(engine, komet, "after " + expected.Message);
        }
    }

    // A layout with a gap in front of every few locations, as the engine leaves it after removals
    private static MeshDataPool Layout(Random r, int id, int parts)
    {
        var pool = Pool(id, Huge, parts + 1);
        var list = Locations(pool);
        var (vertices, indices) = (0, 0);
        for (var i = 0; i < parts; i++)
        {
            if (r.Next(4) == 0) (vertices, indices) = (vertices + r.Next(1, 3000), indices + r.Next(1, 4500));
            var v = r.Next(0, 2500);
            var n = r.Next(3) == 0 ? r.Next(0, 4000) : v * 3 / 2;
            list.Add(new ModelDataPoolLocation
            {
                PoolId = id, IndicesStart = indices, IndicesEnd = indices + n, VerticesStart = vertices,
                VerticesEnd = vertices + v
            });
            (vertices, indices) = (vertices + v, indices + n);
        }

        (pool.verticesPosition, pool.indicesPosition) = (vertices, indices);
        pool.CalcFragmentation();
        return pool;
    }

    // The squeeze skip may only ever say null where the engine's own walk finds no gap, and it says so wherever the walk finds none
    [Test]
    public void TheSkipSaysNullExactlyWhereTheEnginesWalkFindsNoGap()
    {
        Counting.Hud = true;
        var skips = MeshPool.Skips;
        int fits = 0, misses = 0;
        for (var seed = 0; seed < 40; seed++)
        {
            var r = new Random(seed);
            var parts = r.Next(0, 300);
            for (var trial = 0; trial < 40; trial++)
            {
                var size = (Vertices: r.Next(0, 3200), Indices: r.Next(0, 4800));
                var komet = Layout(new Random(seed), 3, parts);
                var engine = Layout(new Random(seed), 3, parts);
                Komet(true);
                var skipped = MeshPool.Misses(komet, Mesh(size.Vertices, size.Indices));
                Komet(false);
                var fit = Squeeze.Invoke(engine, [Api, Mesh(size.Vertices, size.Indices), null, default(Sphere)]) !=
                          null;
                if (skipped == fit)
                    Assert.Fail(
                        $"seed {seed} trial {trial} {size}: skip says {skipped}, the engine's walk found a gap: {fit}");
                if (fit) fits++;
                else misses++;
            }
        }

        TestContext.Out.WriteLine($"fits {fits}, misses {misses}, answered by the bounds {MeshPool.Skips - skips}");
        Assert.Multiple(() =>
        {
            Assert.That(fits, Is.GreaterThan(200));
            Assert.That(misses, Is.GreaterThan(200));
            Assert.That(MeshPool.Skips - skips, Is.GreaterThan(50).And.LessThan(misses),
                "both the bounds and the walk have to answer");
        });
    }

    // A squeeze narrows the widest gap but leaves its bound standing; the next walk that finds nothing sets the bounds to the exact
    // widest gaps, so a mesh at least that wide is answered without a walk again
    [Test]
    public void AMissedWalkTightensTheBounds()
    {
        var pool = Pool(4, Huge);
        var list = Locations(pool);
        foreach (var (start, end) in ((int, int)[])[(100, 200), (300, 400), (1400, 1500), (1510, 1600)])
            list.Add(new ModelDataPoolLocation
            { PoolId = 4, IndicesStart = start, IndicesEnd = end, VerticesStart = start, VerticesEnd = end });
        (pool.verticesPosition, pool.indicesPosition) = (1600, 1600);
        pool.CalcFragmentation();
        Counting.Hud = true;
        var skips = MeshPool.Skips;
        Assert.Multiple(() =>
        {
            Assert.That(MeshPool.Misses(pool, Mesh(1000, 1000)), Is.True,
                "the widest gap is 1000, and a gap must be wider");
            Assert.That(MeshPool.Skips - skips, Is.EqualTo(1), "answered by the bounds from the first full pass");
            Assert.That(MeshPool.Misses(pool, Mesh(999, 999)), Is.False, "fits in front of the third location");
            Assert.That(MeshPool.Misses(pool, Mesh(999, 99)), Is.False);
            Assert.That(MeshPool.Misses(pool, Mesh(99, 999)), Is.False);
        });
        var squeezed = Add(pool, (900, 900), true);
        Assert.That(Describe(squeezed), Is.EqualTo("[400..1300) [400..1300)"), "squeezed into the widest gap");
        Assert.Multiple(() =>
        {
            Assert.That(MeshPool.Misses(pool, Mesh(500, 500)), Is.True, "the widest gap left is 100");
            Assert.That(MeshPool.Skips - skips, Is.EqualTo(1), "the bound was still 1000, so that took a walk");
            Assert.That(MeshPool.Misses(pool, Mesh(500, 500)), Is.True);
            Assert.That(MeshPool.Skips - skips, Is.EqualTo(2), "and the walk left the exact bound behind");
            Assert.That(MeshPool.Misses(pool, Mesh(99, 99)), Is.False, "99 still fits in front of the first location");
        });
    }

    // The vector rebase leaves the arrays exactly as InsertAt's scalar loops would, the tail past count untouched
    [Test]
    public void IndicesMatchTheEngineLoop([Range(0, 40)] int count)
    {
        var r = new Random(count);
        var delta = r.Next(1, 400000);
        var values = Enumerable.Range(0, count + 3).Select(_ => r.Next(0, 500000)).ToArray();
        var expected = values.ToArray();
        for (var i = 0; i < count; i++) expected[i] += delta;
        MeshPool.Shift(values, count, delta);
        Assert.That(values, Is.EqualTo(expected));
    }

    [Test]
    public void VerticesMatchTheEngineLoop([Range(0, 40)] int count)
    {
        var r = new Random(count);
        int dx = r.Next(-40000, 40000), dy = r.Next(-1024, 1024), dz = r.Next(-40000, 40000);
        var xyz = Enumerable.Range(0, 3 * count + 7).Select(_ => (float)(r.NextDouble() * 2048 - 1024)).ToArray();
        var expected = xyz.ToArray();
        for (var j = 0; j < count; j++)
        {
            expected[3 * j] += dx;
            expected[3 * j + 1] += dy;
            expected[3 * j + 2] += dz;
        }

        MeshPool.Shift(xyz, count, dx, dy, dz);
        Assert.That(xyz, Is.EqualTo(expected));
    }

    [Test]
    public void BadShiftArgumentsLeaveTheArraysAlone()
    {
        var xyz = new float[9];
        var indices = new int[3];
        MeshPool.Shift(xyz, 4, 1, 2, 3); // 12 floats needed, 9 there
        MeshPool.Shift(indices, 4, 1);
        Assert.Multiple(() =>
        {
            Assert.That(xyz, Is.All.Zero);
            Assert.That(indices, Is.All.Zero);
        });
    }

    private sealed class Tally
    {
        public long Recounts;
        public int Squeezed, Appended, Removed, Tails, Empty, Drains, Behind, Foreign;
    }
}
