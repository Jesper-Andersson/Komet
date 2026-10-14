using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// MultiChunkMapComponent.FinishSetChunks is taken over only for pieces the engine's draw copies texel for texel: 32x32 and every
// pixel opaque; anything else, and a switched off or unverified feature, is the engine's
[NonParallelizable]
public sealed class MapTileUploadTests
{
    [TearDown]
    public void Restore() => MapTileUpload.Enabled = true;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-maptileupload");
        MapTileUpload.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(MapTileUpload.Seams()[0], Is.Not.Null, "the essentials mod's map tile");
            Assert.That(MapTileUpload.Matched, Is.True,
                $"MultiChunkMapComponent.FinishSetChunks is not the body verified: 0x{EngineShape.Of(MapTileUpload.Seams()):X16}UL");
            Assert.That(MapTileUpload.Blocked, Is.False);
            Assert.That(Harmony.GetPatchInfo(MapTileUpload.Seams()[0])?.Prefixes.Select(p => p.owner),
                Is.EqualTo([harmony.Id]));
        });
    }

    private static int[] Piece(int alpha = 0xFF) =>
        [.. Enumerable.Range(0, 1024).Select(i => alpha << 24 | i * 2713 & 0xFFFFFF)];

    [Test]
    public void TakesOverOnlyOpaqueWholePieces()
    {
        var translucent = Piece();
        translucent[517] &= 0x7FFFFFFF;
        Assert.Multiple(() =>
        {
            Assert.That(MapTileUpload.Uploadable([Piece(), null, null, null, Piece(), null, null, null, Piece()]), Is.True);
            Assert.That(MapTileUpload.Uploadable(new int[]?[9]), Is.True, "nothing to set");
            Assert.That(MapTileUpload.Uploadable([Piece(), translucent, null, null, null, null, null, null, null]), Is.False,
                "one pixel not opaque");
            Assert.That(MapTileUpload.Uploadable([Piece(0), null, null, null, null, null, null, null, null]), Is.False,
                "transparent");
            Assert.That(MapTileUpload.Uploadable([new int[512], null, null, null, null, null, null, null, null]), Is.False,
                "not 32x32");
        });
    }

    [Test]
    public void EverythingElseIsTheEngines()
    {
        using var harmony = new TestHarmony("komet-test-maptileupload");
        MapTileUpload.Install(harmony, new QuietLogger());
        LoadedTexture? texture = null;
        int[]?[]? pieces = [Piece(), null, null, null, null, null, null, null, null];
        Assert.Multiple(() =>
        {
            Assert.That(MapTileUpload.Finish(ref texture, ref pieces, new int[96 * 96], null), Is.True, "no client");
            int[]?[]? none = null;
            Assert.That(MapTileUpload.Finish(ref texture, ref none, new int[96 * 96], null), Is.True, "nothing to set");
            MapTileUpload.Enabled = false;
            Assert.That(MapTileUpload.Finish(ref texture, ref pieces, new int[96 * 96], null), Is.True, "switched off");
            Assert.That(pieces, Is.Not.Null, "left for the engine");
        });
    }
}
