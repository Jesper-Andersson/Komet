using Komet.Tessellation;
using Vintagestory.GameContent;

namespace Komet.Test.Tessellation;

public sealed class TessBlockPosTests
{
    private const int Calls = 1000;

    [TearDown]
    public void Restore() => TessBlockPos.Enabled = true;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-tessblockpos");
        TessBlockPos.Install(harmony);
        Assert.That(TessBlockPos.Rewritten, Is.True,
            "TesselateBlock or BuildDecorPolygons no longer ask with a new BlockPos");
    }

    // The vanilla blocks never read the position; one that keeps it or hands it on is not proven, one that reads its fields is
    [TestCase(typeof(BlockPlant), true)]
    [TestCase(typeof(BlockCrop), true)]
    [TestCase(typeof(BlockDeadCrop), true)]
    [TestCase(typeof(Keeper), false)]
    [TestCase(typeof(Passer), false)]
    [TestCase(typeof(Reader), true)]
    public void AnImplementationIsProvenFromItsIl(Type type, bool proven) =>
        Assert.That(TessBlockPos.Confined(type), Is.EqualTo(proven));

    // At sets its position as the constructor sets a new one, dimension included
    [TestCase(10, 110, -20)]
    [TestCase(512000, 32768 + 70, 511999)]
    [TestCase(-5, 3 * 32768 + 1, 7)]
    public void TheThreadsPositionIsWhatTheConstructorWouldHaveBuilt(int x, int y, int z)
    {
        var (mine, engine) = (TessBlockPos.At(x, y, z), new BlockPos(x, y, z));
        Assert.That((mine.X, mine.Y, mine.Z, mine.dimension),
            Is.EqualTo((engine.X, engine.Y, engine.Z, engine.dimension)));
    }

    [Test]
    public void OnlyAProvenBlockGetsTheThreadsPosition()
    {
        var (reader, keeper) = (new Reader(), new Keeper());
        Assert.That(TessBlockPos.Adjust(reader, TessBlockPos.At(1, 32768 + 2, 3), [], 0), Is.EqualTo(1_002_001));
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++) _ = TessBlockPos.Adjust(reader, TessBlockPos.At(i, i, i), [], 0);
        var proven = GC.GetAllocatedBytesForCurrentThread() - start;
        var scratch = TessBlockPos.At(4, 32768 + 5, 6);
        _ = TessBlockPos.Adjust(keeper, scratch, [], 0);
        TessBlockPos.Enabled = false;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++) _ = TessBlockPos.Adjust(reader, TessBlockPos.At(i, i, i), [], 0);
        var off = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Multiple(() =>
        {
            Assert.That(proven, Is.Zero, "a proven block needs no position of its own");
            Assert.That(keeper.Kept, Is.Not.SameAs(scratch));
            Assert.That((keeper.Kept!.X, keeper.Kept.Y, keeper.Kept.Z, keeper.Kept.dimension),
                Is.EqualTo((4, 5, 6, 1)));
            Assert.That(off, Is.GreaterThanOrEqualTo(Calls * 24L), "switched off every call builds its own");
        });
    }

    private sealed class Keeper : Block, IDrawYAdjustable
    {
        public BlockPos? Kept { get; private set; }

        public float AdjustYPosition(BlockPos pos, Block[] chunkExtBlocks, int extIndex3d)
        {
            Kept = pos;
            return 0;
        }
    }

    private sealed class Passer : Block, IDrawYAdjustable
    {
        public float AdjustYPosition(BlockPos pos, Block[] chunkExtBlocks, int extIndex3d) => pos.Copy().X;
    }

    private sealed class Reader : Block, IDrawYAdjustable
    {
        public float AdjustYPosition(BlockPos pos, Block[] chunkExtBlocks, int extIndex3d) =>
            pos.X + 1000 * pos.Y + 1_000_000 * pos.dimension;
    }
}
