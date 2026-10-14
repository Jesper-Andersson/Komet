using Komet.Core;
using Komet.Tessellation;
using Vintagestory.API.Client.Tesselation;

namespace Komet.Test.Tessellation;

// Golden test of VisibleFaces against the engine's own ChunkTesselator.CalculateVisibleFaces, unpatched, on the same halo, with
// blocks that record every virtual call the engine makes with its arguments and position. Both runs start from the same random
// buffer, so the stale centre bytes of skipChunkCenter are compared too. The buffer, the result and the call log must be identical;
// where the sweep hands the chunk to the engine (an unknown mode), it must have called nothing, and the engine run on the sweep's
// buffer must end where the engine alone did.
public sealed class VisibleFacesGoldenTests
{
    private const int Worlds = 300, Seed = 20260923;

    private static readonly EnumFaceCullMode[] Modes = Enum.GetValues<EnumFaceCullMode>();

    private static readonly EnumDrawType[] DrawTypes =
    [
        EnumDrawType.JSON, EnumDrawType.JSONAndSnowLayer, EnumDrawType.JSONAndWater, EnumDrawType.Cube, EnumDrawType.Liquid,
        EnumDrawType.TopSoil, EnumDrawType.Cross, EnumDrawType.Empty, EnumDrawType.Transparent, EnumDrawType.BlockLayer_3,
        EnumDrawType.CrossAndSnowlayer, EnumDrawType.SurfaceLayer
    ];

    private static readonly EnumBlockMaterial[] Materials =
    [EnumBlockMaterial.Stone, EnumBlockMaterial.Leaves, EnumBlockMaterial.Snow, EnumBlockMaterial.Soil, EnumBlockMaterial.Water];

    [TearDown]
    public void Reset() => (VisibleFaces.Enabled, Counting.Hud) = (true, false);

    [Test]
    [Category("Slow")]
    public void SweepMatchesTheEngineOnRandomWorlds()
    {
        var r = new Random(Seed);
        int answered = 0, ported = 0, calls = 0, snowCalls = 0;
        for (var world = 0; world < Worlds; world++)
        {
            var snowy = world % 5 >= 3;
            var palette = Palette(r, world % 7 == 3 ? 3000 : 2 + r.Next(40), snowy);
            using var rig = new TessRig(palette);
            Fill(rig, r, palette, world);
            foreach (var skip in (bool[])[false, true])
            {
                var outcome = Compare(rig, r, skip, world * 32 + 7, world % 9 * 32, 64);
                Assert.That(outcome.Result, Is.GreaterThanOrEqualTo(0),
                    $"world {world}: every mode is known, yet handed back");
                (answered, ported, calls, snowCalls) = (answered + 1, ported + outcome.Ported, calls + outcome.Calls,
                    snowCalls + outcome.SnowCalls);
            }
        }

        TestContext.Out.WriteLine(
            $"chunks answered {answered}, ported cells {ported}, engine calls {calls}, of them snow {snowCalls}");
        Assert.Multiple(() =>
        {
            Assert.That(ported, Is.GreaterThan(100_000), "the port ran");
            Assert.That(calls, Is.GreaterThan(10_000), "the port made virtual calls");
            Assert.That(snowCalls, Is.GreaterThan(10_000), "snow cover and snow layers were asked");
        });
    }

    // Every cell has one mode: the port on its own, including the calls on neighbours at the halo's edge, with and without snow
    // cover above (then Default cells are ported too)
    [Test]
    [Category("Slow")]
    public void PortMatchesTheEngineForEveryMode()
    {
        var r = new Random(Seed + 1);
        foreach (var mode in Modes)
        {
            var ported = 0;
            for (var world = 0; world < 20; world++)
            {
                var palette = Palette(r, 12, world % 4 >= 2);
                for (var i = 1; i < palette.Length; i++) palette[i].FaceCullMode = mode;
                using var rig = new TessRig(palette);
                Fill(rig, r, palette, world);
                var outcome = Compare(rig, r, world % 2 == 1, 3200 + world, 96, -64);
                Assert.That(outcome.Result, Is.GreaterThanOrEqualTo(0), $"{mode}");
                ported += outcome.Ported;
            }

            Assert.That(ported, Is.GreaterThan(100), $"{mode} went through the port");
        }
    }

