using Komet.Api;
using Komet.Core;
using Komet.Hud;
using Komet.Options;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Options;

public sealed class OptionsTests
{
    private static readonly string[] GameTabs =
        ["vs-general", "vs-quality", "vs-performance", "vs-mouse", "vs-accessibility", "vs-sound", "vs-interface"];

    [OneTimeSetUp]
    public void LoadTheGamesLanguage()
    {
        GameLang.LoadEnglish();
    }

    [TearDown]
    public void Forget()
    {
        KometOptions.Clear();
    }

    // The names' window opens with the chosen name in the middle (the scroll NaN: just opened) and stays within the names; no
    // assertion trips on the NaN
    [Test]
    public void ThePickerOpensOnTheChosenNameAndScrollsWithinTheNames()
    {
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        try
        {
            var names = (Cell: 36.0, Step: 39.0, View: 200.0, Total: 39.0 * 40 - 3);
            var end = names.Total - names.View;
            Assert.Multiple(() =>
            {
                Assert.That(OptionsScreen.Scrolled(double.NaN, 20, names), Is.EqualTo(20 * 39 - (200 - 36) / 2.0));
                Assert.That(OptionsScreen.Scrolled(double.NaN, 0, names), Is.Zero, "the first name: at the top");
                Assert.That(OptionsScreen.Scrolled(double.NaN, 39, names), Is.EqualTo(end), "the last: at the bottom");
                Assert.That(OptionsScreen.Scrolled(5000, 3, names), Is.EqualTo(end), "scrolled past the end");
                Assert.That(OptionsScreen.Scrolled(120, 3, names), Is.EqualTo(120), "scrolled: kept");
                Assert.That(logger.Lines, Is.Empty, "no assertion failed");
            });
        }
        finally
        {
            Contracts.Attach(null);
        }
    }

    [Test]
    public void EveryGameOptionShowsWhatTheGameTranslates()
    {
        var lang = JObject.Parse(File.ReadAllText(Path.Combine(GameInstall.Assets, "game", "lang", "en.json")));
        var komet = JObject.Parse(File.ReadAllText(Path.Combine(Paths.KometDir, "assets", "komet", "lang", "en.json")));
        var known = lang.Properties().Concat(komet.Properties()).SelectMany(p => new[] { p.Name, p.Value.ToString() }).ToHashSet();
        var pages = EngineOptions.Pages(Answers.NullClient, _ => { });
        var missing = new List<string>();
        foreach (var page in pages)
            for (var i = 0; i < page.Count; i++)
            {
                var row = page[i];
                missing.AddRange(new[] { row.Label, row.Hint ?? row.Label }.Concat(row.Names).Where(t => !known.Contains(t)));
            }

        Assert.Multiple(() =>
        {
            Assert.That(pages.Select(p => p.Id), Is.SupersetOf(GameTabs));
            Assert.That(pages.Sum(p => p.Count), Is.GreaterThanOrEqualTo(28 + 6 + 6 + 9 + 12), "every control of the game's tabs");
            Assert.That(missing, Is.Empty);
        });
    }

    [Test]
    public void EveryHotkeyHasItsRowOnTheControlsPage()
    {
        var before = ScreenManager.hotkeyManager;
        var saved = ClientSettings.KeyMapping.GetValueOrDefault("walkforward");
        try
        {
            ScreenManager.hotkeyManager = new HotkeyManager();
            ScreenManager.hotkeyManager.RegisterDefaultHotKeys();
            var keys = ScreenManager.hotkeyManager.HotKeys;
            var page = EngineOptions.Pages(Answers.NullClient).Single(p => p.Id == "vs-controls");
            var rows = Enumerable.Range(0, page.Count).Select(i => page[i]).Where(r => r.Kind == OptionKind.Key).ToArray();
            EngineOptions.Bind("walkforward", keys["jump"].CurrentMapping.Clone()); // the same way the screen binds
            var forward = rows.Single(r => r.Code == "walkforward");
            Assert.Multiple(() =>
            {
                Assert.That(rows.Select(r => r.Code), Is.EquivalentTo(keys.Keys));
                Assert.That(forward.Warn!(), Is.True);
                Assert.That(EngineOptions.Clash("jump"), Is.EqualTo(keys["walkforward"].Name));
                Assert.That(rows.Single(r => r.Code == "sprint").Warn!(), Is.False);
            });
        }
        finally
        {
            ScreenManager.hotkeyManager = before;
            if (saved is null) _ = ClientSettings.KeyMapping.Remove("walkforward");
            else ClientSettings.KeyMapping["walkforward"] = saved;
        }
    }

