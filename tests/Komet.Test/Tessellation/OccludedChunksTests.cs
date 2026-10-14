using Komet.Core;
using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// Golden tests against the engine's own tesselator: for every chunk the fast path would answer, ChunkTesselator.BeginProcessChunk
// finds no face to draw, and NowProcessChunk returns exactly what OccludedChunks.Apply hands out (0 vertices, the tesselator's empty
// part array for centre and edge, the same bounds, bit for bit), for full and edge-only passes. And for chunks built only of
// FaceCullMode.Default blocks the fast path's test agrees with the engine's face culling both ways: it takes every enclosed chunk and
// none that draws a face.
public sealed class OccludedChunksTests
{
    private const int X = 1, Y = 1, Z = 1;

    private static readonly (int Dx, int Dy, int Dz)[] Faces =
        [(0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0), (0, 1, 0), (0, -1, 0)];

    private static readonly string[] Corners =
        ["currentOceanityMapTL", "currentOceanityMapTR", "currentOceanityMapBL", "currentOceanityMapBR"];

    // The rig is internal to the tests, so a case carries the change's name and the change waits here
    private static readonly Dictionary<string, Action<ChunkRig>> Changed = [];

    [TearDown]
    public void Restore() => (OccludedChunks.Enabled, Counting.Hud) = (true, false);

    private static int Solid(int x, int y, int z) => ChunkRig.Stone;

    private static int Mixed(int x, int y, int z) => (x + y + z) % 3 == 0 ? ChunkRig.Granite : ChunkRig.Stone;

    // The chunk and its six face neighbours of stone (and the bottom ones: the chunk below the world's lowest is never there), in a
    // map region with climate and ocean maps
    private static ChunkRig Enclosed(int cy, System.Func<int, int, int, int>? center = null)
    {
        var rig = new ChunkRig();
        rig.AddRegion(X, Z, cy);
        _ = rig.Put(X, cy, Z, center ?? Solid);
        foreach (var (dx, dy, dz) in Faces)
            if (cy + dy >= 0)
                _ = rig.Put(X + dx, cy + dy, Z + dz, Mixed);
        return rig;
    }

    // What the previous pass left in the tesselator: another climate map and ocean corners, and a mini-dimension's tmpPos
    private static void Stale(ChunkRig rig)
    {
        ChunkRig.Set(rig.Tesselator, "currentClimateRegionMap", new int[1]);
        foreach (var corner in Corners) ChunkRig.Set(rig.Tesselator, corner, -7.5f);
        _ = ((BlockPos)ChunkRig.Get(rig.Tesselator, "tmpPos")).SetDimension(1);
    }

    // What a pass leaves in the tesselator that the next pass reads before it sets it
    private static (int[] Climate, float[] Ocean, int Dimension) Left(ChunkRig rig) =>
        ((int[])ChunkRig.Get(rig.Tesselator, "currentClimateRegionMap"),
            Corners.Select(c => (float)ChunkRig.Get(rig.Tesselator, c)).ToArray(),
            ((BlockPos)ChunkRig.Get(rig.Tesselator, "tmpPos")).dimension);