    // The snow rule on the top face: a JSONAndSnowLayer block above that is opaque downward has the cell's AllowSnowCoverage asked,
    // at the cell's position, in the engine's order; not opaque downward, nothing is asked
    [Test]
    public void SnowCoveredNeighbourIsAskedInOrder()
    {
        var palette = Palette(new Random(3), 4, false);
        var ground = new CullRecordingBlock
        { BlockId = 1, DrawType = EnumDrawType.Cube, SideOpaque = new SmallBoolArray(63) };
        var snow = new CullRecordingBlock
        { BlockId = 3, DrawType = EnumDrawType.JSONAndSnowLayer, SideOpaque = new SmallBoolArray(32) };
        (palette[1], palette[3]) = (ground, snow);
        using var rig = new TessRig(palette);
        Array.Fill(rig.Solid, ground);
        // halo coordinates: the cell below each is one of the chunk's, corners and the top layer included
        foreach (var (x, y, z) in (ReadOnlySpan<(int, int, int)>)
                 [(5, 7, 9), (1, 2, 1), (32, 33, 32), (12, 33, 3), (20, 2, 20)])
            rig.Solid[Ext(x, y, z)] = snow;
        var outcome = Compare(rig, new Random(4), false, 64, 32, 96);
        Assert.That((outcome.Result, outcome.SnowCalls), Is.EqualTo((1, 5)),
            "the ground cell under each snow cell asked once");
        snow.SideOpaque = new SmallBoolArray(31); // not opaque downward: no call
        Assert.That(Compare(rig, new Random(4), true, 64, 32, 96).SnowCalls, Is.Zero);
    }

    [Test]
    public void UnknownModeHandsTheChunkBack()
    {
        var r = new Random(Seed + 7);
        var palette = Palette(r, 8, true);
        palette[5].FaceCullMode = (EnumFaceCullMode)42;
        using var rig = new TessRig(palette);
        Fill(rig, r, palette, 6);
        Assert.That(Compare(rig, r, false, 0, 0, 0).Result, Is.EqualTo(-1));
    }

    // A chunk of air: no cell is drawn, the result is false, and the skipped centre is written too
    [Test]
    public void AirChunkIsFalseAndZeroed()
    {
        var palette = Palette(new Random(5), 3, false);
        using var rig = new TessRig(palette);
        Array.Fill(rig.Solid, palette[0]);
        foreach (var skip in (bool[])[false, true])
            Assert.That(Compare(rig, new Random(6), skip, 0, 0, 0).Result, Is.Zero);
    }

    // Through Harmony: the patched method answers what the unpatched one answers, and a non-standard MoveIndex goes to the engine
    [Test]
    public void PatchedMethodMatchesTheEngine()
    {
        var r = new Random(Seed + 2);
        using var harmony = new TestHarmony("komet-test-visiblefaces");
        VisibleFaces.Install(harmony);
        Assert.That(VisibleFaces.Installed, Is.True);
        Counting.Hud = true;
        var (chunks, cells) = (VisibleFaces.Chunks + VisibleFaces.Fallbacks, VisibleFaces.FastCells);
        for (var world = 0; world < 40; world++)
        {
            var palette = Palette(r, 2 + r.Next(30), world % 4 == 3);
            using var rig = new TessRig(palette);
            Fill(rig, r, palette, world);
            var start = new byte[TessRig.Cells];
            r.NextBytes(start);
            var skip = world % 2 == 0;
            VisibleFaces.Enabled = false;
            var (engine, engineResult) = Run(rig, start, skip);
            VisibleFaces.Enabled = true;
            var (fast, fastResult) = Run(rig, start, skip);
            var world1 = world;
            Assert.Multiple(() =>
            {
                Assert.That(fastResult, Is.EqualTo(engineResult), $"result of world {world1}");
                Assert.That(fast, Is.EqualTo(engine), $"buffer of world {world1}");
            });
        }

        Assert.That(VisibleFaces.Chunks + VisibleFaces.Fallbacks - chunks, Is.EqualTo(40));
        Assert.That(VisibleFaces.FastCells - cells, Is.GreaterThan(0));
    }

    // A prefix on the method would be skipped with the original: another mod's, and another Komet class's under the same Harmony id,
    // stand the sweep down - found by the tesselation pass's recheck -, and it is back once they are gone
    [Test]
    public void OtherPrefixesSendChunksToTheEngine()
    {
        var palette = Palette(new Random(Seed + 5), 6, false);
        using var rig = new TessRig(palette);
        Fill(rig, new Random(Seed + 6), palette, 1);
        using var harmony = new TestHarmony("komet-test-visiblefaces-own");
        using var other = new TestHarmony("komet-test-visiblefaces-other");
        TessSeams.Install(harmony, null);
        VisibleFaces.Install(harmony);
        Counting.Hud = true;
        var chunks = VisibleFaces.Chunks;
        _ = rig.Tesselator.CalculateVisibleFaces(false, 0, 0, 0);
        Assert.That((VisibleFaces.Chunks - chunks, VisibleFaces.StoodDown), Is.EqualTo((1L, false)));
        _ = other.Patch(VisibleFaces.Target(), Foreign.Prefix);
        // a pass begins, and the first one rechecks; -1 returns at once
        _ = rig.Tesselator.NowProcessChunk(-1, 0, 0, null!, false);
        _ = rig.Tesselator.CalculateVisibleFaces(false, 0, 0, 0);
        Assert.That((VisibleFaces.Chunks - chunks, VisibleFaces.StoodDown), Is.EqualTo((1L, true)), "another mod");
        other.UnpatchAll(other.Id);
        VisibleFaces.Recheck();
        _ = rig.Tesselator.CalculateVisibleFaces(false, 0, 0, 0);
        Assert.That((VisibleFaces.Chunks - chunks, VisibleFaces.StoodDown), Is.EqualTo((2L, false)), "back");
        _ = harmony.Patch(VisibleFaces.Target(), Foreign.Prefix);
        VisibleFaces.Recheck();
        _ = rig.Tesselator.CalculateVisibleFaces(false, 0, 0, 0);
        Assert.That((VisibleFaces.Chunks - chunks, VisibleFaces.StoodDown), Is.EqualTo((2L, true)),
            "the same id, another class");
    }