    [TestCase("Deutsch / German (100% complete)", "Deutsch\tGerman")]
    [TestCase("English / English", "English")]
    [TestCase("\u0627\u0644\u0639\u0631\u0628\u064a\u0629 / Arabic (100% complete)", "Arabic")]
    [TestCase("Chinese Simplified / \u7b80\u4f53\u4e2d\u6587 (95% complete)", "Chinese Simplified")]
    [TestCase("Espa\u00f1ol, latinoamericano / Spanish, Latin American (80% complete)",
        "Espa\u00f1ol, latinoamericano\tSpanish, Latin American")]
    public void LanguagesAreNamedReadably(string name, string shown)
    {
        Assert.That(EngineOptions.Readable(name), Is.EqualTo(shown));
    }

    private static readonly string[] Areas = ["komet-render", "komet-chunks", "komet-misc", "komet-vulkan"];

    // The Overview first, the areas, then HUD and tools; every knob page is one of the areas; "advanced" adds rows, no pages
    [Test]
    public void KometHasItsHudPagesAndOnePagePerKnobPage()
    {
        var basic = new KometPages(new HudSettings(), () => { }, _ => { }, () => { }, () => { }, () => { }).Build();
        var pages = new KometPages(new HudSettings { ShowAdvanced = true }, () => { }, _ => { }, () => { }, () => { }, () => { })
            .Build();
        var knobPages = Knobs.BuiltIn.ToArray().Select(k => k.Page).OfType<string>().Distinct().Select(p => "komet-" + p);
        var rows = pages.SelectMany(p => Enumerable.Range(0, p.Count).Select(i => p[i].Label)).ToArray();
        string[] ids = [KometPages.Overview, .. Areas, KometPages.Hud, KometPages.Tools];
        Assert.Multiple(() =>
        {
            Assert.That(basic.Select(p => p.Id), Is.EqualTo(ids));
            Assert.That(pages.Select(p => p.Id), Is.EqualTo(ids));
            Assert.That(knobPages, Is.SubsetOf(Areas));
            Assert.That(pages, Has.All.Matches<OptionPage>(p => p.Count is > 0 and <= OptionPage.MaxRows));
            Assert.That(pages.Sum(p => p.Count), Is.GreaterThan(basic.Sum(p => p.Count) + 60), "advanced lists every exact switch");
            Assert.That(rows, Does.Not.Contain(""), "every row has a label");
        });
    }

    // Every built-in knob with a page is a row somewhere once "advanced" is on: nothing is settable only in komet.json
    [Test]
    public void AdvancedShowsEveryKnob()
    {
        GameLang.EnsureLoaded();
        var pages = new KometPages(new HudSettings { ShowAdvanced = true }, () => { }, _ => { }, () => { }, () => { }, () => { })
            .Build();
        var labels = pages.SelectMany(p => Enumerable.Range(0, p.Count).Select(i => p[i].Label)).ToHashSet();
        var missing = Knobs.BuiltIn.ToArray().Where(k => k.Page is not null && !labels.Contains(KometPages.T(k.Label)))
            .Select(k => k.Key).ToArray();
        Assert.That(missing, Is.Empty);
    }

