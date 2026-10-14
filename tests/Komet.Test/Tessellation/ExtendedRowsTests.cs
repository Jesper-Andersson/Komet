using Komet.Core;
using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// Golden tests against the engine: the same ClientChunkData, built through the engine's own pool and setters, read by the engine's
// GetRange_Faster and GetRange and by ExtendedRows' stand-ins into arrays prefilled with sentinels. Every Block reference, every
// packed light and every untouched cell must match, and so must the exceptions the engine throws on data it cannot read.
public sealed class ExtendedRowsTests
{
    private const string ClientData = "Vintagestory.Client.NoObf.ClientChunkData, VintagestoryLib";

    private const int ExtLength = 39304, KnownBlocks = 250, FastBlocks = 300, Size = 32, Cells = Size * Size * Size,
        Rows = Size * Size;

    private static readonly ClientChunkDataPool Pool = new(Size, null!);

    private static readonly Block[] BlocksFast =
        [.. Enumerable.Range(0, FastBlocks).Select(i => new Block { BlockId = i })];

    private static readonly Block Sentinel = new() { BlockId = -7 };

    private static readonly (int X0, int Length)[] Ranges =
        [(0, 32), (0, 2), (30, 2), (5, 11), (31, 1), (30, 4), (7, 0)];

    private static readonly FieldInfo LockCount = AccessTools.Field(typeof(FastRWLock), "currentCount");
    private static readonly ColorUtil.LightUtil Converter = NewConverter(11);

    [SetUp]
    public void Reset() => (ExtendedRows.Enabled, Counting.Hud) = (true, true);

    [TearDown]
    public void Restore() => (ExtendedRows.Enabled, Counting.Hud) = (true, false);

    private static ChunkData NewData() => (ChunkData)ClientChunk.CreateNew(Pool).Data;

    private static ColorUtil.LightUtil NewConverter(int seed)
    {
        var r = new Random(seed);
        var block = Enumerable.Range(0, 32).Select(_ => (float)r.NextDouble()).ToArray();
        var sun = Enumerable.Range(0, 32).Select(_ => (float)r.NextDouble()).ToArray();
        var hues = Enumerable.Range(0, 64).Select(_ => (byte)r.Next(256)).ToArray();
        var sats = Enumerable.Range(0, 8).Select(_ => (byte)r.Next(256)).ToArray();
        return new ColorUtil.LightUtil(block, sun, hues, sats);
    }

    private static int Index(int x, int y, int z) => (y * Size + z) * Size + x;

    // Terrain: `ids` below y 8 in noise, one id per row between 8 and 15 (uniform rows), air above; every id is used at least once
    private static void PlaceBlocks(ChunkData data, Random r, int[] ids)
    {
        for (var i = 0; i < ids.Length; i++) data[i] = ids[i];
        for (var i = ids.Length; i < Cells; i++)
        {
            var y = i / Rows;
            if (y < 8) data[i] = ids[r.Next(ids.Length)];
            else if (y < 16) data[i] = ids[i / Size % ids.Length];
        }
    }

    private static int[] Ids(Random r, int count, int from = 1, int to = KnownBlocks) =>
        [.. Enumerable.Range(from, to - from).OrderBy(_ => r.Next()).Take(count)];

    private static int[] LightValues(Random r, int count) =>
        [.. Enumerable.Range(0, count).Select(_ => r.Next(32) | (r.Next(32) << 5) | (r.Next(64) << 10) | (r.Next(8) << 16))];

    private static void PlaceLight(ChunkData data, Random r, int[] values)
    {
        for (var i = 0; i < Cells; i++)
            data.SetLight(i, (uint)(i / Rows >= 20 ? 24 : values[r.Next(values.Length)]));
    }

    private static void PlaceFluids(ChunkData data, Random r, int[] ids)
    {
        for (var i = 0; i < Cells; i++)
            if (i / Rows is >= 4 and < 12 && r.Next(3) == 0)
                data.SetFluid(i, ids[r.Next(ids.Length)]);
    }

