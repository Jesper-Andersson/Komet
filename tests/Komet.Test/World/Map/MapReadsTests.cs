using Komet.World;

namespace Komet.Test.World;

// The rewrite finds the block reads in the engine's GenerateChunkImage, and a read outside a map piece stays the engine's
public sealed class MapReadsTests
{
    [Test]
    public void TheMapsReadsAreRewritten()
    {
        var harmony = new Harmony("komet-test-mapreads");
        try
        {
            MapReads.Install(harmony);
            Assert.That(MapReads.Rewritten, Is.True);
            var chunk = Answers.Of<IWorldChunk>(new() { ["UnpackAndReadBlock"] = args => (int)args![0]! + 7 });
            Assert.That(MapReads.Read(chunk, 5, 3), Is.EqualTo(12));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
