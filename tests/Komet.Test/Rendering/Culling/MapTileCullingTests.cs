using Komet.Core;
using Komet.Rendering;
using Vintagestory.GameContent;

namespace Komet.Test.Rendering;

// MultiChunkMapComponent.Render skips only the draw of a tile whose rectangle, placed as the engine places it, lies wholly outside
// the map's inner bounds by more than a pixel; a tile touching the edge, and everything the prefix cannot judge, is drawn.
[NonParallelizable]
public sealed class MapTileCullingTests
{
    [TearDown]
    public void Restore() => MapTileCulling.Enabled = true;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-maptileculling");
        MapTileCulling.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(MapTileCulling.Seams()[0], Is.Not.Null, "the essentials mod's map tile");
            Assert.That(MapTileCulling.Matched, Is.True,
                $"MultiChunkMapComponent.Render is not the body verified: 0x{EngineShape.Of(MapTileCulling.Seams()):X16}UL");
            Assert.That(MapTileCulling.Blocked, Is.False);
            Assert.That(Harmony.GetPatchInfo(MapTileCulling.Seams()[0])?.Prefixes.Select(p => p.owner),
                Is.EqualTo([harmony.Id]));
        });
    }

    // A 200 pixel map at (10, 20) showing 200 blocks from (1000, 2000): one pixel a block, a tile 96 of each
    [Test]
    public void CullsOnlyTilesWhollyOutsideTheMap()
    {
        using var harmony = Installed();
        var map = Map(new Cuboidd(1000, 0, 2000, 1200, 0, 2200), 1);
        Assert.Multiple(() =>
        {
            Assert.That(Drawn(map, 1000, 2000), Is.True, "in the corner");
            Assert.That(Drawn(map, 1150, 2150), Is.True, "half in");
            Assert.That(Drawn(map, 904, 2000), Is.True, "its right edge on the map's left");
            Assert.That(Drawn(map, 800, 2000), Is.False, "left of it");
            Assert.That(Drawn(map, 1210, 2000), Is.False, "right of it");
            Assert.That(Drawn(map, 1000, 1800), Is.False, "above it");
            Assert.That(Drawn(map, 1000, 2500), Is.False, "below it");
        });
    }

    // Zoomed in twice: 100 blocks fill the map and a tile is 192 pixels wide
    [Test]
    public void TheTileGrowsWithTheZoom()
    {
        using var harmony = Installed();
        var map = Map(new Cuboidd(1000, 0, 2000, 1100, 0, 2100), 2);
        Assert.Multiple(() =>
        {
            Assert.That(Drawn(map, 910, 2000), Is.True, "reaching 12 pixels in");
            Assert.That(Drawn(map, 900, 2000), Is.False, "ending 8 pixels short");
        });
    }

    [Test]
    public void EverythingItCannotJudgeIsDrawn()
    {
        using var harmony = Installed();
        var (map, far) = (Map(new Cuboidd(1000, 0, 2000, 1200, 0, 2200), 1), new Vec3d(5000, 0, 5000));
        Assert.Multiple(() =>
        {
            Assert.That(MapTileCulling.Render(map, null, far), Is.True, "no texture");
            Assert.That(MapTileCulling.Render(map, Tile(), null), Is.True, "no position");
            Assert.That(MapTileCulling.Render(Map(new Cuboidd(1000, 0, 2000, 1000, 0, 2200), 1), Tile(), far), Is.True,
                "an empty view");
            Assert.That(MapTileCulling.Render(Map(null!, 1), Tile(), far), Is.True, "no view");
            Assert.That(MapTileCulling.Render(Map(new Cuboidd(1000, 0, 2000, 1200, 0, 2200), float.NaN), Tile(), far),
                Is.True, "no zoom");
            MapTileCulling.Enabled = false;
            Assert.That(MapTileCulling.Render(map, Tile(), far), Is.True, "switched off");
        });
    }

    private static TestHarmony Installed()
    {
        var harmony = new TestHarmony("komet-test-maptileculling");
        MapTileCulling.Install(harmony, new QuietLogger());
        Assert.That(MapTileCulling.Blocked, Is.False);
        return harmony;
    }

    private static bool Drawn(GuiElementMap map, double x, double z) => MapTileCulling.Render(map, Tile(), new Vec3d(x, 0, z));

    private static LoadedTexture Tile() => new(null!, 0, 96, 96); // no texture id: nothing to dispose

    private static GuiElementMap Map(Cuboidd view, float zoom)
    {
        var map = (GuiElementMap)RuntimeHelpers.GetUninitializedObject(typeof(GuiElementMap));
        (map.Bounds, map.CurrentBlockViewBounds, map.ZoomLevel) = (new Placed(10, 20, 200, 200), view, zoom);
        return map;
    }

    private sealed class Placed(double x, double y, double width, double height) : ElementBounds
    {
        public override double renderX => x;
        public override double renderY => y;
        public override double InnerWidth => width;
        public override double InnerHeight => height;
    }
}