    private static Action Build(ChunkData data) => () =>
    {
        BlockChunkDataLayer.blocksByPaletteIndex = null;
        BuildFast(data, BlocksFast);
    };

    private static List<Case> Matrix()
    {
        var r = new Random(1234);
        List<Case> cases = [];
        var empty = NewData();
        cases.Add(new Case("empty", empty, Build(empty), Expect.Fast, Expect.Fast));
        var unbuilt = NewData();
        unbuilt.FillWithSunlight(24);
        cases.Add(new Case("no blocks, never built (blockAir null)", unbuilt, () => BlockAir(unbuilt) = null,
            Expect.Fast, Expect.Fast));
        for (var bits = 1; bits <= 8; bits++)
        {
            var data = NewData();
            PlaceBlocks(data, r, Ids(r, (1 << bits) - 1 - (bits > 1 ? r.Next(1 << (bits - 2)) : 0)));
            PlaceLight(data, r, LightValues(r, bits * 5));
            if (bits % 3 != 0) PlaceFluids(data, r, Ids(r, bits % 3 == 1 ? 1 : 5, KnownBlocks - 20));
            cases.Add(new Case($"blocks {bits} bits", data, Build(data), Expect.Fast, Expect.Fast));
        }

        foreach (var lights in (int[])[1, 3, 7, 15, 31, 100])
        {
            var data = NewData();
            PlaceBlocks(data, r, Ids(r, 6));
            PlaceLight(data, r, LightValues(r, lights));
            PlaceFluids(data, r, Ids(r, lights % 40 + 1, 1, 200));
            cases.Add(new Case($"light {lights} values", data, Build(data), Expect.Fast, Expect.Fast));
        }

        cases.Add(DoubleBuffered(r, false));
        cases.Add(DoubleBuffered(r, true));
        cases.AddRange(Stale(r));
        cases.AddRange(Odd(r));
        cases.AddRange(Wide(r));
        return cases;
    }

    // Light double buffering: SetSunlight_Buffered copies the planes into light2, then more block light grows the live palette past what
    // the buffer's planes can index; GetRange reads the buffer with the live palette. Without a light layer they read 0.
    private static Case DoubleBuffered(Random r, bool dropLayer, int lights = 6)
    {
        var data = NewData();
        PlaceBlocks(data, r, Ids(r, 12));
        PlaceLight(data, r, LightValues(r, lights));
        for (var i = 0; i < Cells; i += 97) data.SetSunlight_Buffered(i, i % 32);
        for (var i = 0; i < Cells; i += 13) data.SetBlocklight_Buffered(i, (i / 13 % 31) << 5);
        Assert.That(Light2(data), Is.Not.Null, "double buffering started");
        if (dropLayer) data.lightLayer = null;
        var name = dropLayer
            ? "light2 without a light layer"
            : $"light2 of {Light2(data)!.Length} planes with a grown palette";
        return new Case(name, data, Build(data), Expect.Fast, Expect.Fast);
    }

    // More than eight planes (the per-plane decoder): 280 block ids (9 bits, getBlockGeneralCase and GetUnsafe's general case), 1500
    // light values (11 bits, GetGeneralCase) and a buffered light of 11 planes. Then the engine's own throws: a buffered light over a
    // live palette replaced by a smaller one (Light2 indexes past it), and a block layer that lost its second plane (getBlockTwo and
    // GetFromBits2 throw NullReferenceException, GetUnsafe its own Exception).
    private static IEnumerable<Case> Wide(Random r)
    {
        var blocks = NewData();
        PlaceBlocks(blocks, r, Ids(r, 280, 1, FastBlocks));
        PlaceLight(blocks, r, LightValues(r, 12));
        PlaceFluids(blocks, r, Ids(r, 3, KnownBlocks - 10));
        Assert.That(TessSeams.Bitsize(blocks.blocksLayer), Is.EqualTo(9));
        yield return new Case("blocks 9 bits", blocks, Build(blocks), Expect.Fast, Expect.Fast);

        var light = NewData();
        PlaceBlocks(light, r, Ids(r, 30));
        PlaceLight(light, r, LightValues(r, 1500));
        Assert.That(TessSeams.Bitsize(light.lightLayer!), Is.EqualTo(11));
        yield return new Case("light 11 bits", light, Build(light), Expect.Fast, Expect.Fast);
        yield return DoubleBuffered(r, false, 1500);

        var shrunk = NewData();
        PlaceBlocks(shrunk, r, Ids(r, 5));
        PlaceLight(shrunk, r, LightValues(r, 20));
        shrunk.SetSunlight_Buffered(0, 3); // light2: the five planes as they are
        shrunk.lightLayer = null;
        shrunk.SetSunlight_Buffered(5, 7); // a new light layer of one bit, two palette entries
        Assert.That(Light2(shrunk), Has.Length.EqualTo(5));
        yield return new Case("light2 over a smaller live palette", shrunk, Build(shrunk), Expect.Throws,
            Expect.Throws);

        var missing = NewData();
        PlaceBlocks(missing, r, Ids(r, 3));
        missing.FillWithSunlight(20);
        Assert.That(TessSeams.Bitsize(missing.blocksLayer), Is.EqualTo(2));
        DataBit1(missing.blocksLayer) = null;
        yield return new Case("a missing plane", missing, Build(missing), Expect.Throws, Expect.Throws);
    }

