using Komet.Shapes;

namespace Komet.Test.Shapes;

// Creature items and wearables build their inventory mesh from shapes parsed without the animations: the same elements and
// textures, no animations, and both engine builders found and rewritten
[NonParallelizable]
public sealed class InventoryShapesTests
{
    // The JSON with the root object's "animations" value replaced by null, as the scan finds it
    private static string WithoutAnimations(string json) =>
        InventoryShapes.Animations(json, out var from, out var to) ? string.Concat(json.AsSpan(0, from), "null", json.AsSpan(to)) : json;

    [Test]
    public void OnlyTheRootsAnimationsGo()
    {
        const string json = """
            { "textures": { "a": "x" }, /* "animations": [ */ "elements": [ { "name": "a\"]", "animations": [1] } ],
              // "animations": 1
              'Animations': [ { "code": "walk", "keyframes": [ { "frame": 0, "elements": { "]": {} } } ] } ], "textureWidth": 32 }
            """;
        var lean = WithoutAnimations(json);
        Assert.That(lean, Does.Contain("'Animations': null, \"textureWidth\": 32"));
        Assert.That(lean, Does.Contain("\"animations\": [1]"), "an element's own key stays");
        Assert.That(WithoutAnimations("{ \"elements\": [] }"), Is.EqualTo("{ \"elements\": [] }"));
    }

    // Read from the asset's bytes through the pooled buffer: the same shape as the text without the animations, BOM or not
    [TestCase(false)]
    [TestCase(true)]
    public void TheAssetReadsAsItsTextWouldWithoutAnimations(bool bom)
    {
        GameInstall.RequireAssets();
        var bytes = File.ReadAllBytes(Path.Combine(GameInstall.Assets, "survival/shapes/entity/animal/mammal/hooved/deer/moose/adult.json"));
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        var asset = new Vintagestory.Common.Asset(bytes, new AssetLocation("game:shapes/entity/animal/mammal/hooved/deer/moose/adult.json"), null);
        var lean = InventoryShapes.Lean(asset, null)!;
        var full = asset.ToObject<Shape>();
        Assert.Multiple(() =>
        {
            Assert.That(lean.Animations, Is.Null);
            Assert.That(Names(lean.Elements), Is.EqualTo(Names(full.Elements)));
            Assert.That(lean.Textures.Keys, Is.EquivalentTo(full.Textures.Keys));
        });
    }

    [Test]
    public void BothBuildersAreRewritten()
    {
        var harmony = new Harmony("komet-test-inventoryshapes");
        try
        {
            InventoryShapes.Install(harmony);
            Assert.That((InventoryShapes.Creatures, InventoryShapes.Wearables), Is.EqualTo((true, true)));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    [Test]
    public void TheIconBudgetFindsTheItemRenderer() => Assert.That(IconBudget.Target(), Is.Not.Null);

    private static List<string> Names(ShapeElement[]? elements) =>
        elements is null ? [] : [.. elements.SelectMany(e => (List<string>)[e.Name + ":" + e.FacesResolved?.Count(f => f is not null), .. Names(e.Children)])];
}
