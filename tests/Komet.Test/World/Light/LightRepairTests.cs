using Komet.World;

namespace Komet.Test.World;

// Golden test against the engine. The reference lights LightWorld with PlaceBlockLight for every source, then every column's sunlight
// by the sequence FullRelight runs for a whole column (FullRelight itself cannot serve: it places the sources again at twice their
// chunk's offset, pinned below). Block light the engine blends from overlapping sources in the order they were placed (a hue, and at
// the edge of a reach a step, can differ with another order), so the whole light is compared with the intact world after the same
// sources were placed again in the same order.
[NonParallelizable]
public sealed class LightRepairTests
{
    private const int Seed = 5;

    // Corner with lava, the glass wall's column, the pond with its light under water, the light cluster on the chunk corner
    private static readonly (int X, int Z)[] Broken = [(0, 3), (1, 1), (3, 0), (0, 0)];

    // The chunks of column 1, 1, which the repairs report
    private static readonly string[] Reported = ["1,0,1", "1,1,1", "1,2,1"];

    private static readonly BlockPos Low = new(0, 0, 0);
    private static readonly BlockPos High = new(LightWorld.SizeX - 1, LightWorld.SizeY - 1, LightWorld.SizeZ - 1);

    private int[][] _reference = [];

    [OneTimeSetUp]
    public void Reference() => _reference = Lit().Light();

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Waiting")]
    private static extern ref Queue<(int Dimension, int X, int Z)> Waiting(
        [UnsafeAccessorType("Komet.World.LightRepair, Komet")] object? repair);

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-lightrepair");
        LightRepair.Install(harmony, new QuietLogger());
        var seams = LightRepair.Seams();
        Assert.Multiple(() =>
        {
            Assert.That(LightRepair.Matched, Is.True, "the load, the illuminator or the tick is not the body verified");
            Assert.That(Harmony.GetPatchInfo(seams[0])?.Prefixes.Select(p => p.owner), Is.EqualTo([harmony.Id]));
            Assert.That(Harmony.GetPatchInfo(seams[0])?.Postfixes.Select(p => p.owner), Is.EqualTo([harmony.Id]));
            Assert.That(Harmony.GetPatchInfo(seams[1])?.Prefixes.Select(p => p.owner), Is.EqualTo([harmony.Id]),
                "FullRelight");
            Assert.That(Harmony.GetPatchInfo(seams[6])?.Postfixes.Select(p => p.owner), Is.EqualTo([harmony.Id]),
                "the sweep's tick");
        });
    }

    [TestCaseSource(nameof(Broken))]
    [Category("Slow")]
    public void ARepairedColumnGetsTheLightTheEngineGivesTheIntactWorld((int X, int Z) column)
    {
        var world = Lit();
        var chunks = Column(world, column.X, column.Z);
        var rain = Rain(world, column.X, column.Z);
        var lost = Lose(chunks, rain);
        var broken = LightRepair.Broken(chunks, rain);
        _ = LightRepair.Sunlight(world.Illuminator, chunks, column.X, column.Z, 0);
        _ = LightRepair.BlockLight(world.Illuminator, world, column.X, column.Z, broken, 0);
        var intact = Lit();
        _ = LightRepair.BlockLight(intact.Illuminator, intact, column.X, column.Z, broken, 0);
        var (found, expected) = (world.Light(), intact.Light());
        Assert.Multiple(() =>
        {
            Assert.That(broken, Has.Length.EqualTo(lost).And.Length.GreaterThan(0));
            Assert.That(Differences(Sun(found), Sun(_reference)), Is.Empty, "sunlight that is not the reference's");
            Assert.That(Differences(found, expected), Is.Empty, "light that is not the intact world's");
        });
    }

    // The sweep lights a loaded column that lost its light as the load does, reports every chunk it changed, and leaves a column
    // that has its light alone
    [Test]
    [Category("Slow")]
    public void TheSweepLightsALoadedColumnThatLostItsLight()
    {
        var world = Lit();
        var rain = Rain(world, 1, 1);
        var lost = Lose(Column(world, 1, 1), rain);
        var broken = LightRepair.Broken(Column(world, 1, 1), rain);
        var changed = new List<string>();
        var untouched = LightRepair.Repair(world.Illuminator, world, 2, 2, Rain(world, 2, 2), (_, _, _) => { });
        var lit = LightRepair.Repair(world.Illuminator, world, 1, 1, rain, (x, y, z) => changed.Add($"{x},{y},{z}"));
        var intact = Lit();
        _ = LightRepair.BlockLight(intact.Illuminator, intact, 1, 1, broken, 0);
        var found = world.Light();
        Assert.Multiple(() =>
        {
            Assert.That(untouched, Is.Zero, "a column with its light");
            Assert.That(lit, Is.EqualTo(lost).And.GreaterThan(0));
            Assert.That(Differences(Sun(found), Sun(_reference)), Is.Empty, "sunlight that is not the reference's");
            Assert.That(Differences(found, intact.Light()), Is.Empty, "light that is not the intact world's");
            Assert.That(changed, Is.SupersetOf(Reported), "the column reported");
        });
    }

    // Without any light, a chunk reaching the sky is broken and one wholly under the rain height is not. With light none is. Packed,
    // the empty arrays CompressInto writes for no light count as none, and a column with a chunk missing is left alone.
    [Test]
    [Category("Slow")]
    public void OnlyAChunkThatReachesTheSkyWithoutLightIsBroken()
    {
        var world = Lit();
        var chunks = Column(world, 2, 2);
        var rain = new ushort[32 * 32];
        Array.Fill(rain, (ushort)40); // chunk 0 (0-31) lies under it, 1 and 2 reach the sky
        chunks[0].Lighting.ClearLight();
        chunks[2].Lighting.ClearLight();
        Assert.Multiple(() =>
        {
            Assert.That(LightRepair.Broken(chunks, rain), Is.EqualTo([2]));
            Assert.That(LightRepair.Broken(chunks, null), Is.EqualTo([0, 2]),
                "without a rain map every chunk reaches it");
            Assert.That(LightRepair.Broken([chunks[0], null!, chunks[2]], rain), Is.Empty, "a chunk missing");
            Assert.That(LightRepair.Missing(new PackedChunk([], [1])), Is.True, "packed without light");
            Assert.That(LightRepair.Missing(new PackedChunk([1], [])), Is.True, "packed without its palette");
            Assert.That(LightRepair.Missing(new PackedChunk(null, null)), Is.True, "packed, no arrays");
            Assert.That(LightRepair.Missing(new PackedChunk([1, 2], [3])), Is.False, "packed with light");
            Assert.That(LightRepair.Missing(chunks[1]), Is.False, "unpacked with light");
        });
    }

    [TestCase(40, 40, 40, 1, 1, 1, 0)]
    [TestCase(31, 40, 40, 1, 1, 1, 1)]
    [TestCase(70, 20, 40, 1, 1, 1, 7 + 12)]
    public void TheReachOfASourceIsItsStepsToTheChunk(int x, int y, int z, int cx, int cy, int cz, int reach)
    {
        Assert.That(LightRepair.Reach(new BlockPos(x, y, z), cx, cy, cz), Is.EqualTo(reach));
    }

    // Over a world loaded whole the relight is the engine's but for its sources: the same sunlight, every source where it is and
    // none added, and the light of the intact world after every source was placed again in the order the relight places them
    [Test]
    [Category("Slow")]
    public void TheRelightGivesTheEnginesSunlightAndPlacesEverySourceWhereItIs()
    {
        var world = Lit();
        var (before, order) = (Sources(world), Placements(world));
        var relit = LightRepair.FullRelight(world.Illuminator, Low, High);
        var engine = Lit();
        engine.Illuminator.FullRelight(Low, High);
        var intact = Lit();
        foreach (var (hsv, x, y, z) in order) _ = intact.Illuminator.PlaceBlockLight(hsv, x, y, z);
        var found = world.Light();
        Assert.Multiple(() =>
        {
            Assert.That(relit, Is.True);
            Assert.That(Sources(world), Is.EqualTo(before), "a light source added or moved");
            Assert.That(Differences(Sun(found), Sun(_reference)), Is.Empty, "sunlight that is not the reference's");
            Assert.That(Differences(Sun(found), Sun(engine.Light())), Is.Empty, "sunlight that is not the engine's");
            Assert.That(Differences(found, intact.Light()), Is.Empty, "light that is not the intact world's");
        });
    }

    // A column loaded in part when the relight runs keeps its light - the engine's FullRelight leaves it none - and waits; while it is
    // still in part it waits on, and once loaded whole it is relit: its light cleared, even of a chunk packed meanwhile, then the
    // reference's sunlight, and the intact world's light after its sources were placed again
    [Test]
    [Category("Slow")]
    public void AColumnLoadedInPartKeepsItsLightAndIsRelitOnceWhole()
    {
        var (world, engine) = (Lit(), Lit());
        _ = world.Unloaded.Add((1, 2, 1));
        _ = engine.Unloaded.Add((1, 2, 1));
        var relit = LightRepair.FullRelight(world.Illuminator, Low, High);
        engine.Illuminator.FullRelight(Low, High);
        var (kept, dark, waiting) = (Sun(world.Light()), Sun(engine.Light()), Waiting(null).Count);
        var inPart = LightRepair.RelightWaiting(world.Illuminator, world, 2, (_, _, _) => { });
        var waitingOn = Waiting(null).Count;
        world.Unloaded.Clear();
        var packed = Pack((WorldChunk)world.GetChunk(1, 1, 1));
        var changed = new List<string>();
        var whole = LightRepair.RelightWaiting(world.Illuminator, world, 2, (x, y, z) => changed.Add($"{x},{y},{z}"));
        var intact = Lit();
        _ = LightRepair.BlockLight(intact.Illuminator, intact, 1, 1, [0, 1, 2], 0);
        var (found, expected) = (world.Light(), intact.Light());
        int[] part = [Index(1, 0, 1), Index(1, 1, 1)];
        var sun = Sun(_reference);
        Assert.Multiple(() =>
        {
            Assert.That(relit, Is.True);
            Assert.That(part.Sum(c => sun[c].Count(value => value > 0)), Is.GreaterThan(0), "no sunlight to keep");
            Assert.That(part.Select(c => Differences([kept[c]], [sun[c]])), Is.All.Empty, "the part's sunlight");
            Assert.That(part.Sum(c => dark[c].Count(value => value > 0)), Is.Zero,
                "the engine's FullRelight left it sunlight");
            Assert.That((waiting, inPart, waitingOn), Is.EqualTo((1, 0, 1)), "waiting, relit in part, waiting on");
            Assert.That((packed, whole, Waiting(null).Count), Is.EqualTo((true, 1, 0)),
                "packed, relit whole, left");
            Assert.That(Differences(Sun(found), sun), Is.Empty, "sunlight that is not the reference's");
            Assert.That(Differences([.. Enumerable.Range(0, 3).Select(y => found[Index(1, y, 1)])],
                [.. Enumerable.Range(0, 3).Select(y => expected[Index(1, y, 1)])]), Is.Empty, "the column's light");
            Assert.That(changed, Is.SupersetOf(Reported), "the column reported");
        });
    }

    // Why the reference is not FullRelight's: it places every light source again at its chunk's offset plus the position within the
    // world, a source in a chunk off the origin twice as far out - a light source where there is none, or none at all
    [Test]
    [Category("Slow")]
    public void TheEnginesFullRelightPlacesLightSourcesAtTwiceTheirChunksOffset()
    {
        var world = Lit();
        var before = Sources(world);
        world.Illuminator.FullRelight(Low, High);
        var added = Sources(world).Except(before).ToList();
        Assert.That(added, Is.Not.Empty.And.Contains("2,2,2:13312"), "a source of chunk 1,1,1 placed in chunk 2,2,2");
    }

    // The chunks of a column that reach the sky lose their light, as a save left by a relight; how many
    private static int Lose(IWorldChunk[] chunks, ushort[] rain)
    {
        var lost = 0;
        for (var y = 0; y < chunks.Length; y++)
        {
            // wholly below the rain height: its light may be missing rightly
            if (rain.Min() + 1 > y * 32 + 31) continue;
            chunks[y].Lighting.ClearLight();
            lost++;
        }

        return lost;
    }

    // Packed as the server packs a chunk it has not touched for a while: its data compressed, its ChunkData freed
    private static bool Pack(WorldChunk chunk)
    {
        chunk.Unpack(); // marks it changed, so that Pack compresses it
        chunk.Pack();
        return chunk.TryCommitPackAndFree(0) && chunk.IsPacked() && chunk.Lighting is null;
    }

    private static List<string> Differences(int[][] found, int[][] expected)
    {
        var differences = new List<string>();
        for (var c = 0; c < found.Length; c++)
        {
            for (var i = 0; i < found[c].Length && differences.Count < 12; i++)
                if (found[c][i] != expected[c][i])
                    differences.Add($"{c}:{i} {expected[c][i]} -> {found[c][i]}");
        }

        return differences;
    }

    // The sunlight of every cell: the low five bits
    private static int[][] Sun(int[][] light) =>
        [.. light.Select(chunk => chunk.Select(value => value & 0x1F).ToArray())];

    // LightWorld with its light sources placed, then the sunlight of every column, in the order FullRelight takes them
    private static LightWorld Lit()
    {
        var world = new LightWorld(LightWorld.MakeBlocks(), Seed) { Recording = false };
        world.LightUp();
        for (var x = 0; x < LightWorld.ChunksX; x++)
        {
            for (var z = 0; z < LightWorld.ChunksZ; z++)
                _ = LightRepair.Sunlight(world.Illuminator, Column(world, x, z), x, z, 0);
        }

        return world;
    }

    private static int Index(int x, int y, int z) => (y * LightWorld.ChunksZ + z) * LightWorld.ChunksX + x;

    // Every light source as FullRelight places it again: chunk by chunk, x before y before z, each where it is
    private static List<(byte[] Hsv, int X, int Y, int Z)> Placements(LightWorld world)
    {
        var placements = new List<(byte[] Hsv, int X, int Y, int Z)>();
        for (var i = 0; i < LightWorld.ChunksX * LightWorld.ChunksY * LightWorld.ChunksZ; i++)
        {
            var (x, y, z) = (i / (LightWorld.ChunksY * LightWorld.ChunksZ), i / LightWorld.ChunksZ % LightWorld.ChunksY,
                i % LightWorld.ChunksZ);
            var chunk = world.GetChunk(x, y, z);
            foreach (var at in chunk.LightPositions)
            {
                var pos = new BlockPos(x * 32 + at % 32, y * 32 + at / 1024, z * 32 + at / 32 % 32);
                placements.Add((world.Blocks[chunk.Data[at]].GetLightHsv(null!, pos), pos.X, pos.Y, pos.Z));
            }
        }

        return placements;
    }

    private static List<string> Sources(LightWorld world)
    {
        var sources = new List<string>();
        for (var i = 0; i < LightWorld.ChunksX * LightWorld.ChunksY * LightWorld.ChunksZ; i++)
        {
            var (x, y, z) = (i % LightWorld.ChunksX, i / LightWorld.ChunksX % LightWorld.ChunksY,
                i / (LightWorld.ChunksX * LightWorld.ChunksY));
            sources.AddRange(world.GetChunk(x, y, z).LightPositions.Select(p => $"{x},{y},{z}:{p}"));
        }

        return sources;
    }

    // Typed as the server's load hands its column over: an array of a chunk class, taken as IWorldChunk[] by covariance
    private static IWorldChunk[] Column(LightWorld world, int x, int z)
    {
        WorldChunk[] column =
            [.. Enumerable.Range(0, LightWorld.ChunksY).Select(y => (WorldChunk)world.GetChunk(x, y, z))];
        return column;
    }

    private static ushort[] Rain(LightWorld world, int cx, int cz)
    {
        var rain = new ushort[32 * 32];
        for (var x = 0; x < 32; x++)
        {
            for (var z = 0; z < 32; z++)
            {
                for (var y = LightWorld.SizeY - 1; y >= 0; y--)
                {
                    var (wx, wz) = (cx * 32 + x, cz * 32 + z);
                    if (world.BlockAt(wx, y, wz) == LightWorld.Air && world.Fluid(wx, y, wz) == 0) continue;
                    rain[z * 32 + x] = (ushort)y;
                    break;
                }
            }
        }

        return rain;
    }

    // A chunk as the server keeps it packed: no ChunkData, only the compressed arrays
    private sealed class PackedChunk : WorldChunk
    {
        public PackedChunk(byte[]? light, byte[]? lightSat) =>
            (lightCompressed, lightSatCompressed) = (light, lightSat);

        public override IMapChunk MapChunk => null!;
        public override HashSet<int> LightPositions { get; set; } = [];
        public override Dictionary<string, byte[]> ModData { get; set; } = [];
    }
}