    // GetBlockAsBlock out of date, built for a replaced layer, or reading a table another chunk left
    private static IEnumerable<Case> Stale(Random r)
    {
        var grown = NewData();
        PlaceBlocks(grown, r, Ids(r, 5));
        grown.FillWithSunlight(20);
        BuildFast(grown, BlocksFast);
        grown[Index(3, 20, 3)] = 222; // paletteCount != blocksArrayCount: GetRange_Faster hands the rows to GetRange
        yield return new Case("block added after BuildFastBlockAccessArray", grown, () => { }, Expect.Fast,
            Expect.Fast);

        var replaced = NewData();
        PlaceBlocks(replaced, r, Ids(r, 6));
        PlaceLight(replaced, r, LightValues(r, 4));
        var other = NewData();
        PlaceBlocks(other, r, Ids(r, 6)); // the same palette count, another arrangement
        var built = replaced.blocksLayer;
        yield return new Case("delegate built for a replaced layer", replaced, () =>
        {
            BlockChunkDataLayer.blocksByPaletteIndex = null;
            replaced.blocksLayer = built;
            BuildFast(replaced, BlocksFast);
            replaced.blocksLayer = other.blocksLayer;
        }, Expect.Fast, Expect.Fast);

        var big = NewData();
        PlaceBlocks(big, r, Ids(r, 100));
        var small = Garbage(r);
        yield return new Case("stale shared table", small, () =>
        {
            BlockChunkDataLayer.blocksByPaletteIndex = null;
            BuildFast(big, BlocksFast);
            BuildFast(small, BlocksFast);
        }, Expect.Fast, Expect.Fast);
        var shortTable = Garbage(r);
        yield return new Case("table shorter than the planes index", shortTable, Build(shortTable), Expect.Throws,
            Expect.Fast);
    }

    // Five palette entries (three planes) and cells whose planes say 5, 6 or 7: past the palette count
    private static ChunkData Garbage(Random r)
    {
        var data = NewData();
        PlaceBlocks(data, r, Ids(r, 4));
        data.FillWithSunlight(22);
        var planes = DataBits(data.blocksLayer)!;
        Assert.That(TessSeams.Bitsize(data.blocksLayer), Is.EqualTo(3));
        for (var row = 0; row < Rows; row += 7)
            (planes[0][row], planes[2][row]) = (planes[0][row] | 0x00F0_0F00, planes[2][row] | 0x00FF_0F00);
        return data;
    }