    [Test]
    public void NonStandardMoveIndexGoesToTheEngine()
    {
        var palette = Palette(new Random(7), 5, false);
        using var rig = new TessRig(palette);
        Fill(rig, new Random(8), palette, 1);
        TileSideEnum.MoveIndex[2] = 35;
        Assert.That(VisibleFaces.Compute(rig.Tesselator, new byte[TessRig.Cells], false, 0, 0, 0), Is.EqualTo(-1));
    }

    // Komet.Testing cannot see TessSeams: ChunkRig's own copy of the standard MoveIndex must stay the one Komet's paths take
    [Test]
    public void ChunkRigMovesAreTheStandardOnes() => Assert.That(ChunkRig.Moves, Is.EqualTo(TessSeams.Moves));

    private static Outcome Compare(TessRig rig, Random r, bool skip, int baseX, int baseY, int baseZ)
    {
        var start = new byte[TessRig.Cells];
        r.NextBytes(start);
        var engine = (byte[])start.Clone();
        var fast = (byte[])start.Clone();
        rig.DrawBuffer = engine;
        _ = rig.Pos.Set(-1, -1, -1);
        var engineLog = CullRecordingBlock.Log = [];
        var engineResult = rig.Tesselator.CalculateVisibleFaces(skip, baseX, baseY, baseZ);
        var enginePos = (rig.Pos.X, rig.Pos.Y, rig.Pos.Z);
        var fastLog = CullRecordingBlock.Log = [];
        _ = rig.Pos.Set(-1, -1, -1);
        var result = VisibleFaces.Compute(rig.Tesselator, fast, skip, baseX, baseY, baseZ);
        CullRecordingBlock.Log = null;
        if (result < 0)
        {
            Assert.That(fastLog, Is.Empty, "handed back after calling a block");
            rig.DrawBuffer = fast; // what the prefix does next: the engine on the buffer the sweep wrote into
            _ = rig.Tesselator.CalculateVisibleFaces(skip, baseX, baseY, baseZ);
            Assert.That(fast, Is.EqualTo(engine), "the engine rewrites what the sweep wrote");
            return new Outcome(result, 0, engineLog.Count, 0);
        }

        var ported = CountPorted(rig, skip);
        Assert.Multiple(() =>
        {
            Assert.That(result == 1, Is.EqualTo(engineResult), "result");
            Assert.That(fast, Is.EqualTo(engine), $"buffer at {Difference(fast, engine)}");
            Assert.That(fastLog, Is.EqualTo(engineLog), "virtual calls, their arguments and order");
            Assert.That((rig.Pos.X, rig.Pos.Y, rig.Pos.Z), Is.EqualTo(enginePos), "tmpPos left behind");
        });
        return new Outcome(result, ported, fastLog.Count,
            fastLog.Count(call => call.Method is CullRecordingBlock.Snow or CullRecordingBlock.Level));
    }

    private static (byte[] Buffer, bool Result) Run(TessRig rig, byte[] start, bool skip)
    {
        var buffer = (byte[])start.Clone();
        rig.DrawBuffer = buffer;
        CullRecordingBlock.Log = [];
        var result = rig.Tesselator.CalculateVisibleFaces(skip, 32, 64, 96);
        CullRecordingBlock.Log = null;
        return (buffer, result);
    }

    private static int CountPorted(TessRig rig, bool skip)
    {
        var count = 0;
        for (var i = 0; i < TessRig.Cells; i++)
        {
            var (x, y, z) = (i & 31, i >> 10, (i >> 5) & 31);
            var (block, up) = (rig.Solid[Ext(x + 1, y + 1, z + 1)], rig.Solid[Ext(x + 1, y + 2, z + 1)]);
            var centre = x is > 0 and < 31 && y is > 0 and < 31 && z is > 0 and < 31;
            var snowy = up.DrawType == EnumDrawType.JSONAndSnowLayer && up.SideOpaque[5];
            if (block != rig.Air && !(skip && centre) &&
                (block.FaceCullMode != EnumFaceCullMode.Default || snowy)) count++;
        }

        return count;
    }