    // What a preset or this computer sets is a trade-off with a row of its own, never one of the Overview's preferences; a preset
    // value lies in the knob's range
    [Test]
    public void ThePresetsAndTheComputerSetOnlyWeighedKnobs()
    {
        var knobs = Knobs.BuiltIn.ToArray();
        var set = knobs.Where(k => k.Presets is not null || k.Suggest is not null).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(set, Is.Not.Empty);
            Assert.That(set.Where(k => !k.Weighed || k.Page is null).Select(k => k.Key), Is.Empty);
            Assert.That(knobs.Where(k => k.General && k.Weighed).Select(k => k.Key), Is.Empty);
            Assert.That(knobs.Where(k => k.Presets is { } p && !new[] { p.Quality, p.Balanced, p.Speed }.All(v => v >= k.Min && v <= k.Max))
                .Select(k => k.Key), Is.Empty);
        });
    }

    [Test]
    public void APageRegisteredAgainStartsOver()
    {
        _ = KometOptions.Page("mod", "Mod").Switch("a", () => true, _ => { }).Switch("b", () => true, _ => { });
        var again = KometOptions.Page("mod", "Mod").Switch("c", () => true, _ => { });
        _ = KometOptions.Page("two", "Two", "Mods");
        Assert.Multiple(() =>
        {
            Assert.That(KometOptions.Count, Is.EqualTo(2));
            Assert.That(KometOptions.At(0), Is.SameAs(again));
            Assert.That(again.Count, Is.EqualTo(1));
            Assert.That(again.Section, Is.EqualTo("Mod"), "the title stands in for a missing section");
            KometOptions.Clear();
            Assert.That(KometOptions.Count, Is.Zero);
        });
    }

    [Test]
    public void RowsCarryTheirValuesAsNumbers()
    {
        var (on, level, pick) = (false, 0.0, 0);
        var page = KometOptions.Page("mod", "Mod").Group("Group")
            .Switch("switch", () => on, v => on = v)
            .Slider("slider", 0, 10, 2.5, () => level, v => level = v, " u")
            .Choice("choice", ["a", "b", "c"], () => pick, i => pick = i);
        page[1].Set(1);
        page[2].Set(page[2].At(0.49));
        page[3].Set(2);
        Assert.Multiple(() =>
        {
            Assert.That(page[0].Kind, Is.EqualTo(OptionKind.Group));
            Assert.That((on, page[1].Get()), Is.EqualTo((true, 1.0)));
            Assert.That(level, Is.EqualTo(5), "a slider snaps to its step");
            Assert.That(page[2].ValueText(level), Is.EqualTo("5 u"));
            Assert.That((pick, page[3].Max), Is.EqualTo((2, 2.0)));
        });
    }

    [Test]
    public void FormatAndEnabledApplyToTheRowAddedLast()
    {
        var enabled = false;
        var page = KometOptions.Page("mod", "Mod")
            .Slider("first", 0, 1, 1, () => 0, _ => { })
            .Slider("second", 0, 1, 1, () => 0, _ => { })
            .Format(v => v > 0 ? "all" : "none").EnabledWhen(() => enabled);
        Assert.Multiple(() =>
        {
            Assert.That(page[0].ValueText(1), Is.EqualTo("1"));
            Assert.That(page[1].ValueText(1), Is.EqualTo("all"));
            Assert.That((page[0].IsEnabled, page[1].IsEnabled), Is.EqualTo((true, false)));
        });
    }

    [Test]
    public void ARowPastTheBoundIsDropped()
    {
        var page = KometOptions.Page("mod", "Mod");
        for (var i = 0; i < OptionPage.MaxOptions + 5; i++) _ = page.Switch("s" + i, () => false, _ => { });
        Assert.That(page.Count, Is.EqualTo(OptionPage.MaxOptions));
    }

    // Two choices, OpenGL and Vulkan: the mode is VulkanOnly alone, every other knob is left as it is
    [TestCase(0)]
    [TestCase(1)]
    public void TheVulkanModeIsTheVulkanOnlySwitch(int mode)
    {
        var values = Enumerable.Repeat(7, Knobs.Count).ToArray();
        VulkanMode.Set(mode, (knob, value) => values[knob] = value);
        var only = Knobs.Find("VulkanOnly");
        Assert.Multiple(() =>
        {
            Assert.That(VulkanMode.Of(knob => values[knob]), Is.EqualTo(mode));
            Assert.That(values[only], Is.EqualTo(mode));
            Assert.That(values.Where((_, i) => i != only), Is.All.EqualTo(7), "no other knob written");
            Assert.That(string.Join(" ", VulkanMode.Choices.Select(c => c.Name)), Is.EqualTo("opengl vulkan"));
            Assert.That(VulkanMode.Choice(mode), Is.EqualTo(mode));
        });
    }
}