    private static IEnumerable<Case> Odd(Random r)
    {
        var noBlocks = NewData();
        PlaceLight(noBlocks, r, LightValues(r, 9));
        PlaceFluids(noBlocks, r, Ids(r, 3));
        yield return new Case("fluids and light, no blocks layer", noBlocks, Build(noBlocks), Expect.Fast, Expect.Fast);

        var cleared = NewData();
        PlaceBlocks(cleared, r, Ids(r, 9));
        PlaceLight(cleared, r, LightValues(r, 5));
        cleared.blocksLayer.Clear(null);
        cleared.lightLayer.Clear(null);
        yield return new Case("bitsize 0 with a palette", cleared, Build(cleared), Expect.Fast, Expect.Fast);

        var past = NewData();
        PlaceBlocks(past, r, [.. Ids(r, 5), 260, 280, 299]);
        PlaceLight(past, r, LightValues(r, 3));
        yield return new Case("block ids past Blocks.Count", past, Build(past), Expect.Fast, Expect.Fast);
        var clearedPast = NewData();
        PlaceBlocks(clearedPast, r, [.. Ids(r, 5), 260, 280]);
        clearedPast.blocksLayer.ClearPaletteOutsideMaxValue(KnownBlocks);
        yield return new Case("block ids cleared at Blocks.Count", clearedPast, Build(clearedPast), Expect.Fast,
            Expect.Fast);

        var fluidPast = NewData();
        PlaceBlocks(fluidPast, r, Ids(r, 3));
        PlaceFluids(fluidPast, r, [5, 400]);
        yield return new Case("fluid id past blocksFast", fluidPast, Build(fluidPast), Expect.Throws, Expect.Throws);

        var fluidGeneral = NewData();
        PlaceBlocks(fluidGeneral, r, Ids(r, 2));
        PlaceFluids(fluidGeneral, r, Ids(r, 40));
        PlaceLight(fluidGeneral, r, LightValues(r, 2));
        yield return new Case("fluids of 6 bits", fluidGeneral, Build(fluidGeneral), Expect.Fast, Expect.Fast);

        var foreign = NewData();
        PlaceBlocks(foreign, r, Ids(r, 7));
        PlaceFluids(foreign, r, Ids(r, 2));
        foreign.fluidsLayer.Get = index => index % 5 == 0 ? 17 : 0;
        yield return new Case("a Get a mod replaced", foreign, Build(foreign), Expect.SomeFallbacks,
            Expect.SomeFallbacks);
    }

    private static Output Read(RowReader read, ChunkData data, int x0, int length, Block? air)
    {
        var output = new Output();
        BlockAir(data) = air;
        for (var row = 0; row < Rows; row++)
        {
            var index3D = row * Size + x0;
            try
            {
                read(data, output.Blocks!, output.Fluids!, output.Rgbs, row * 37 + x0 - 1, index3D, index3D + length,
                    BlocksFast, Converter);
            }
            catch (Exception e) when (Engine(e))
            {
                output.Thrown.Add($"row {row}: {e.GetType().Name}: {e.Message}");
            }
        }

        output.AirAfter = BlockAir(data);
        return output;
    }

    private static void Same(Output engine, Output mine, string what)
    {
        var blocks = FirstDifference(engine.Blocks, mine.Blocks);
        var fluids = FirstDifference(engine.Fluids, mine.Fluids);
        var rgbs = Enumerable.Range(0, ExtLength).FirstOrDefault(i => engine.Rgbs[i] != mine.Rgbs[i], -1);
        Assert.Multiple(() =>
        {
            Assert.That(blocks, Is.EqualTo(-1), $"{what}: first block that differs");
            Assert.That(fluids, Is.EqualTo(-1), $"{what}: first fluid that differs");
            Assert.That(rgbs, Is.EqualTo(-1), $"{what}: first light that differs");
            Assert.That(mine.Thrown, Is.EqualTo(engine.Thrown), $"{what}: exceptions");
            Assert.That(mine.AirAfter, Is.SameAs(engine.AirAfter), $"{what}: blockAir afterwards");
        });
    }

    private static int FirstDifference(Block?[] a, Block?[] b) =>
        Enumerable.Range(0, ExtLength).FirstOrDefault(i => !ReferenceEquals(a[i], b[i]), -1);