    // What the engine leaves in a TesselatedChunk against what the fast path leaves in another one for the same chunk, and what each
    // leaves in the tesselator for the passes after it, from the same stale start
    private static void AssertSameResult(ChunkRig rig, int cy, bool edgeOnly)
    {
        var engine = rig.Tess(X, cy, Z);
        var mine = rig.Tess(X, cy, Z);
        Stale(rig);
        var vertices = rig.Tesselator.NowProcessChunk(X, cy, Z, engine, edgeOnly);
        var engineLeft = Left(rig);
        Stale(rig);
        var answered = OccludedChunks.Apply(rig.Tesselator, X, cy, Z, mine, edgeOnly);
        var myLeft = Left(rig);
        Sphere a = ChunkRig.Bounds(engine), b = ChunkRig.Bounds(mine);
        Assert.Multiple(() =>
        {
            Assert.That((vertices, answered), Is.EqualTo((0, 0)));
            Assert.That(myLeft.Climate, Is.SameAs(engineLeft.Climate), "the climate map, from the map's cache");
            Assert.That(myLeft.Ocean.Select(BitConverter.SingleToInt32Bits),
                Is.EqualTo(engineLeft.Ocean.Select(BitConverter.SingleToInt32Bits)),
                "the ocean corners, or the previous chunk's where the region has none");
            Assert.That((myLeft.Dimension, engineLeft.Dimension), Is.EqualTo((0, 0)),
                "tmpPos in the chunk's dimension");
            Assert.That(ChunkRig.EdgeParts(mine), Is.SameAs(ChunkRig.EdgeParts(engine)),
                "the tesselator's own empty part array");
            Assert.That(ChunkRig.CenterParts(mine), Is.SameAs(ChunkRig.CenterParts(engine)));
            Assert.That(ChunkRig.EdgeParts(engine), Is.Empty);
            if (edgeOnly)
                Assert.That(ChunkRig.CenterParts(engine), Is.Null,
                    "an edge-only pass leaves the centre to the previous one");
            else Assert.That(ChunkRig.CenterParts(engine), Is.Empty);
            float[] engineBounds = [a.x, a.y, a.z, a.radius, a.radiusY, a.radiusZ],
                myBounds = [b.x, b.y, b.z, b.radius, b.radiusY, b.radiusZ];
            Assert.That(myBounds.Select(BitConverter.SingleToInt32Bits),
                Is.EqualTo(engineBounds.Select(BitConverter.SingleToInt32Bits)));
        });
    }

