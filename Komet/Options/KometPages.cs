using System.Globalization;
using Komet.Api;
using Komet.Core;
using Komet.Hud;
using Komet.Rendering;

namespace Komet.Options;

// Komet's pages, built per composition (a language change shows at once). Three tiers: the Overview's preset sets the
// trade-offs; each area's page lists those trade-offs singly (Weighed); with "advanced" the page adds every engine-exact
// switch of the area under a master switch, so nothing is reachable only in komet.json.
internal sealed class KometPages(HudSettings settings, Action dump, Action<float> bench, Action verify, Action window, Action debug)
{
    public const string Overview = "komet-overview", Hud = "komet-hud", Tools = "komet-tools";
    private const int MaxKnobPages = 8;

    private static readonly string[] Corners = ["topleft", "topright", "bottomleft", "bottomright"];

    // Knob pages in the sidebar's order (a page a knob names beyond these follows, in knob order)
    private static readonly string[] Areas = ["render", "chunks", "misc", "vulkan"];

    public bool Advanced => settings.ShowAdvanced;

    public OptionPage[] Build()
    {
        var section = T("section-komet");
        if (!NotNull(settings)) return [];
        List<OptionPage> pages = [OverviewPage(section)];
        foreach (var area in Areas.Bounded(MaxKnobPages)) pages.Add(AreaPage(area, section));
        var knobs = Knobs.BuiltIn;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page is { } page && pages.TrueForAll(p => p.Id != "komet-" + page) && Assert(pages.Count < MaxKnobPages + 3))
                pages.Add(AreaPage(page, section));
        pages.Add(HudPage(section));
        pages.Add(ToolsPage(section));
        _ = Assert(pages.Count > 2);
        return [.. pages];
    }

    // The preset with this computer's facts, the renderer, the preferences
    private OptionPage OverviewPage(string section)
    {
        var s = settings.Knobs;
        var page = new OptionPage(Overview, T("page-overview"), section);
        if (!NotNull(s)) return page;
        var hw = HardwareProfile.Found;
        var names = Array.ConvertAll(Presets.Names, name => T("preset-" + name));
        _ = page.Group(T("preset-group"))
            .Choice(T("preset"), [.. names, T("preset-adjusted")],
                () => s.Adjusted ? Presets.Names.Length : Array.IndexOf(Presets.Names, s.Preset),
                choice => { if (Index(choice, Presets.Names.Length)) s.ChoosePreset(Presets.Names[choice]); },
                T("preset-hint"))
            .Button(T("hardware", hw.Cores, Known(hw.RamGb), Known(hw.VramGb), Known(hw.RefreshHz)), T("do-detected"), s.UseDetected,
                T("detected-hint"));
        _ = ModeRow(page.Group(T("renderer-group", VulkanMode.Renderer())));
        _ = page.Group(T("general"));
        var knobs = Knobs.BuiltIn; // Knob.General: preferences, not quality against speed
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].General && knobs[i].Page is not null)
                _ = Row(page, knobs[i], i);
        return page;
    }

    // An area's trade-offs by group; with "advanced" its exact switches too, each one after the master
    private OptionPage AreaPage(string area, string section)
    {
        var page = new OptionPage("komet-" + area, T("page-" + area), section) { Capacity = OptionPage.MaxRows };
        var knobs = Knobs.BuiltIn;
        List<string> groups = [];
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page == area && Single(knobs[i]) && knobs[i].Group is { } group && !groups.Contains(group))
                groups.Add(group);
        foreach (var group in groups.Bounded(Knobs.MaxKnobs))
        {
            _ = page.Group(T(group));
            for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
                if (knobs[i].Page == area && knobs[i].Group == group && Single(knobs[i]))
                    _ = Row(page, knobs[i], i);
        }

        if (settings.ShowAdvanced) ExactRows(page, area);
        else if (page.Count == 0) _ = page.Group(T("page-" + area)); // nothing weighed here: the page stands for its exact switches
        return Features.AddRows(page, true);
    }

    // Knob.Weighed: a trade-off a player weighs, a row of its own
    private static bool Single(Knob knob) => NotNull(knob) && knob.Page is not null && knob.Weighed && !knob.General;

    // Every other switch only makes the engine's own result faster: under the area's master switch
    private static bool Exact(Knob knob) =>
        NotNull(knob) && knob.Page is not null && knob.IsSwitch && !knob.Weighed && !knob.General;

    // The area's exact switches: one master (on when all are, setting it sets all), then each one
    private void ExactRows(OptionPage page, string area)
    {
        var knobs = Knobs.BuiltIn;
        List<int> members = [];
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page == area && Exact(knobs[i]))
                members.Add(i);
        if (members.Count == 0) return;
        var s = settings.Knobs;
        _ = page.Group(T("exact"))
            .Switch(T("exact-all", members.Count), () => members.TrueForAll(i => s.Knob(i) != 0),
                on => members.ForEach(i => s.SetKnob(i, on ? 1 : 0)), T("exact-all-hint"));
        foreach (var i in members.Bounded(Knobs.MaxKnobs)) _ = Row(page, knobs[i], i);
    }

    private OptionPage HudPage(string section)
    {
        var s = settings;
        if (!NotNull(s) || !Assert(Corners.Length == (int)HudCorner.BottomRight + 1)) return new OptionPage(Hud, "HUD", section);
        return new OptionPage(Hud, T("page-hud"), section).Group(T("display"))
            .Switch(T("visible"), () => s.Visible, on => s.Visible = on)
            .Choice(T("corner"), Array.ConvertAll(Corners, c => T("corner-" + c)), () => (int)s.Corner, i => s.Corner = (HudCorner)i)
            .Slider(T("opacity"), HudSettings.OpacityRange, () => s.Opacity, v => s.Opacity = v)
            .Slider(T("scale"), HudSettings.ScaleRange, () => s.FontScale, v => s.FontScale = v)
            .Switch(T("overlaygraph"), () => s.ShowGraph, on => s.ShowGraph = on)
            .Switch(T("overlaymods"), () => s.ShowMods, on => s.ShowMods = on)
            .Slider(T("toast"), HudSettings.ToastRange, () => s.ToastMs, v => s.ToastMs = v)
            .Group(T("windows"))
            .Button(T("window"), T("do-open"), window)
            .Button(T("debugwindow"), T("do-open"), debug);
    }

    private OptionPage ToolsPage(string section)
    {
        var (s, page) = (settings, new OptionPage(Tools, T("page-tools"), section));
        if (!NotNull(s) || !NotNull(section)) return page;
        return page.Group(T("options"))
            .Switch(T("showadvanced"), () => s.ShowAdvanced, on => s.ShowAdvanced = on, T("showadvanced-hint"))
            .Group(T("version"))
            .Switch(T("updates"), () => s.UpdateCheck, on => (s.UpdateAsked, s.UpdateCheck) = (true, on))
            .Button(T("checksum"), T("do-verify"), verify)
            .Group(T("measure"))
            .Slider(T("interval"), HudSettings.IntervalRange, () => s.Interval, v => s.Interval = v)
            .Slider(T("bench"), HudSettings.BenchRange, () => s.BenchSeconds, v => s.BenchSeconds = v)
            .Button(T("benchmark"), T("do-bench", s.BenchSeconds), () => bench((float)s.BenchSeconds))
            .Button(T("values"), T("do-copy"), dump)
            .Group(T("reset"))
            .Button(T("rebuild"), T("do-rebuild"), TerrainRebuild.Request, T("rebuild-hint"))
            .Button(T("positions"), T("do-reset"), s.ResetPositions)
            .Button(T("defaults"), T("do-reset"), s.ResetDefaults);
    }

    private static string Known(int value) => value > 0 && Assert(value < 1_000_000) ? value.ToString(CultureInfo.InvariantCulture) : "?";

    private OptionPage Row(OptionPage page, Knob knob, int index)
    {
        if (!NotNull(knob) || !Index(index, Knobs.BuiltInCount)) return page;
        var (unit, hint, s) = (knob.Unit.Length > 0 ? " " + knob.Unit : "", T(knob.Label + "-hint"), settings.Knobs);
        return (knob.IsSwitch
            ? page.Switch(T(knob.Label), () => s.Knob(index) != 0, on => s.SetKnob(index, on ? 1 : 0), hint)
            : page.Slider(T(knob.Label), knob.Min, knob.Max, 1, () => s.Knob(index),
                v => s.SetKnob(index, (int)v), unit, hint).Standard(KometSettings.KnobDefault(index)))
            .LockedWhen(() => Features.LockText(index));
    }

    private OptionPage ModeRow(OptionPage page) =>
        page.Choice(T("vulkan-mode"), Array.ConvertAll(VulkanMode.Choices, choice => T("vulkan-mode-" + choice.Name)),
                () => VulkanMode.Choice(VulkanMode.Of(settings.Knobs.Knob)),
                choice => VulkanMode.Set(VulkanMode.Choices[choice].Mode, settings.Knobs.SetKnob), T("vulkan-mode-hint"))
            .LockedWhen(() => VulkanMode.CoreKnob >= 0 ? Features.LockText(VulkanMode.CoreKnob) : null);

    internal static string T(string key, params object[] args) =>
        NotNull(key) && Assert(key.Length > 0) ? HudText.Translate("settings-" + key, args) : "";
}
