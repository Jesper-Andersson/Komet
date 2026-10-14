using System.Globalization;
using HarmonyLib;
using Komet.Api;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Options.KometPages;

namespace Komet.Options;

// Each control mirrors its GuiCompositeSettings handler (1.22.7); ClientSettings watchers do the rest and it saves
// itself.
internal static partial class EngineOptions
{
    private const string Custom = "preset-custom";
    private const int MaxLanguages = 64;

    [Flags]
    private enum Effect
    {
        None = 0,
        Reload = 1, // shaders
        Rebuild = 2, // framebuffers
        Custom = 4 // the preset no longer describes the settings
    }

    private static Effect? _batched; // while Batch runs: the reloads and rebuilds asked for so far, done once at its end
    private static ICoreClientAPI? _capi;

    // Without tab the buttons to the game's screens are left out
    public static OptionPage[] Pages(ICoreClientAPI capi, Action<string>? tab = null)
    {
        if (!NotNull(capi)) return [];
        _capi = capi;
        var section = T("section-game");
        List<OptionPage> pages = [General(capi, section), Quality(capi, section), Performance(section), Mouse(section)];
        if (Controls(section, tab) is { } controls) pages.Add(controls);
        pages.AddRange([Accessibility(capi, section), Sound(section), Interface(capi, section, tab)]);
        if (Developer(section) is { } developer) pages.Add(developer);
        return [.. pages];
    }

    private static OptionPage General(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-general", T("page-general"), section);
        var presets = GraphicsPreset.Presets;
        if (!NotNull(capi) || !NotNull(presets) || !Assert(presets.Count > 0)) return page;
        _ = Presets(capi, page)
            .Slider(Name("viewdist"), 32, 1536, 32, () => ClientSettings.ViewDistance,
                v => Apply(() => ClientSettings.ViewDistance = (int)v, Effect.Custom), " " + T("unit-blocks"),
                Hover(capi.IsSinglePlayer ? "viewdist-singleplayer" : "viewdist"))
            .Setting("fov", 20, 150, 1, () => ClientSettings.FieldOfView, v => ClientSettings.FieldOfView = (int)v, "°")
            .Choice(Name("windowmode"), Names("windowmode-", "normal", "fullscreen", "maxborderless", "fullscreen-ontop"),
                () => ClientSettings.GameWindowMode, mode =>
                {
                    if (Index(mode, 4) && NotNull(SetWindowMode)) SetWindowMode(mode);
                })
            .Choice(Name("windowborder"), Names("windowborder-", "resizable", "fixed", "hidden"),
                () => ClientSettings.WindowBorder, Border)
            .Choice(Name("vsync"), [Lang.Get("Off"), Lang.Get("On"), Lang.Get("On + Sleep")],
                () => ClientSettings.VsyncMode, i => ClientSettings.VsyncMode = i, Hover("vsync"))
            .Setting("maxfps", 15, 241, 1, () => Math.Clamp(ClientSettings.MaxFPS, 15, 241),
                v => ClientSettings.MaxFPS = (int)v)
            .Format(v => v >= 241 ? Lang.Get("unlimited") : ((int)v).ToString(CultureInfo.InvariantCulture));
        return Appearance(capi, page);
    }

    // "Custom" is no step: shown once a setting departs from the preset.
    private static OptionPage Presets(ICoreClientAPI capi, OptionPage page)
    {
        GraphicsPreset[] steps = [.. GraphicsPreset.Presets.Where(p => p.Langcode != Custom).Take(OptionPage.MaxOptions)];
        if (!NotNull(capi) || !NotNull(page) || !Assert(steps.Length > 1)) return page;
        var custom = Lang.Get(Custom);
        return page.Slider(Name("preset"), 0, steps.Length - 1, 1,
                () => Array.FindIndex(steps, p => p.PresetId == ClientSettings.GraphicsPresetId),
                v => Preset(capi, steps[Math.Clamp((int)Math.Round(v), 0, steps.Length - 1)].PresetId), "", T("preset-hint"))
            // -1 (no preset matches) is normal, not a failed assertion
            .Format(v => (int)Math.Round(v) is var i && (uint)i < (uint)steps.Length ? Lang.Get(steps[i].Langcode) : custom);
    }

