using Komet.World;

namespace Komet.Test.World;

// The four rewrites find their one site each; the pools hand back what a new object or array would be
[NonParallelizable]
public sealed class WorldGenScratchTests
{
    [Test]
    public void AllFourAreRewritten()
    {
        var harmony = new Harmony("komet-test-worldgenscratch");
        try
        {
            WorldGenScratch.Install(harmony);
            Assert.That(WorldGenScratch.Rewritten, Is.EqualTo(WorldGenScratch.AllBits));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    public void APoppedPositionIsReusedOnlyAfterTheNextPop()
    {
        var stack = new Stack<BlockPos>();
        stack.Push(new BlockPos(1, 2, 3, 0));
        stack.Push(new BlockPos(4, 5, 6, 1));
        var first = WorldGenScratch.Pop(stack);
        var fresh = WorldGenScratch.Take(7, 8, 9, 0);
        var second = WorldGenScratch.Pop(stack);
        var reused = WorldGenScratch.Take(10, 11, 12, 2);
        Assert.Multiple(() =>
        {
            Assert.That(fresh, Is.Not.SameAs(first), "the popped position is still being read");
            Assert.That((second.X, second.Y, second.Z), Is.EqualTo((1, 2, 3)));
            Assert.That(reused, Is.SameAs(first), "the previous one comes back once the next is popped");
            Assert.That((reused.X, reused.Y, reused.Z, reused.dimension), Is.EqualTo((10, 11, 12, 2)));
        });
    }

    [Test]
    public void TheWeightsComeBackClearedAndOfTheAskedLength()
    {
        var weights = WorldGenScratch.Weights(5);
        weights[3] = 1;
        var again = WorldGenScratch.Weights(5);
        Assert.Multiple(() =>
        {
            Assert.That(again, Is.SameAs(weights));
            Assert.That(again, Is.All.Zero);
            Assert.That(WorldGenScratch.Weights(7), Has.Length.EqualTo(7));
        });
    }

    [Test]
    public void ACodesLastPartIsTheEnginesAnswer()
    {
        var block = new Block { Code = new AssetLocation("game:rock-granite") };
        Assert.Multiple(() =>
        {
            Assert.That(WorldGenScratch.LastCodePart(block, 0), Is.EqualTo(block.LastCodePart(0)));
            Assert.That(WorldGenScratch.LastCodePart(block, 0), Is.EqualTo("granite"));
            Assert.That(WorldGenScratch.LastCodePart(block, 1), Is.EqualTo("rock"));
        });
    }
}