    [TestCase(1, false)]
    [TestCase(1, true)]
    [TestCase(0, false)]
    [TestCase(0, true)]
    public void AnEnclosedChunkGetsTheEnginesResult(int cy, bool edgeOnly)
    {
        using var rig = Enclosed(cy);
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, cy, Z, rig.Tess(X, cy, Z)), Is.True);
        Assert.That(rig.EngineDrawsNothing(X, cy, Z), Is.True);
        AssertSameResult(rig, cy, edgeOnly);
        Assert.That(Left(rig).Ocean, Has.None.EqualTo(-7.5f),
            "the region's ocean corners were read, so the comparison says something");
    }

    // Caves, glass and water in the neighbours do not matter as long as the slab touching the chunk is closed
    [Test]
    public void OnlyTheTouchingSlabOfANeighbourCounts()
    {
        using var rig = Enclosed(1, Mixed);
        _ = rig.Put(X + 1, Y, Z, Cave, (x, _, _) => x == 5 ? ChunkRig.Water : ChunkRig.Air);
        // slabs lying on the chunk close its top
        _ = rig.Put(X, Y + 1, Z, (_, y, _) => y == 0 ? ChunkRig.Slab : ChunkRig.Air);
        Assert.Multiple(() =>
        {
            Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, rig.Tess(X, Y, Z)), Is.True);
            Assert.That(rig.EngineDrawsNothing(X, Y, Z), Is.True);
        });
        AssertSameResult(rig, Y, false);
    }

    private static int Cave(int x, int y, int z)
    {
        if (x == 0) return ChunkRig.Stone;
        return x % 2 == 0 ? ChunkRig.Air : ChunkRig.Glass;
    }

    // One change at a time to an enclosed chunk: the fast path has to agree with the engine's face culling on every one, except
    // where it is conservative on purpose - a snow layer block above a block is hidden only if that block allows snow coverage, a
    // virtual the fast path does not call
    private static IEnumerable<TestCaseData> Changes()
    {
        yield return Case("air inside",
            rig => rig.Put(X, Y, Z, (x, y, z) => (x, y, z) == (7, 9, 11) ? ChunkRig.Air : ChunkRig.Stone));
        yield return Case("air at the edge",
            rig => rig.Put(X, Y, Z, (x, y, z) => (x, y, z) == (0, 9, 11) ? ChunkRig.Air : ChunkRig.Stone));
        yield return Case("glass inside",
            rig => rig.Put(X, Y, Z, (x, y, z) => (x, y, z) == (7, 9, 11) ? ChunkRig.Glass : ChunkRig.Stone));
        yield return Case("water inside",
            rig => rig.Put(X, Y, Z, Solid, (x, y, z) => (x, y, z) == (3, 3, 3) ? ChunkRig.Water : ChunkRig.Air));
        yield return Case("json and water inside",
            rig => rig.Put(X, Y, Z, (x, y, z) => (x, y, z) == (7, 9, 11) ? ChunkRig.JsonWater : ChunkRig.Stone));
        yield return Case("snow under stone",
            rig => rig.Put(X, Y, Z, (x, y, z) => (x, y, z) == (7, 9, 11) ? ChunkRig.Snow : ChunkRig.Stone), true);
        yield return Case("slab inside",
            rig => rig.Put(X, Y, Z, (x, y, z) => (x, y, z) == (7, 9, 11) ? ChunkRig.Slab : ChunkRig.Stone));
        foreach (var (dx, dy, dz) in Faces)
        {
            var name = $"{dx}/{dy}/{dz}";
            yield return Case("missing " + name, rig => rig.Remove(X + dx, Y + dy, Z + dz));
            yield return Case("empty " + name,
                rig => rig.Put(X + dx, Y + dy, Z + dz, (_, _, _) => ChunkRig.Air, empty: true));
            yield return Case("glass touching " + name, rig => rig.Put(X + dx, Y + dy, Z + dz,
                (x, y, z) => Touching(dx, dy, dz, x, y, z) && (x + y + z) % 7 == 0 ? ChunkRig.Glass : ChunkRig.Stone));
            yield return Case("slab touching " + name, rig => rig.Put(X + dx, Y + dy, Z + dz,
                (x, y, z) => Touching(dx, dy, dz, x, y, z) ? ChunkRig.Slab : ChunkRig.Stone));
            yield return Case("snow touching " + name, rig => rig.Put(X + dx, Y + dy, Z + dz,
                (x, y, z) => Touching(dx, dy, dz, x, y, z) ? ChunkRig.Snow : ChunkRig.Stone), dy == 1);
            yield return Case("air behind the slab " + name, rig => rig.Put(X + dx, Y + dy, Z + dz,
                (x, y, z) => Touching(dx, dy, dz, x, y, z) ? ChunkRig.Stone : ChunkRig.Air));
        }
    }

    private static TestCaseData Case(string name, Action<ChunkRig> change, bool conservative = false)
    {
        Changed[name] = change;
        return new TestCaseData(name, conservative).SetName("Agrees: " + name);
    }

    private static bool Touching(int dx, int dy, int dz, int x, int y, int z) => (dx, dy, dz) switch
    {
        (1, 0, 0) => x == 0,
        (-1, 0, 0) => x == 31,
        (0, 1, 0) => y == 0,
        (0, -1, 0) => y == 31,
        (0, 0, 1) => z == 0,
        _ => z == 31
    };

    [TestCaseSource(nameof(Changes))]
    public void TheFastPathAgreesWithTheEnginesFaceCulling(string change, bool conservative)
    {
        if (Changed.Count == 0) _ = Changes().ToList();
        using var rig = Enclosed(Y);
        Changed[change](rig);
        var enclosed = OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, rig.Tess(X, Y, Z));
        var nothing = rig.EngineDrawsNothing(X, Y, Z);
        if (conservative) Assert.That(enclosed, Is.False, "left to the engine");
        else Assert.That(enclosed, Is.EqualTo(nothing));
        if (!enclosed) return;
        Assert.That(nothing, Is.True);
        AssertSameResult(rig, Y, false);
        AssertSameResult(rig, Y, true);
    }

    // The engine culls Merge blocks against each other too; the fast path takes only Default and leaves these to the engine
    [Test]
    public void OtherCullModesAreLeftToTheEngine()
    {
        using var rig = Enclosed(Y, (x, y, z) => (x, y, z) == (7, 9, 11) ? ChunkRig.Merge : ChunkRig.Stone);
        Assert.Multiple(() =>
        {
            Assert.That(rig.EngineDrawsNothing(X, Y, Z), Is.True, "the engine draws nothing here either");
            Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, rig.Tess(X, Y, Z)), Is.False,
                "but only Default is proven");
        });
    }

    [Test]
    public void DecorsShapeReloadsAndTheMapEdgeAreLeftToTheEngine()
    {
        using var rig = Enclosed(Y);
        var tess = rig.Tess(X, Y, Z);
        rig.At(X, Y, Z)!.Decors = new Dictionary<int, Block> { [0] = rig.Blocks[ChunkRig.Stone] };
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, tess), Is.False,
            "a decor may draw on a hidden face");
        rig.At(X, Y, Z)!.Decors = [];
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, tess), Is.True,
            "no decor in the dictionary draws nothing");
        ChunkRig.Set(rig.Tesselator, "reloadTesselatorOnTesselationThread", true);
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, tess), Is.False,
            "ReloadTesselator must still run");
        ChunkRig.Set(rig.Tesselator, "reloadTesselatorOnTesselationThread", false);
        rig.At(X, Y, Z)!.Empty = true;
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, tess), Is.False);
        rig.At(X, Y, Z)!.Empty = false;
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, ChunkRig.ChunksX, Y, Z, tess), Is.False, "outside the map");
        Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y + 1, Z, tess), Is.False,
            "not the chunk in the map there");
    }

    // A palette value at or past Blocks.Count is air to the engine (ClearPaletteOutsideMaxValue), so it opens the chunk
    [Test]
    public void IdsPastTheBlockCountAreAir()
    {
        using var rig = Enclosed(Y);
        rig.Game.Blocks = [.. rig.Blocks.Take(ChunkRig.Granite)]; // granite and up no longer count
        Assert.Multiple(() =>
        {
            Assert.That(OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, rig.Tess(X, Y, Z)), Is.False);
            Assert.That(rig.EngineDrawsNothing(X, Y, Z), Is.False, "the neighbours' granite became air");
        });
    }

    // Straight on the bit planes: which palette indices a layer uses decides, not what its palette lists
    [Test]
    public void AnUnusedAirEntryInThePaletteDoesNotCount()
    {
        using var rig = new ChunkRig();
        var chunk = rig.Put(0, 0, 0, Solid);
        var layer = ((ChunkData)chunk.Data).blocksLayer;
        Assert.That(layer.palette[0], Is.Zero, "the engine keeps air at index 0");
        Assert.That(OccludedChunks.Fits(layer, rig.Blocks, ChunkRig.Count, -1), Is.True);
        chunk.Data[ChunkRig.Local(31, 31, 31)] = ChunkRig.Air;
        Assert.That(OccludedChunks.Fits(layer, rig.Blocks, ChunkRig.Count, -1), Is.False);
        Assert.That(OccludedChunks.Fits(layer, rig.Blocks, ChunkRig.Count, 4), Is.True,
            "as the chunk above another, its layer y 0 counts");
        Assert.That(OccludedChunks.Fits(layer, rig.Blocks, ChunkRig.Count, 5), Is.False,
            "as the chunk below, its layer y 31");
        Assert.That(OccludedChunks.Fits(null, rig.Blocks, ChunkRig.Count, -1), Is.False, "no layer is all air");
    }

    [Test]
    public void GoodFollowsTheEnginesCullTest()
    {
        using var rig = new ChunkRig();
        var b = rig.Blocks;
        Assert.Multiple(() =>
        {
            Assert.That(OccludedChunks.Good(b[ChunkRig.Stone], -1), Is.True);
            Assert.That(OccludedChunks.Good(b[ChunkRig.Air], -1), Is.False);
            Assert.That(OccludedChunks.Good(b[ChunkRig.Merge], -1), Is.False);
            Assert.That(OccludedChunks.Good(b[ChunkRig.JsonWater], -1), Is.False);
            Assert.That(OccludedChunks.Good(b[ChunkRig.Snow], -1), Is.False);
            Assert.That(OccludedChunks.Good(b[ChunkRig.Slab], 4), Is.True, "above: its down side faces the chunk");
            Assert.That(OccludedChunks.Good(b[ChunkRig.Slab], 5), Is.False);
            Assert.That(OccludedChunks.Good(b[ChunkRig.Snow], 4), Is.False, "the engine's snow rule on the up side");
            Assert.That(OccludedChunks.Good(b[ChunkRig.Snow], 1), Is.True);
            Assert.That(OccludedChunks.Good(null, 1), Is.False);
        });
    }

    // Sixty stone types: 6-bit palettes, the engine's general bit-plane case, with unused palette indices past the count. One random
    // change per seed, each kind once - a hole in the chunk, a hole in a touching slab, a hole behind it, none - and the fast path has
    // to agree with the engine's face culling, and hand out the engine's result where it takes over. A plane read wrong would take a
    // stone type for air (or air for stone) somewhere in the 229 376 positions.
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    public void SixBitPalettesWithRandomHolesAgree(int seed)
    {
        const int types = 60;
        var rng = new Random(seed);
        using var rig = new ChunkRig(types);

        int Any(int x, int y, int z) =>
            ChunkRig.Count + (int)((uint)((x * 73856093) ^ (y * 19349663) ^ (z * 83492791) ^ (seed * 2654435761u)) % types);

        var center = rig.Put(X, Y, Z, Any);
        foreach (var (dx, dy, dz) in Faces) _ = rig.Put(X + dx, Y + dy, Z + dz, Any);
        var bits = (int)ChunkRig.Get(((ChunkData)center.Data).blocksLayer, "bitsize");
        Assert.That(bits, Is.GreaterThanOrEqualTo(6), "the general case");
        var (hx, hy, hz) = (rng.Next(32), rng.Next(32), rng.Next(32));
        int[] holes = [ChunkRig.Air, ChunkRig.Glass, ChunkRig.Slab, ChunkRig.JsonWater];
        var hole = holes[rng.Next(holes.Length)];
        var face = rng.Next(Faces.Length);
        var (fx, fy, fz) = Faces[face];
        var neighbour = rig.At(X + fx, Y + fy, Z + fz)!;
        var (tx, ty, tz) = (fx, fy, fz) switch
        {
            (1, 0, 0) => (0, hy, hz),
            (-1, 0, 0) => (31, hy, hz),
            (0, 1, 0) => (hx, 0, hz),
            (0, -1, 0) => (hx, 31, hz),
            (0, 0, 1) => (hx, hy, 0),
            _ => (hx, hy, 31)
        };
        switch (seed % 4)
        {
            case 1:
                center.Data[ChunkRig.Local(hx, hy, hz)] = hole;
                break;
            case 2:
                neighbour.Data[ChunkRig.Local(tx, ty, tz)] = hole;
                break;
            case 3:
                // the far side: behind the touching slab
                neighbour.Data[ChunkRig.Local(31 - tx, 31 - ty, 31 - tz)] = ChunkRig.Air;
                break;
        }

        var enclosed = OccludedChunks.Enclosed(rig.Tesselator, X, Y, Z, rig.Tess(X, Y, Z));
        var nothing = rig.EngineDrawsNothing(X, Y, Z);
        Assert.That(enclosed, Is.EqualTo(nothing), $"change {seed % 4}, block {hole}, face {face}");
        if (seed % 4 is 0 or 3) Assert.That(enclosed, Is.True, "no hole, or one the chunk cannot see");
        if (!enclosed) return;
        AssertSameResult(rig, Y, false);
        AssertSameResult(rig, Y, true);
    }

    // The positions of the extended arrays an edge-only pass reads: those within two blocks of the chunk's faces
    private static bool Shell(int ext)
    {
        int x = ext % 34 - 1, z = ext / 34 % 34 - 1, y = ext / 34 / 34 - 1;
        return x is < 2 or > 29 || y is < 2 or > 29 || z is < 2 or > 29;
    }

    private static bool Edge(int index)
    {
        int x = index % 32, z = index / 32 % 32, y = index / 1024;
        return x is 0 or 31 || y is 0 or 31 || z is 0 or 31;
    }

    // What the skipped pipeline leaves behind: the tesselator's extended arrays and face flags still hold the chunk before. An
    // edge-only pass of the enclosed chunk reads only the flags it writes itself, and so does the next chunk's edge-only pass: everything
    // it reads comes out the same whether the enclosed chunk went through the engine or through the fast path.
    [Test]
    public void TheNextEdgePassSeesNothingOfTheSkippedPipeline()
    {
        using var rig = Enclosed(Y);
        _ = rig.Put(2, 3, 2, (x, y, z) => (x * 7 + y * 3 + z) % 5 == 0 ? ChunkRig.Air : ChunkRig.Glass,
            (x, y, z) => (x + y + z) % 11 == 0 ? ChunkRig.Water : ChunkRig.Air);
        _ = rig.Put(0, 2, 0, (x, y, z) => (x + 2 * y + 3 * z) % 4 == 0 ? ChunkRig.Air : ChunkRig.Stone);
        _ = rig.Put(0, 2, 1, (_, y, _) => y < 9 ? ChunkRig.Stone : ChunkRig.Air);
        _ = rig.Tesselator.BeginProcessChunk(2, 3, 2, rig.At(2, 3, 2)!, false);
        Assert.That(rig.Draw.Count(flags => flags != 0), Is.GreaterThan(ChunkRig.Volume / 4),
            "a chunk full of faces before");
        AssertSameResult(rig, Y, true);

        (Block[] Blocks, Block[] Fluids, int[] Rgbs, byte[] Draw, byte[] DrawFluids) Next(bool fast)
        {
            _ = rig.Tesselator.BeginProcessChunk(2, 3, 2, rig.At(2, 3, 2)!, false); // a chunk full of faces before
            var tess = rig.Tess(X, Y, Z);
            _ = fast
                ? OccludedChunks.Apply(rig.Tesselator, X, Y, Z, tess, false)
                : rig.Tesselator.NowProcessChunk(X, Y, Z, tess, false);
            _ = rig.Tesselator.BeginProcessChunk(0, 2, 0, rig.At(0, 2, 0)!, true);
            var ext = Enumerable.Range(0, 34 * 34 * 34).Where(Shell).ToArray();
            var edge = Enumerable.Range(0, ChunkRig.Volume).Where(Edge).ToArray();
            return (ext.Select(i => rig.BlocksExt[i]).ToArray(), ext.Select(i => rig.FluidsExt[i]).ToArray(),
                ext.Select(i => rig.RgbsExt[i]).ToArray(), edge.Select(i => rig.Draw[i]).ToArray(),
                edge.Select(i => rig.DrawFluids[i]).ToArray());
        }

        var engine = Next(false);
        var mine = Next(true);
        Assert.Multiple(() =>
        {
            Assert.That(mine.Blocks, Is.EqualTo(engine.Blocks), "extended blocks");
            Assert.That(mine.Fluids, Is.EqualTo(engine.Fluids), "extended fluids");
            Assert.That(mine.Rgbs, Is.EqualTo(engine.Rgbs), "extended light");
            Assert.That(mine.Draw, Is.EqualTo(engine.Draw), "edge face flags");
            Assert.That(mine.DrawFluids, Is.EqualTo(engine.DrawFluids), "edge fluid face flags");
            Assert.That(engine.Draw.Any(flags => flags != 0), Is.True,
                "the edge pass draws faces, so the comparison says something");
        });
    }

    // Another mod's patch on a method of the skipped pipeline, a chunk reader whose output the prefix reproduces included, would miss
    // the enclosed chunks: the engine runs every pass while it is there. On NowProcessChunk only a patch of its body counts.
    [TestCase("BuildBlockPolygons", HarmonyPatchType.Postfix, true)]
    [TestCase("GetRange_Faster", HarmonyPatchType.Postfix, true)]
    [TestCase("NowProcessChunk", HarmonyPatchType.Transpiler, true)]
    [TestCase("NowProcessChunk", HarmonyPatchType.Prefix, false)]
    public void AnotherModsPatchOnThePipeline(string seam, HarmonyPatchType kind, bool standsDown)
    {
        using var rig = Enclosed(Y);
        using var harmony = new TestHarmony("komet-test-occludedchunks");
        using var other = new TestHarmony("komet-test-occludedchunks-other");
        var method = Array.Find(OccludedChunks.Shaped(), m => m?.Name == seam);
        var patch = kind == HarmonyPatchType.Transpiler ? Foreign.Transpiler : Foreign.Postfix;
        var answered = standsDown ? 0L : 1L;
        OccludedChunks.Install(harmony);
        _ = other.Patch(method, kind == HarmonyPatchType.Prefix ? patch : null,
            kind == HarmonyPatchType.Postfix ? patch : null, kind == HarmonyPatchType.Transpiler ? patch : null);
        OccludedChunks.Recheck();
        Counting.Hud = true;
        var before = OccludedChunks.Hits;
        _ = rig.Tesselator.NowProcessChunk(X, Y, Z, rig.Tess(X, Y, Z), false);
        Assert.That((OccludedChunks.StoodDown, OccludedChunks.Hits - before), Is.EqualTo((standsDown, answered)));
        other.UnpatchAll(other.Id);
        OccludedChunks.Recheck();
        _ = rig.Tesselator.NowProcessChunk(X, Y, Z, rig.Tess(X, Y, Z), false);
        Assert.That((OccludedChunks.StoodDown, OccludedChunks.Hits - before), Is.EqualTo((false, answered + 1)));
    }

    [Test]
    public void ThePrefixAnswersEnclosedChunksOnly()
    {
        using var rig = Enclosed(Y);
        using var harmony = new TestHarmony("komet-test-occludedchunks");
        // KometModSystem's order and id: the other fast paths patch methods the prefix skips
        TessSeams.Install(harmony, null);
        ExtendedRows.Install(harmony);
        VisibleFaces.Install(harmony);
        FaceLight.Install(harmony);
        OccludedChunks.Install(harmony);
        TessSeams.Recheck();
        Assert.That((ExtendedRows.Rewritten, VisibleFaces.Installed, OccludedChunks.Installed),
            Is.EqualTo((true, true, true)));
        Assert.That((ExtendedRows.Blocked, VisibleFaces.StoodDown, FaceLight.StoodDown, OccludedChunks.StoodDown),
            Is.EqualTo((false, false, false, false)));
        Counting.Hud = true;
        var before = OccludedChunks.Hits;
        var tess = rig.Tess(X, Y, Z);
        ChunkRig.Set(tess, "boundingSphere", new Sphere(1, 2, 3, 4, 5, 6));
        Assert.That(rig.Tesselator.NowProcessChunk(X, Y, Z, tess, false), Is.Zero);
        Assert.That(OccludedChunks.Hits - before, Is.EqualTo(1));
        Assert.That(ChunkRig.Bounds(tess).x, Is.EqualTo(X * 32 + 16f));
        OccludedChunks.Enabled = false; // the engine's own pass, which draws nothing here either
        Assert.That(rig.Tesselator.NowProcessChunk(X, Y, Z, rig.Tess(X, Y, Z), false), Is.Zero);
        Assert.That(OccludedChunks.Hits - before, Is.EqualTo(1));
    }
}