    private static void AllRows(string method, RowReader engine, RowReader mine, System.Func<Case, Expect> expect)
    {
        foreach (var c in Matrix())
        {
            c.Prepare();
            var air = BlockAir(c.Data);
            var thrown = 0;
            foreach (var (x0, length) in Ranges)
            {
                var what = $"{method}, {c.Name}, cells {x0}..{x0 + length - 1}";
                var (decoded, fallbacks) = (ExtendedRows.Decoded, ExtendedRows.Fallbacks);
                var fast = Read(mine, c.Data, x0, length, air);
                (decoded, fallbacks) = (ExtendedRows.Decoded - decoded, ExtendedRows.Fallbacks - fallbacks);
                var reference = Read(engine, c.Data, x0, length, air);
                Same(reference, fast, what);
                thrown += reference.Thrown.Count;
                Assert.Multiple(() =>
                {
                    Assert.That(ExtendedRows.Blocked, Is.False,
                        "another patch stood the rows down: nothing was compared");
                    Assert.That(Held(c.Data), Is.Empty, $"{what}: locks still held");
                    if (length == 0 || x0 + length > Size)
                    {
                        Assert.That((decoded, fallbacks), Is.EqualTo((0L, (long)Rows)),
                            $"{what}: not one row of the chunk, the engine's");
                        return;
                    }

                    switch (expect(c))
                    {
                        case Expect.Fast:
                            Assert.That((decoded, fallbacks), Is.EqualTo(((long)Rows, 0L)),
                                $"{what}: every row decoded");
                            break;
                        case Expect.SomeFallbacks:
                            Assert.That(fallbacks, Is.GreaterThan(0), $"{what}: handed to the engine");
                            break;
                        default:
                            Assert.That(fallbacks, Is.GreaterThanOrEqualTo(reference.Thrown.Count),
                                $"{what}: thrown by the engine");
                            break;
                    }
                });
            }

            if (expect(c) == Expect.Throws)
                Assert.That(thrown, Is.GreaterThan(0), $"{method}, {c.Name}: the engine throws");
        }
    }

    [Test]
    [Category("Slow")]
    public void GetRangeFasterRowsMatchTheEngine() =>
        AllRows("GetRange_Faster", EngineFaster, ExtendedRows.Faster, c => c.Faster);

    [Test]
    [Category("Slow")]
    public void GetRangeRowsMatchTheEngine() => AllRows("GetRange", EngineRange, ExtendedRows.Range, c => c.Range);

    // What the engine's readers throw on data they cannot read: GetUnsafe turns a NullReferenceException into a plain Exception
    private static bool Engine(Exception e) =>
        e is IndexOutOfRangeException or NullReferenceException or ArgumentException || e.GetType() == typeof(Exception);

    // The read locks of the layers and of light2 that are still taken: a leaked one would stall the next writer forever
    private static List<string> Held(ChunkData data)
    {
        List<string> held = [];

        void Check(string name, FastRWLock gate)
        {
            if ((int)LockCount.GetValue(gate)! != 0) held.Add($"{name} {LockCount.GetValue(gate)}");
        }

        if (data.blocksLayer is { } blocks) Check("blocks", blocks.readWriteLock);
        if (data.fluidsLayer is { } fluids) Check("fluids", fluids.readWriteLock);
        if (data.lightLayer is { } light) Check("light", light.readWriteLock);
        Check("light2", Light2Lock(data));
        return held;
    }

    [Test]
    public void SwitchedOffTheEngineRuns()
    {
        var c = Matrix()[5];
        c.Prepare();
        var air = BlockAir(c.Data);
        ExtendedRows.Enabled = false;
        var counted = ExtendedRows.Decoded + ExtendedRows.CellsDecoded + ExtendedRows.Fallbacks;
        var off = Read(ExtendedRows.Faster, c.Data, 0, 32, air);
        Assert.That(ExtendedRows.Decoded + ExtendedRows.CellsDecoded + ExtendedRows.Fallbacks, Is.EqualTo(counted));
        ExtendedRows.Enabled = true;
        Same(Read(ExtendedRows.Faster, c.Data, 0, 32, air), off, "on against off");
    }

    private static int Count(List<CodeInstruction> code, string name) => code.Count(c =>
        c.operand is MethodInfo { DeclaringType: var t, Name: var n } && t == typeof(ExtendedRows) && n == name);