    private static OptionPage Appearance(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page) || !Assert(page.Count > 0)) return page;
        return page.Group(Lang.Get("setting-column-appear"))
            .Setting("gamma", 30, 300, 5, () => Math.Round(ClientSettings.GammaLevel * 100),
                v => ClientSettings.GammaLevel = (float)(v / 100))
            .Setting("dynamiccolorgrading", () => ClientSettings.DynamicColorGrading, on => Grading(capi, on))
            .Setting("contrast", 100, 200, 10, () => Math.Round(ClientSettings.ExtraContrastLevel * 100) + 100,
                v => Apply(() => ClientSettings.ExtraContrastLevel = (float)((v - 100) / 100), Effect.Custom), "%")
            .EnabledWhen(() => !ClientSettings.DynamicColorGrading)
            .Setting("sepia", 0, 100, 5, () => Math.Round(ClientSettings.SepiaLevel * 100),
                v => ClientSettings.SepiaLevel = (float)(v / 100))
            .EnabledWhen(() => !ClientSettings.DynamicColorGrading);
    }

    private static OptionPage Quality(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-quality", T("page-quality"), section);
        string[] ssao = [Lang.Get("Off"), Lang.Get("Medium quality"), Lang.Get("High quality")];
        string[] shadows = [Lang.Get("Off"), Lang.Get("Low quality"), Lang.Get("Medium quality"),
            Lang.Get("High quality"), Lang.Get("Very high quality")];
        if (!NotNull(capi) || !Assert(shadows.Length == 5 && ssao.Length == 3)) return page;
        _ = page.Group(Lang.Get("setting-column-graphics"))
            .Slider(Name("shadows"), 0, 4, 1, () => ClientSettings.ShadowMapQuality,
                v => Apply(() => ClientSettings.ShadowMapQuality = (int)v, Effect.Rebuild | Effect.Reload | Effect.Custom),
                "", Hover("dynashade")).Format(v => Named(shadows, v))
            .Setting("smoothshadows", () => ClientSettings.SmoothShadows,
                on => Apply(() => ClientSettings.SmoothShadows = on, Effect.Custom))
            .Setting("ssao", 0, 2, 1, () => ClientSettings.SSAOQuality, // in game the engine's watcher reloads and rebuilds
                v => Apply(() => ClientSettings.SSAOQuality = (int)v, Effect.Custom))
            .Format(v => Named(ssao, v))
            .Setting("bloom", () => ClientSettings.Bloom,
                on => Apply(() => ClientSettings.Bloom = on, Effect.Reload | Effect.Custom))
            .Setting("abloom", 0, 100, 10, () => ClientSettings.AmbientBloomLevel,
                v => Apply(() => ClientSettings.AmbientBloomLevel = (float)v, Effect.Reload | Effect.Custom), "%")
            .Setting("godrays", () => ClientSettings.GodRayQuality > 0,
                on => Apply(() => ClientSettings.GodRayQuality = on ? 1 : 0, Effect.Reload | Effect.Custom))
            .Setting("fxaa", () => ClientSettings.FXAA,
                on => Apply(() => ClientSettings.FXAA = on, Effect.Reload | Effect.Custom));
        return Effects(capi, page);
    }

    private static OptionPage Effects(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page) || !Assert(page.Count > 0)) return page;
        return page.Choice(Name("clouds"), Names("settings-clouds-", "off", "volumetric", "classic"),
                () => ClientSettings.CloudRenderMode, i => ClientSettings.CloudRenderMode = i,
                Lang.Get("settings-hover-" + "clouds"))
            .Setting("grasswaves", () => ClientSettings.WavingFoliage,
                on => Apply(() => ClientSettings.WavingFoliage = on, Effect.Reload | Effect.Custom))
            .Setting("foamandshinyeffect", () => ClientSettings.LiquidFoamAndShinyEffect,
                on => Apply(() => ClientSettings.LiquidFoamAndShinyEffect = on, Effect.Reload | Effect.Custom))
            .Setting("particles", 0, 100, 2, () => ClientSettings.ParticleLevel,
                v => Apply(() => ClientSettings.ParticleLevel = (int)v, Effect.Custom), "%")
            .Setting("dynalight", 0, 100, 1, () => ClientSettings.MaxDynamicLights,
                v => Apply(() => ClientSettings.MaxDynamicLights = (int)v, Effect.Reload | Effect.Custom))
            .Format(v => v <= 0 ? Lang.Get("disabled") : ((int)v).ToString(CultureInfo.InvariantCulture))
            .Setting("resolution", 25, 100, 5, () => Math.Round(ClientSettings.SSAA * 100),
                v => Apply(() => ClientSettings.SSAA = (float)(v / 100), Effect.Rebuild | Effect.Custom), "%");
    }

    private static OptionPage Performance(string section)
    {
        var page = new OptionPage("vs-performance", T("page-performance"), section);
        if (!Assert(section.Length > 0)) return page;
        return page.Choice(Name("optimizeram"), [Lang.Get("Optimize somewhat"), Lang.Get("Aggressively optimize ram")],
                () => Math.Clamp(ClientSettings.OptimizeRamMode - 1, 0, 1), i => ClientSettings.OptimizeRamMode = i + 1,
                Hover("optimizeram"))
            .Setting("occlusionculling", () => ClientSettings.Occlusionculling, on => ClientSettings.Occlusionculling = on)
            .Setting("lodbiasfar", 35, 100, 1, () => Math.Round(ClientSettings.LodBiasFar * 100),
                v => Apply(() => ClientSettings.LodBiasFar = (float)(v / 100), Effect.Custom), "%");
    }

    private static void Apply(Action set, Effect effect)
    {
        if (!NotNull(set) || !Assert((effect & ~(Effect.Reload | Effect.Rebuild | Effect.Custom)) == 0)) return;
        set();
        if ((effect & Effect.Custom) != 0 && CustomPreset() is { } custom) ClientSettings.GraphicsPresetId = custom;
        Run(_capi, effect);
    }

    // Several at once (Apply): one rebuild and one shader reload
    internal static void Batch(Action apply)
    {
        if (!NotNull(apply) || !Assert(_batched is null)) return;
        _batched = Effect.None;
        try
        {
            apply();
        }
        finally
        {
            var effect = _batched ?? Effect.None;
            _batched = null;
            Run(_capi, effect);
        }
    }

    internal static void ReloadShaders(ICoreClientAPI capi) => Run(capi, Effect.Reload);

    private static void Run(ICoreClientAPI? capi, Effect effect)
    {
        if (_batched is { } batched)
        {
            _batched = batched | (effect & (Effect.Rebuild | Effect.Reload));
            return;
        }

        if ((effect & Effect.Rebuild) != 0 && NotNull(ScreenManager.Platform)) ScreenManager.Platform.RebuildFrameBuffers();
        if ((effect & Effect.Reload) != 0 && NotNull(capi)) _ = capi.Shader.ReloadShaders();
    }

    internal static void Preset(ICoreClientAPI capi, int index)
    {
        var presets = GraphicsPreset.Presets;
        if (!NotNull(capi) || !Index(index, presets.Count)) return;
        var p = presets[index];
        if (p.Langcode == Custom) return;
        ShaderRegistry.SupressShaderAndBufferReloads = true;
        try
        {
            (ClientSettings.GraphicsPresetId, ClientSettings.ViewDistance, ClientSettings.SmoothShadows) =
                (p.PresetId, p.ViewDistance, p.SmoothLight);
            (ClientSettings.FXAA, ClientSettings.SSAOQuality, ClientSettings.WavingFoliage) = (p.FXAA, p.SSAO, p.WavingFoliage);
            (ClientSettings.LiquidFoamAndShinyEffect, ClientSettings.Bloom, ClientSettings.GodRayQuality) =
                (p.LiquidFoamEffect, p.Bloom, p.GodRays ? 1 : 0);
            (ClientSettings.ShadowMapQuality, ClientSettings.ParticleLevel, ClientSettings.MaxDynamicLights) =
                (p.ShadowMapQuality, p.ParticleLevel, p.DynamicLights);
            (ClientSettings.SSAA, ClientSettings.LodBiasFar) = (p.Resolution, p.LodBiasFar);
        }
        finally
        {
            ShaderRegistry.SupressShaderAndBufferReloads = false;
        }

        Run(capi, Effect.Rebuild | Effect.Reload);
    }

    private static int? CustomPreset()
    {
        if (!NotNull(GraphicsPreset.Presets)) return null;
        var custom = GraphicsPreset.Presets.Find(p => p.Langcode == Custom);
        return custom is not null && Assert(custom.PresetId >= 0) ? custom.PresetId : null;
    }

    private static void Border(int border)
    {
        if (!Index(border, 3)) return;
        ClientSettings.WindowBorder = border;
        if (ClientSettings.GameWindowMode == 2 && border != 2) ClientSettings.GameWindowMode = 0;
        _ = Assert(ClientSettings.GameWindowMode != 2 || border == 2);
    }

    private static void Grading(ICoreClientAPI capi, bool on)
    {
        if (!NotNull(capi)) return;
        ClientSettings.DynamicColorGrading = on;
        if (on || !NotNull(capi.Render.ShaderUniforms)) return;
        capi.Render.ShaderUniforms.SepiaLevel = ClientSettings.SepiaLevel;
        capi.Render.ShaderUniforms.ExtraContrastLevel = ClientSettings.ExtraContrastLevel;
    }

    private static readonly Action<int>? SetWindowMode =
        AccessTools.Method(typeof(GuiCompositeSettings), "SetWindowMode", [typeof(int)]) is { } method
            ? AccessTools.MethodDelegate<Action<int>>(method)
            : null;

    private static OptionPage Setting(this OptionPage page, string id, Func<bool> get, Action<bool> set) =>
        Assert(id.Length > 0) && NotNull(get) ? page.Switch(Name(id), get, set, Hover(id)) : page;

    private static OptionPage Setting(this OptionPage page, string id, double min, double max, double step, Func<double> get,
        Action<double> set, string unit = "") =>
        Assert(id.Length > 0) && NotNull(get) ? page.Slider(Name(id), min, max, step, get, set, unit, Hover(id)) : page;

    private static string Name(string id) =>
        NotNull(id) && Assert(id.Length > 0) ? Lang.Get("setting-name-" + id) : "";

    private static string Hover(string id) =>
        NotNull(id) && Assert(id.Length > 0) ? Lang.Get("setting-hover-" + id) : "";

    private static string Named(string[] names, double value)
    {
        if (!NotNull(names) || !Finite(value)) return "";
        var i = (int)Math.Round(value);
        return Index(i, names.Length) ? names[i] : "";
    }

    private static string[] Names(string prefix, params string[] ids) =>
        NotNull(prefix) && Assert(ids.Length is > 0 and <= OptionPage.MaxOptions)
            ? Array.ConvertAll(ids, id => Lang.Get(prefix + id))
            : [];
}
