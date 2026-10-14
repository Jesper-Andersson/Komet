using System.Globalization;
using Komet.Api;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Options.KometPages;

namespace Komet.Options;

// The game's other tabs (1.22.7) with each handler's conversions and side effects; ClientSettings watchers do the rest.
internal static partial class EngineOptions
{
    private static OptionPage Mouse(string section)
    {
        var page = new OptionPage("vs-mouse", Lang.Get("setting-mouse-header"), section);
        if (!NotNull(section) || !Assert(section.Length > 0)) return page;
        return page.Slider(Name("mousesensivity"), 1, 200, 5, () => ClientSettings.MouseSensivity,
                v => ClientSettings.MouseSensivity = (int)v)
            .Slider(Name("mousesmoothing"), 0, 95, 5, () => Math.Clamp(100 - ClientSettings.MouseSmoothing, 0, 95),
                v => ClientSettings.MouseSmoothing = 100 - (int)v)
            .Slider(Name("mousewheelsensivity"), 1, 100, 1, () => Math.Round(ClientSettings.MouseWheelSensivity * 10),
                v => ClientSettings.MouseWheelSensivity = (float)(v / 10))
            .Format(v => (v / 10).ToString("0.#", CultureInfo.InvariantCulture) + "x")
            .Setting("directmousemode", () => ClientSettings.DirectMouseMode, DirectMouse)
            .Switch(Name("invertyaxis"), () => ClientSettings.InvertMouseYAxis, on => ClientSettings.InvertMouseYAxis = on)
            .Choice(Name("itemCollectMode"), [Lang.Get("Always collect items"), Lang.Get("Only collect items when sneaking")],
                () => ClientSettings.ItemCollectMode, i => ClientSettings.ItemCollectMode = i);
    }

    private static OptionPage Accessibility(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-accessibility", Lang.Get("setting-accessibility-header"), section);
        if (!NotNull(capi) || !Assert(section.Length > 0)) return page;
        return page.Setting("togglesprint", () => ClientSettings.ToggleSprint, on => ClientSettings.ToggleSprint = on)
            .Setting("bobblehead", () => ClientSettings.ViewBobbing, on => ClientSettings.ViewBobbing = on)
            .Setting("camerashake", 0, 100, 1, () => Math.Round(ClientSettings.CameraShakeStrength * 100),
                v => ClientSettings.CameraShakeStrength = (float)(v / 100), " %")
            .Setting("wireframethickness", 1, 16, 1, () => Math.Round(ClientSettings.Wireframethickness * 2),
                v => ClientSettings.Wireframethickness = (float)(v / 2))
            .Format(v => (v / 2).ToString("0.#", CultureInfo.InvariantCulture) + "x")
            .Choice(Name("wireframecolors"), [Lang.Get("Preset 1"), Lang.Get("Preset 2"), Lang.Get("Preset 3")],
                () => Math.Clamp(ClientSettings.guiColorsPreset - 1, 0, 2), i =>
                {
                    ClientSettings.guiColorsPreset = i + 1;
                    capi.ColorPreset?.OnUpdateSetting();
                }, Hover("wireframecolors"))
            .Setting("instabilityWavingStrength", 0, 150, 1, () => Math.Round(ClientSettings.InstabilityWavingStrength * 100),
                v => ClientSettings.InstabilityWavingStrength = (float)(v / 100), " %");
    }

    private static OptionPage Sound(string section)
    {
        var page = new OptionPage("vs-sound", Lang.Get("setting-sound-header"), section);
        string[] frequencies = Names("setting-musicfrequency-", "low", "medium", "often", "veryoften");
        if (!NotNull(section) || !Assert(frequencies.Length == 4)) return page;
        _ = page.Slider(Name("mastersoundlevel"), 0, 100, 1, () => ClientSettings.MasterSoundLevel,
                v => ClientSettings.MasterSoundLevel = (int)v, "%")
            .Slider(Name("soundlevel"), 0, 100, 1, () => ClientSettings.SoundLevel, v => ClientSettings.SoundLevel = (int)v, "%")
            .Slider(Name("entitysoundlevel"), 0, 100, 1, () => ClientSettings.EntitySoundLevel,
                v => ClientSettings.EntitySoundLevel = (int)v, "%")
            .Slider(Name("ambientsoundlevel"), 0, 100, 1, () => ClientSettings.AmbientSoundLevel,
                v => ClientSettings.AmbientSoundLevel = (int)v, "%")
            .Slider(Name("weathersoundlevel"), 0, 100, 1, () => ClientSettings.WeatherSoundLevel,
                v => ClientSettings.WeatherSoundLevel = (int)v, "%")
            .Slider(Name("musiclevel"), 0, 100, 1, () => ClientSettings.MusicLevel, v => ClientSettings.MusicLevel = (int)v, "%")
            .Slider(Name("musicfrequency"), 0, 3, 1, () => ClientSettings.MusicFrequency, v => ClientSettings.MusicFrequency = (int)v)
            .Format(v => Named(frequencies, v))
            .Setting("hrtfmode", () => ClientSettings.UseHRTFAudio, on => ClientSettings.UseHRTFAudio = on);
        return Devices(page);
    }