    [Test]
    public void PatchesTheRealMethod()
    {
        using var harmony = new TestHarmony("komet-test-extendedrows");
        ExtendedRows.Install(harmony);
        var code = ExtendedRows.Rewrite(PatchProcessor.GetOriginalInstructions(ExtendedRows.Target()!));
        var engine = ExtendedRows.EngineMethods();
        Assert.Multiple(() =>
        {
            Assert.That((ExtendedRows.Rewritten, ExtendedRows.Blocked), Is.EqualTo((true, false)));
            Assert.That(Harmony.GetPatchInfo(ExtendedRows.Target()!)?.Transpilers.Select(p => p.owner),
                Is.EqualTo([harmony.Id]));
            Assert.That((Count(code, nameof(ExtendedRows.Faster)), Count(code, nameof(ExtendedRows.Range))),
                Is.EqualTo((4, 1)));
            Assert.That(code.Count(c => engine.Any(m => c.Calls(m!))), Is.Zero, "no engine call left");
        });
    }

    // A fifth GetRange_Faster where the GetRange was (another mod's transpiler went first): the IL comes back untouched
    [Test]
    public void AnotherCallShapeIsLeftAlone()
    {
        var engine = ExtendedRows.EngineMethods();
        var original = PatchProcessor.GetOriginalInstructions(ExtendedRows.Target()!);
        var mutated = PatchProcessor.GetOriginalInstructions(ExtendedRows.Target()!);
        var range = mutated.FindIndex(c => c.Calls(engine[1]!));
        Assert.That(range, Is.GreaterThan(0));
        mutated[range].operand = engine[0];
        var result = ExtendedRows.Rewrite(mutated);
        Assert.Multiple(() =>
        {
            Assert.That(ExtendedRows.Rewritten, Is.False);
            Assert.That(result.Select(c => c.opcode), Is.EqualTo(original.Select(c => c.opcode)));
            Assert.That(result.Count(c => c.Calls(engine[0]!)), Is.EqualTo(5));
            Assert.That(
                result.Count(c => c.operand is MethodInfo { DeclaringType: var t } && t == typeof(ExtendedRows)),
                Is.Zero);
        });
    }

    [Test]
    public void AForeignPatchStandsTheRowsDown()
    {
        using var ours = new TestHarmony("komet-test-extendedrows");
        using var theirs = new TestHarmony("komet-test-extendedrows-other");
        ExtendedRows.Install(ours);
        _ = theirs.Patch(ExtendedRows.EngineMethods()[0], postfix: Foreign.Postfix);
        ExtendedRows.Recheck();
        var c = Matrix()[4];
        c.Prepare();
        var decoded = ExtendedRows.Decoded;
        _ = Read(ExtendedRows.Faster, c.Data, 0, 32, BlockAir(c.Data));
        Assert.That((ExtendedRows.Blocked, ExtendedRows.Decoded - decoded), Is.EqualTo((true, 0L)));
        theirs.UnpatchAll(theirs.Id);
        ExtendedRows.Recheck();
        decoded = ExtendedRows.Decoded;
        _ = Read(ExtendedRows.Faster, c.Data, 0, 32, BlockAir(c.Data));
        Assert.That((ExtendedRows.Blocked, ExtendedRows.Decoded - decoded), Is.EqualTo((false, (long)Rows)));
    }

