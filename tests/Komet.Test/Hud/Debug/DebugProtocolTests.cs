using System.Text.Json;
using Komet.Api;
using Komet.Diagnostics;
using Komet.Hud;

namespace Komet.Test.Hud;

// The debug protocol is read by people and by whoever they send it to: every section is there, in the language asked for whatever
// the game's is, no key is left untranslated, no number prints as NaN, and no path names the account.
[NonParallelizable]
public sealed class DebugProtocolTests
{
    private static readonly string[] RuntimeKeys =
    [
        "hud-dbg-level-high", "hud-dbg-level-medium", "hud-dbg-level-low", "hud-dbg-level-info", "hud-dbg-risk-high",
        "hud-dbg-risk-medium", "hud-dbg-risk-low", "hud-dbg-mark-gc", "hud-dbg-mark-jit", "hud-dbg-mark-runqueue",
        "hud-dbg-mark-outside", "hud-dbg-mark-unprofiled", "hud-dbg-mark-other"
    ];

    private string _assets = "";

    // Komet's German alone. PreLoad reads only the game domain's file, where a key spelled with its domain keeps it.
    [SetUp]
    public void LoadGerman()
    {
        GameLang.EnsureLoaded();
        _assets = Directory.CreateTempSubdirectory("komet-lang-").FullName;
        var german = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.KometDir, "assets", "komet", "lang", "de.json")))
            .RootElement.EnumerateObject().ToDictionary(p => "komet:" + p.Name, p => p.Value.GetString());
        _ = Directory.CreateDirectory(Path.Combine(_assets, "game", "lang"));
        File.WriteAllText(Path.Combine(_assets, "game", "lang", "de.json"), JsonSerializer.Serialize(german));
        var service = new TranslationService("de", new QuietLogger());
        service.PreLoad(_assets);
        Lang.AvailableLanguages["de"] = service;
    }

    [TearDown]
    public void DropGerman()
    {
        _ = Lang.AvailableLanguages.Remove("de");
        Directory.Delete(_assets, true);
    }

    [Test]
    public void TheProtocolHasEverySectionInTheLanguageAskedFor()
    {
        var input = Input("de");
        var text = DebugProtocol.Build(input, Extra(), DebugFrames.Summarize(input.Frames), ["komet-debug.txt"]);
        Assert.Multiple(() =>
        {
            for (var i = 0; i < DebugProtocol.Sections.Length; i++) Assert.That(text, Does.Contain($"[{i + 1}] "));
            Assert.That(text, Does.Contain("KOMET DEBUG-PROTOKOLL"));
            Assert.That(text, Does.Contain("[1] BEFUNDE"));
            Assert.That(text, Does.Not.Contain("hud-dbg-"), "an untranslated key");
            Assert.That(text, Does.Not.Contain("NaN").And.Not.Contain("Infinity"));
            Assert.That(text, Does.Contain("[18] TEST (TESTMOD)"), "a section another mod added comes last");
            Assert.That(text, Does.Contain("HOCH"), "the 120 ms frame is a finding");
        });
    }

    [Test]
    public void TheSummaryReadsPercentilesLowsAndTheSplit()
    {
        var summary = DebugFrames.Summarize(Input("en").Frames);
        Assert.Multiple(() =>
        {
            Assert.That(summary.Frames, Is.EqualTo(1000));
            Assert.That(summary.Steady, Is.EqualTo(1000));
            Assert.That(summary.MedianMs, Is.EqualTo(10).Within(1e-3));
            Assert.That(summary.WorstMs, Is.EqualTo(120).Within(1e-3));
            Assert.That(summary.Over50, Is.EqualTo(10));
            Assert.That(summary.Low1Fps, Is.EqualTo(1000 / 120.0).Within(1e-3), "the worst 10 frames");
            Assert.That(summary.GcMs, Is.EqualTo(10 * 30).Within(1e-3));
            Assert.That(summary.Gen0, Is.EqualTo(10));
        });
    }

    [Test]
    public void KeysBuiltAtRuntimeExistInEveryLanguage()
    {
        foreach (var lang in (string[])["en", "de"])
        {
            var keys = JsonDocument.Parse(File.ReadAllText(Path.Combine(Paths.KometDir, "assets", "komet", "lang", lang + ".json")))
                .RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            var numbers = DebugProtocol.Numbers(default).Select(n => "hud-dbg-n-" + n.Key);
            Assert.That(RuntimeKeys.Concat(numbers).Concat(DebugProtocol.Sections).Where(k => !keys.Contains(k)), Is.Empty, lang);
        }
    }

    [Test]
    public void APrefixThatSkipsAnotherOwnersPatchIsHighRisk()
    {
        static PatchLink Link(string owner, PatchKind kind, bool skips = false) => new(owner, "", kind, 400, "M", skips);
        Assert.Multiple(() =>
        {
            Assert.That(HarmonyAudit.Rate([Link("a", PatchKind.Prefix, true), Link("b", PatchKind.Postfix)], 2),
                Is.EqualTo(PatchRisk.High));
            Assert.That(HarmonyAudit.Rate([Link("a", PatchKind.Transpiler), Link("b", PatchKind.Transpiler)], 2),
                Is.EqualTo(PatchRisk.High));
            Assert.That(HarmonyAudit.Rate([Link("a", PatchKind.Transpiler), Link("b", PatchKind.Postfix)], 2),
                Is.EqualTo(PatchRisk.Medium));
            Assert.That(HarmonyAudit.Rate([Link("a", PatchKind.Prefix), Link("b", PatchKind.Postfix)], 2), Is.EqualTo(PatchRisk.Low));
            Assert.That(HarmonyAudit.Rate([Link("a", PatchKind.Prefix, true)], 1), Is.EqualTo(PatchRisk.Low));
        });
    }

    [Test]
    public void ThePathsDoNotNameTheAccount()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.That(DebugProtocol.Anonymize($"log at {home}/.config/VintagestoryData/Logs"),
            Is.EqualTo("log at ~/.config/VintagestoryData/Logs"));
    }

    // 990 frames of 10 ms and ten of 120 ms with a 30 ms gen0 collection each
    private static DebugInput Input(string code)
    {
        var frames = Enumerable.Range(0, 1000).Select(i => i % 100 == 99
            ? new DebugFrame(i / 100f, 120, 30, 0, 0.1f, 0.2f, 4096, 1024, 1, true)
            : new DebugFrame(i / 100f, 10, 0, 0, 0.1f, 0.2f, 64, 16, 0, true)).ToArray();
        return new DebugInput
        {
            Code = code, Mode = "record", Started = DateTimeOffset.Now, Komet = "2.0.0", Frames = frames,
            Spikes = [(new DebugSpike(99, frames[99]), [("~gc", 30f), ("entities", 12f), (null, 0f)])],
            Causes = [("~gc", 10, 120, 118)], ModTimes = [("testmod", 0.4, [("Renderer (60/s)", 0.3)])],
            Scopes = [("testmod:path", 12.5, 300)], Extra = [("Test (testmod)", "cached: 4")], HudMeans = "FPS: 60",
            System = [("hud-dbg-os", "Linux")], Runtime = [("hud-dbg-locks", "3")], World = [("hud-dbg-chunks", "100 loaded")],
            Render = [("hud-dbg-draw-calls", "900")], Client = [("viewDistance", "256")], Knobs = [("FrustumSweep", 1, 0)],
            Features = [new FeatureInfo("ChunkBudget", "Chunk budget", "komet", FeatureState.StoodDown, true)],
            Mods = [("testmod", "Test", "1.0.0", "Code", "testmod.zip")], Entities = [("game:wolf", 3)], EntityTotal = 3
        };
    }

    private static DebugExtra Extra() =>
        new(new HarmonyAudit(), ["1.1.2026 12:00:00 [Error] boom"], 1, 0, new Dictionary<string, double> { ["fps"] = 50 }, "20260101-120000");
}