    private static OptionPage Devices(OptionPage page)
    {
        var listed = ScreenManager.Platform?.AvailableAudioDevices?.Take(OptionPage.MaxOptions - 1).ToArray() ?? [];
        string?[] devices = [null, .. listed];
        string[] names = [T("default-device"), .. listed];
        if (!NotNull(page) || !Assert(names.Length == devices.Length)) return page;
        return page.Choice(Name("audiooutputdevice"), names, () => Math.Max(0, Array.IndexOf(devices, ClientSettings.AudioDevice)),
            i => ClientSettings.AudioDevice = Index(i, devices.Length) ? devices[i] : null);
    }

    private static OptionPage Interface(ICoreClientAPI capi, string section, Action<string>? tab)
    {
        var page = new OptionPage("vs-interface", Lang.Get("setting-interface-header"), section);
        var largest = ScreenManager.Platform?.ScreenSize.Width > 3000 ? 24 : 16;
        if (!NotNull(capi) || !Assert(largest >= 16)) return page;
        _ = page.Setting("guiscale", 4, largest, 1, () => Math.Round(ClientSettings.GUIScale * 8),
                v => ClientSettings.GUIScale = (float)(v / 8))
            .Format(v => (v / 8).ToString("0.###", CultureInfo.InvariantCulture) + "x");
        _ = Languages(page);
        _ = page.Setting("autochat", () => ClientSettings.AutoChat, on => ClientSettings.AutoChat = on)
            .Setting("autochat-selected", () => ClientSettings.AutoChatOpenSelected,
                on => ClientSettings.AutoChatOpenSelected = on)
            .Setting("blockinfohud", () => ClientSettings.ShowBlockInfoHud, on => ClientSettings.ShowBlockInfoHud = on)
            .Setting("blockinteractioninfohud", () => ClientSettings.ShowBlockInteractionHelp,
                on => ClientSettings.ShowBlockInteractionHelp = on)
            .Setting("coordinatehud", () => ClientSettings.ShowCoordinateHud,
                on => ClientSettings.ShowCoordinateHud = on);
        _ = Hands(capi, Minimap(capi, page));
        if (tab is null) return page; // opened on its own: the game's settings are not behind the screen
        return page.Group(T("group-more"))
            .Button(T("original"), T("open"), () => tab("OnGraphicsOptions"), T("original-hint"));
    }