    // BuildExtendedChunkData itself, on a tesselator with a 3x3x3 neighbourhood of matrix chunks (some missing or empty: the engine's
    // sunlit EmptyChunk), once as shipped and once rewritten, from the same pre-seeded arrays (garbage and nulls where the edge-only mode
    // keeps the previous chunk's cells), for both modes and a few centre chunks
    [Test]
    public void TheRewrittenMethodBuildsWhatTheEngineBuilds()
    {
        var matrix = Matrix();
        var (tesselator, method) = Tesselator();
        using var harmony = new TestHarmony("komet-test-extendedrows-whole");
        List<(Block?[] Blocks, Block?[] Fluids, int[] Rgbs)> engine = [];
        var centres = new[] { 3, 9, 17, 22 };
        Run(tesselator, method, matrix, centres, engine);
        ExtendedRows.Install(harmony);
        Assert.That(ExtendedRows.Rewritten, Is.True);
        var decoded = ExtendedRows.Decoded;
        List<(Block?[] Blocks, Block?[] Fluids, int[] Rgbs)> mine = [];
        Run(tesselator, method, matrix, centres, mine);
        Assert.That(ExtendedRows.Decoded - decoded, Is.GreaterThan(1000L * centres.Length), "the rewritten method ran");
        for (var i = 0; i < engine.Count; i++)
        {
            var at = i;
            Assert.Multiple(() =>
            {
                Assert.That(FirstDifference(engine[at].Blocks, mine[at].Blocks), Is.EqualTo(-1), $"run {at}: blocks");
                Assert.That(FirstDifference(engine[at].Fluids, mine[at].Fluids), Is.EqualTo(-1), $"run {at}: fluids");
                Assert.That(mine[at].Rgbs, Is.EqualTo(engine[at].Rgbs), $"run {at}: light");
            });
        }
    }

    private static void Run(ChunkTesselator tesselator, MethodInfo method, List<Case> matrix, int[] centres,
        List<(Block?[] Blocks, Block?[] Fluids, int[] Rgbs)> results)
    {
        var (blocks, fluids, rgbs) = ((Block?[])ChunkRig.Get(tesselator, "currentChunkBlocksExt"),
            (Block?[])ChunkRig.Get(tesselator, "currentChunkFluidBlocksExt"),
            (int[])ChunkRig.Get(tesselator, "currentChunkRgbsExt"));
        var map = (ClientWorldMap)ChunkRig.Get(ChunkRig.Get(tesselator, "game"), "WorldMap");
        foreach (var centre in centres)
        {
            foreach (var skip in (bool[])[false, true])
            {
                var r = new Random(centre);
                for (var i = 0; i < ExtLength; i++)
                    (blocks[i], fluids[i], rgbs[i]) = (r.Next(4) == 0 ? null : BlocksFast[r.Next(FastBlocks)],
                        BlocksFast[r.Next(FastBlocks)], r.Next());
                Place(map, matrix, centre);
                _ = method.Invoke(tesselator, [Chunk(map, 1, 1, 1), 1, 1, 1, false, skip]);
                results.Add(((Block?[])blocks.Clone(), (Block?[])fluids.Clone(), (int[])rgbs.Clone()));
            }
        }
    }

    // The neighbourhood of chunk (1,1,1): matrix chunks around the centre, every fifth missing and every seventh flagged Empty
    private static void Place(ClientWorldMap map, List<Case> matrix, int centre)
    {
        var chunks = (Dictionary<long, ClientChunk>)ChunkRig.Get(map, "chunks");
        chunks.Clear();
        for (var n = 0; n < 27; n++)
        {
            var (x, z, y) = (n % 3, n / 3 % 3, n / 9);
            var center = n == 13;
            if (!center && (n + centre) % 5 == 0) continue;
            var c = matrix[center ? centre : (centre + n) % matrix.Count];
            if (c.Faster == Expect.Throws || c.Range == Expect.Throws) c = matrix[2];
            var chunk = Wrap(c.Data);
            chunk.Empty = !center && (n + centre) % 7 == 0;
            chunks[MapUtil.Index3dL(x, y, z, map.index3dMulX, map.index3dMulZ)] = chunk;
        }
    }

    private static ClientChunk Chunk(ClientWorldMap map, int x, int y, int z) =>
        ((Dictionary<long, ClientChunk>)ChunkRig.Get(map, "chunks"))[
            MapUtil.Index3dL(x, y, z, map.index3dMulX, map.index3dMulZ)];

    private static ClientChunk Wrap(ChunkData data)
    {
        var chunk = ClientChunk.CreateNew(Pool);
        AccessTools.Field(typeof(WorldChunk), "chunkdata").SetValue(chunk, data);
        return chunk;
    }

