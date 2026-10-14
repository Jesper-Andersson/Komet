using Komet.Core;
using Komet.Hud;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Hud;

// The overlay shows up to three mods by name: a click pins one, a second click unpins it, a fourth pin drops the oldest; each change
// is announced, so the overlay redraws and the file is saved
public sealed class HudCornerTests
{
    private static readonly string[] Kept = ["b", "d"];
    private int[] _defaults = [];

    [SetUp]
    public void Save()
    {
        _defaults = Knobs.Snapshot();
    }

    [TearDown]
    public void Restore()
    {
        _ = Knobs.Apply(_defaults);
    }

    [Test]
    public void PinnedModsToggleAndKeepTheNewestThree()
    {
        var (settings, count) = (new HudSettings(), 0);
        settings.Changed += () => count++;
        foreach (var mod in (string[])["a", "b", "c", "d"]) settings.TogglePin(mod);
        settings.TogglePin("c");
        Assert.Multiple(() =>
        {
            Assert.That(settings.PinnedMods, Is.EqualTo(Kept));
            Assert.That(count, Is.EqualTo(5));
        });
    }

    // NearFirst and PoolFragments are the names the knobs had before a rename; the file keeps them
    [Test]
    public void KnobsKeepTheirKeysInTheFile()
    {
        var (schedule, pool, budget) =
            (Knobs.Find("TessSchedule"), Knobs.Find("MeshPool"), Knobs.Find("EntityTessBudget"));
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "NearFirst": false, "PoolFragments": 0, "EntityTessBudget": 7, "UploadCap": 99, "CullGrid": true }""")
            !;
        var logger = new CapturingLogger();
        settings.Knobs.Apply(logger, HudSettings.FileName);
        var file = JObject.Parse(JsonConvert.SerializeObject(settings));
        Assert.Multiple(() =>
        {
            Assert.That((settings.Knobs.Knob(schedule), settings.Knobs.Knob(pool), settings.Knobs.Knob(budget)), Is.EqualTo((0, 0, 7)));
            Assert.That((TessSchedule.Enabled, MeshPool.Enabled, EntityTessBudget.Millis),
                Is.EqualTo((false, false, 7)), "applied");
            Assert.That(settings.Knobs.Knob(Knobs.Find("UploadCap")), Is.EqualTo(_defaults[Knobs.Find("UploadCap")]));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap"));
            Assert.That(file["NearFirst"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(file["PoolFragments"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That((int?)file["EntityTessBudget"], Is.EqualTo(7));
            Assert.That(file["CullGrid"], Is.Null, "gone with its knob");
        });
    }

    // Json.NET reads the first two as BigInteger; they fall back like any value out of range instead of throwing out of Load
    [Test]
    public void AnIntegerBeyondLongIsOutOfRange()
    {
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "UploadCap": 100000000000000000000, "NearFirst": -100000000000000000000, "EntityTessBudget": 4294967296 }""")
            !;
        var (logger, fresh) = (new CapturingLogger(), new HudSettings());
        int[] knobs = [Knobs.Find("UploadCap"), Knobs.Find("TessSchedule"), Knobs.Find("EntityTessBudget")];
        settings.Knobs.Apply(logger, HudSettings.FileName);
        Assert.Multiple(() =>
        {
            Assert.That(knobs.Select(settings.Knobs.Knob), Is.EqualTo(knobs.Select(fresh.Knobs.Knob)), "the defaults apply");
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap").And.Some
                .Contains("NearFirst")
                .And.Some.Contains("EntityTessBudget"));
        });
    }
}