    private static OptionPage Minimap(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page) || capi.World?.Config?.GetBool("allowMap", true) == false) return page;
        return page.Setting("minimaphud", () => capi.Settings.Bool["showMinimapHud"],
            on => capi.Settings.Bool["showMinimapHud"] = on);
    }

    private static OptionPage Hands(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page)) return page;
        return page.Setting("immersivemousemode", () => ClientSettings.ImmersiveMouseMode,
                on => ClientSettings.ImmersiveMouseMode = on)
            .Setting("immersivefpmode", () => ClientSettings.ImmersiveFpMode, on => ClientSettings.ImmersiveFpMode = on)
            .Setting("fpmodeyoffset", -100, 10, 1, () => Math.Round(ClientSettings.FpHandsYOffset * 100),
                v => ClientSettings.FpHandsYOffset = (float)(v / 100))
            .Setting("fpmodefov", 70, 90, 1, () => ClientSettings.FpHandsFoV, v => ClientSettings.FpHandsFoV = (int)v, "°")
            .Setting("developermode", () => ClientSettings.DeveloperMode, DeveloperMode);
    }

    private static OptionPage? Developer(string section)
    {
        if (!NotNull(section) || !ClientSettings.DeveloperMode || !Assert(section.Length > 0)) return null;
        return new OptionPage("vs-developer", Lang.Get("setting-dev-header"), section)
            .Setting("errorreporter", () => ClientSettings.StartupErrorDialog,
                on => ClientSettings.StartupErrorDialog = on)
            .Setting("extdebuginfo", () => ClientSettings.ExtendedDebugInfo,
                on => ClientSettings.ExtendedDebugInfo = on)
            .Setting("opengldebug", () => ClientSettings.GlDebugMode, on => ClientSettings.GlDebugMode = on)
            .Setting("openglerrorchecking", () => ClientSettings.GlErrorChecking,
                on => ClientSettings.GlErrorChecking = on)
            .Setting("debugtexturedispose", () => RuntimeEnv.DebugTextureDispose,
                on => RuntimeEnv.DebugTextureDispose = on)
            .Setting("debugvaodispose", () => RuntimeEnv.DebugVAODispose, on => RuntimeEnv.DebugVAODispose = on)
            .Setting("debugsounddispose", () => RuntimeEnv.DebugSoundDispose, on => RuntimeEnv.DebugSoundDispose = on)
            .Setting("fasterstartup", () => ClientSettings.OffThreadMipMapCreation,
                on => ClientSettings.OffThreadMipMapCreation = on);
    }

    private static OptionPage Languages(OptionPage page)
    {
        if (!NotNull(page) || ScreenManager.Platform?.AssetManager is null) return page; // no game assets: not in a running client
        GuiCompositeSettings.getLanguages(out var gameCodes, out var gameNames);
        if (!Assert(gameCodes.Length == gameNames.Length) || !Assert(gameCodes.Length is > 0 and <= MaxLanguages)) return page;
        var sorted = gameCodes.Zip(gameNames, (code, name) => (Code: code, Name: Readable(name)))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var (codes, names) = (Array.ConvertAll(sorted, l => l.Code), Array.ConvertAll(sorted, l => l.Name));
        var restart = Lang.Get("setting-notice-restart");
        return page.Choice(Name("language"), names, () => Math.Max(0, Array.IndexOf(codes, ClientSettings.Language)),
            i => Language(Index(i, codes.Length) ? codes[i] : ClientSettings.Language), Hover("language") + "\n\n" + restart);
    }

    // "Deutsch / German (100% complete)" -> "Deutsch\tGerman": native name if the game's font draws it, else English
    // first
    internal static string Readable(string name)
    {
        if (!NotNull(name)) return "";
        var split = name.IndexOf(" / ", StringComparison.Ordinal);
        var own = (split < 0 ? name : name[..split]).Trim();
        var english = split < 0 ? own : name[(split + 3)..];
        var cut = english.IndexOf(" (", StringComparison.Ordinal);
        english = (cut > 0 ? english[..cut] : english).Trim();
        var main = Drawable(own) && own.Length > 0 ? own : english;
        var aside = main == own ? english : "";
        return Assert(main.Length > 0) && Drawable(aside) && aside.Length > 0 && aside != main ? main + "\t" + aside : main;
    }

    // Below Hebrew: scripts the game's font has glyphs for
    private static bool Drawable(string text) => NotNull(text) && text.All(c => c < '\u0590');

    // Language and fonts, saved at once. Windows takes system fonts for CJK and Thai; others keep sans-serif.
    private static void Language(string lang)
    {
        var startup = Lang.CurrentLocale ?? "en"; // the language the game runs in, the one it started with
        if (!NotNull(lang) || !Assert(lang.Length > 0)) return;
        ClientSettings.Language = lang;
        if (lang.StartsWith("zh-", StringComparison.Ordinal) || lang is "ar" or "ja" or "ko" or "th") LocalFonts(lang, startup);
        else
        {
            if (Array.IndexOf(LocalFontNames, ClientSettings.DefaultFontName) >= 0) ClientSettings.DefaultFontName = "sans-serif";
            if (ClientSettings.DefaultFontName == "sans-serif") ClientSettings.DecorativeFontName = "Lora";
        }

        _ = ClientSettings.Inst.Save(true);
    }

    private static readonly string[] LocalFontNames =
        ["meiryo", "Malgun Gothic", "Leelawadee UI Semilight", "Microsoft YaHei Light", "Microsoft JhengHei UI Light"];

    private static void LocalFonts(string lang, string startup)
    {
        if (!NotNull(lang) || !NotNull(startup)) return;
        if (RuntimeEnv.OS != OS.Windows)
        {
            if (lang != startup && ClientSettings.DefaultFontName == "sans-serif") ClientSettings.DecorativeFontName = "sans-serif";
            return;
        }

        (ClientSettings.DefaultFontName, ClientSettings.DecorativeFontName) = lang switch
        {
            "ko" => ("Malgun Gothic", "Malgun Gothic"),
            "th" => ("Leelawadee UI Semilight", "Leelawadee UI"),
            "ja" => ("meiryo", "meiryo"),
            "zh-cn" => ("Microsoft YaHei Light", "Microsoft YaHei"),
            "zh-tw" => ("Microsoft JhengHei UI Light", "Microsoft JhengHei UI"),
            _ => (ClientSettings.DefaultFontName, "sans-serif")
        };
    }

    // The platform switches raw input itself, no watcher does
    private static void DirectMouse(bool on)
    {
        ClientSettings.DirectMouseMode = on;
        _ = Assert(ClientSettings.DirectMouseMode == on);
        if (NotNull(ScreenManager.Platform)) ScreenManager.Platform.SetDirectMouseMode(on);
    }

    // Off drops every debug setting (the game asks before on; here Apply does)
    private static void DeveloperMode(bool on)
    {
        ClientSettings.DeveloperMode = on;
        if (on) return;
        (ClientSettings.StartupErrorDialog, ClientSettings.ExtendedDebugInfo) = (false, false);
        (ClientSettings.GlDebugMode, ClientSettings.GlErrorChecking) = (false, false);
        (RuntimeEnv.DebugTextureDispose, RuntimeEnv.DebugVAODispose, RuntimeEnv.DebugSoundDispose) = (false, false, false);
        _ = Assert(!ClientSettings.DeveloperMode);
    }
}