    // A tesselator with only what BuildExtendedChunkData reads: the game's block list and world map (its chunk dictionary, lock and
    // sunlit EmptyChunk), the three extended arrays, the neighbour arrays, blocksFast and the light converter
    private static (ChunkTesselator, MethodInfo) Tesselator()
    {
        var tesselator = (ChunkTesselator)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselator));
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
        var dataType = typeof(ClientChunk).Assembly.GetType("Vintagestory.Client.NoObf.ClientChunkData")!;
        ChunkRig.Set(tesselator, "game", game);
        ChunkRig.Set(tesselator, "currentChunkBlocksExt", new Block[ExtLength]);
        ChunkRig.Set(tesselator, "currentChunkFluidBlocksExt", new Block[ExtLength]);
        ChunkRig.Set(tesselator, "currentChunkRgbsExt", new int[ExtLength]);
        ChunkRig.Set(tesselator, "chunksNearby", new ClientChunk[27]);
        ChunkRig.Set(tesselator, "chunkdatasNearby", Array.CreateInstance(dataType, 27));
        ChunkRig.Set(tesselator, "blocksFast", BlocksFast);
        ChunkRig.Set(tesselator, "lightConverter", Converter);
        game.Blocks = [.. BlocksFast.Take(KnownBlocks)];
        game.WorldMap = map;
        ChunkRig.Set(map, "chunksLock", new object());
        ChunkRig.Set(map, "chunks", new Dictionary<long, ClientChunk>());
        (map.index3dMulX, map.index3dMulZ) = (3, 3);
        var empty = ClientChunk.CreateNew(Pool);
        empty.Lighting.FillWithSunlight(24);
        empty.Empty = true;
        ChunkRig.Set(map, "EmptyChunk", empty);
        var method = AccessTools.DeclaredMethod(typeof(ChunkTesselator), "BuildExtendedChunkData");
        Assert.That(method, Is.Not.Null);
        return (tesselator, method);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetRange_Faster")]
    private static extern void EngineFaster([UnsafeAccessorType(ClientData)] object data, Block[] blocksExt,
        Block[] fluidsExt, int[] rgbsExt, int extIndex3D, int index3D, int index3DEnd, Block[] blocksFast,
        ColorUtil.LightUtil lightConverter);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetRange")]
    private static extern void EngineRange([UnsafeAccessorType(ClientData)] object data, Block[] blocksExt,
        Block[] fluidsExt, int[] rgbsExt, int extIndex3D, int index3D, int index3DEnd, Block[] blocksFast,
        ColorUtil.LightUtil lightConverter);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BuildFastBlockAccessArray")]
    private static extern void BuildFast([UnsafeAccessorType(ClientData)] object data, Block[] blocks);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "light2")]
    private static extern ref int[][]? Light2([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "light2Lock")]
    private static extern ref FastRWLock Light2Lock([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockAir")]
    private static extern ref Block? BlockAir([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBit1")]
    private static extern ref int[]? DataBit1(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBits")]
    private static extern ref int[][]? DataBits(ChunkDataLayer layer);

    private enum Expect
    {
        Fast, // every row decoded by ExtendedRows
        SomeFallbacks, // some rows handed to the engine
        Throws // the engine throws on some rows, ExtendedRows hands those rows to it
    }

    private sealed record Case(string Name, ChunkData Data, Action Prepare, Expect Faster, Expect Range)
    {
        public override string ToString() => Name;
    }

    private delegate void RowReader(ChunkData data, Block[] blocksExt, Block[] fluidsExt, int[] rgbsExt, int extIndex3D,
        int index3D, int index3DEnd, Block[] blocksFast, ColorUtil.LightUtil lightConverter);

    private sealed class Output
    {
        public readonly Block?[] Blocks = Filled<Block?>(Sentinel);
        public readonly Block?[] Fluids = Filled<Block?>(Sentinel);
        public readonly int[] Rgbs = Filled(unchecked((int)0xDEADBEEF));
        public readonly List<string> Thrown = [];
        public Block? AirAfter;

        private static T[] Filled<T>(T value)
        {
            var array = new T[ExtLength];
            Array.Fill(array, value);
            return array;
        }
    }
}