    private static string Difference(byte[] a, byte[] b)
    {
        var at = a.AsSpan().CommonPrefixLength(b);
        return at == a.Length
            ? "none"
            : $"cell {at} (x {at & 31}, y {at >> 10}, z {(at >> 5) & 31}): {a[at]} vs {b[at]}";
    }

    private static int Ext(int x, int y, int z) => (y * TessRig.Ext + z) * TessRig.Ext + x;

    // Without snow no block is MergeSnowLayer and no JSONAndSnowLayer block is opaque downward
    private static Block[] Palette(Random r, int count, bool snowy)
    {
        var palette = new Block[count];
        palette[0] = new Block { BlockId = 0, DrawType = EnumDrawType.Empty, SideOpaque = new SmallBoolArray(0) };
        for (var i = 1; i < count; i++)
        {
            var block = r.Next(3) == 0 ? new CullRecordingBlock() : new Block();
            block.BlockId = i;
            block.FaceCullMode = r.Next(2) == 0 ? EnumFaceCullMode.Default : Modes[r.Next(Modes.Length)];
            if (!snowy && block.FaceCullMode == EnumFaceCullMode.MergeSnowLayer)
                block.FaceCullMode = EnumFaceCullMode.Stairs;
            block.SideOpaque = new SmallBoolArray(r.Next(4) == 0 ? 63 : r.Next(256));
            block.SideSolid = new SmallBoolArray(r.Next(256));
            block.DrawType = DrawTypes[r.Next(DrawTypes.Length)];
            if (!snowy && block.DrawType == EnumDrawType.JSONAndSnowLayer)
                block.SideOpaque = new SmallBoolArray(block.SideOpaque & ~32);
            block.BlockMaterial = Materials[r.Next(Materials.Length)];
            block.snowLevel = r.Next(4);
            palette[i] = block;
        }

        return palette;
    }

    // Kinds of worlds by index: random at 0, 30, 70 and 95 % air, layered terrain, one block everywhere, and all-distinct neighbours
    private static void Fill(TessRig rig, Random r, Block[] palette, int world)
    {
        var solid = rig.Solid;
        var kind = world % 7;
        double[] air = [0, 0.3, 0.7, 0.95];
        for (var i = 0; i < solid.Length; i++)
        {
            var (x, z, y) = (i % 34, i / 34 % 34, i / (34 * 34));
            solid[i] = kind switch
            {
                < 4 => r.NextDouble() < air[kind] ? palette[0] : palette[1 + r.Next(palette.Length - 1)],
                4 => y > 12 + (x * 7 + z * 3) % 9
                    ? palette[0]
                    : palette[1 + (y / 5 + r.Next(3)) % (palette.Length - 1)],
                5 => palette[palette.Length - 1],
                _ => palette[1 + r.Next(palette.Length - 1)]
            };
            rig.Fluid[i] = palette[0];
        }
    }

    private readonly record struct Outcome(int Result, int Ported, int Calls, int SnowCalls);
}

internal sealed class CullRecordingBlock : Block
{
    public const int Snow = 1, Solid = 2, Merge = 3, Level = 4;

    public static List<(int Method, int Block, int A, int B, int X, int Y, int Z)>? Log { get; set; }

    public override bool AllowSnowCoverage(IWorldAccessor world, BlockPos blockPos)
    {
        Log?.Add((Snow, BlockId, 0, 0, blockPos.X, blockPos.Y, blockPos.Z));
        return TessMix.Bit(Snow, BlockId, blockPos.X, blockPos.Y, blockPos.Z);
    }

    public override bool SideIsSolid(BlockPos pos, int faceIndex)
    {
        Log?.Add((Solid, BlockId, faceIndex, 0, pos.X, pos.Y, pos.Z));
        return TessMix.Bit(Solid, BlockId, faceIndex, pos.X, pos.Y, pos.Z);
    }

    public override bool ShouldMergeFace(int facingIndex, Block neighbourBlock, int intraChunkIndex3d)
    {
        Log?.Add((Merge, BlockId, facingIndex, neighbourBlock.BlockId, intraChunkIndex3d, 0, 0));
        return TessMix.Bit(Merge, BlockId, facingIndex, neighbourBlock.BlockId, intraChunkIndex3d);
    }

    public override float GetSnowLevel(BlockPos pos)
    {
        Log?.Add((Level, BlockId, 0, 0, pos?.X ?? -1, 0, 0));
        return snowLevel;
    }
}
