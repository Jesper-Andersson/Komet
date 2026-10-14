using System.Runtime.Serialization;
using Komet.Core;
using Komet.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Komet.Hud;

[JsonConverter(typeof(StringEnumConverter))]
internal enum HudCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal readonly record struct HudRange(double Min, double Max, double Step, double Factor, string Unit)
{
    public bool Contains(double value) => Finite(value) && value >= Min && value <= Max;

    public string Text(double value) => Assert(Factor > 0) && Assert(Unit.Length < 8)
        ? (HudText.Format(value * Factor, "N0") + " " + Unit).TrimEnd()
        : "";
}

internal sealed class HudSettings
{
    internal const string FileName = "komet.json", OldFileName = "komet-hud.json"; // the name before 3.0

    public const int SlowEvery = 4, MaxPinnedMods = 3;
    public const double ScreenMargin = 8, SnapStep = 8;
    public static readonly HudRange OpacityRange = new(0, 1, 0.05, 100, "%"), ScaleRange = new(0.5, 2, 0.1, 100, "%");
    public static readonly HudRange IntervalRange = new(0.1, 1, 0.05, 1000, "ms"), BenchRange = new(5, 120, 5, 1, "s");
    public static readonly HudRange ToastRange = new(0, 200, 5, 1, "ms");

    // The knob part of the file (Preset, Chosen, Profiled and the knob keys below are its)
    [JsonIgnore]
    public KometSettings Knobs { get; } = new();

    [JsonExtensionData]
    internal Dictionary<string, JToken> KnobJson => Knobs.Json; // the knobs, as top-level keys of the file

    // Overlay (F7); the window (Ctrl+F7) and the debug window (Ctrl+F8) are dialogs of their own
    public bool Visible { get; set => Set(ref field, value); }

    public HudCorner Corner
    {
        get;
        set { if (Assert(value is >= HudCorner.TopLeft and <= HudCorner.BottomRight)) Set(ref field, value); }
    }

    public double Opacity { get; set { if (Assert(OpacityRange.Contains(value))) Set(ref field, value); } } = 0.6;
    public double Interval { get; set { if (Assert(IntervalRange.Contains(value))) Set(ref field, value); } } = 0.25;
    public double BenchSeconds { get; set { if (Assert(BenchRange.Contains(value))) Set(ref field, value); } } = 30;
    public bool ShowFps { get; set => Set(ref field, value); } = true;
    public bool ShowLows { get; set => Set(ref field, value); } = true;
    public bool ShowFrametime { get; set => Set(ref field, value); } = true;
    public bool ShowGraph { get; set => Set(ref field, value); } = true;
    public bool ShowMods { get; set => Set(ref field, value); } = true;
    public bool ShowPins { get; set => Set(ref field, value); } = true;

    // A spike over this many ms toasts in the overlay with its cause; 0: never
    public double ToastMs { get; set { if (Assert(ToastRange.Contains(value))) Set(ref field, value); } } = 50;

    public List<string> PinnedMods { get; } = [];

    public string Preset
    {
        get => Knobs.Preset;
        set => Knobs.Preset = value;
    }

    public List<string> Chosen => Knobs.Chosen;

    public int Profiled
    {
        get => Knobs.Profiled;
        set => Knobs.Profiled = value;
    }

    public HudSettings() => Knobs.Changed += NotifyChanged;

    [OnDeserializing]
    private void Reading(StreamingContext context)
    {
        Knobs.Reading();
        _ = Assert(PinnedMods.Count == 0);
    }

    // Plain properties: a drag must not end the HUD's interval on every move.
    public string WindowTab { get; set; } = "";
    public double WindowX { get; set; } = double.NaN; // NaN: centred
    public double WindowY { get; set; } = double.NaN;
    public string DebugLanguage { get; set => Set(ref field, value is "de" ? "de" : "en"); } = "en";
    public bool UpdateCheck { get; set => Set(ref field, value); } // asks GitHub once per start, opt-in
    public bool UpdateAsked { get; set => Set(ref field, value); } // the opt-in dialog is shown until answered
    public double FontScale { get; set { if (Assert(ScaleRange.Contains(value))) Set(ref field, value); } } = 1.0;

    public bool ShowAdvanced { get; set => Set(ref field, value); }

    public event Action? Changed;

    private void Set<T>(ref T target, T value)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return;
        target = value;
        Changed?.Invoke();
    }

    public void TogglePin(string mod)
    {
        if (string.IsNullOrEmpty(mod) || !Assert(PinnedMods.Count <= MaxPinnedMods)) return;
        if (!PinnedMods.Remove(mod))
        {
            if (PinnedMods.Count >= MaxPinnedMods) PinnedMods.RemoveAt(0);
            PinnedMods.Add(mod);
        }

        NotifyChanged();
    }

    public void ResetPositions() => (WindowX, WindowY) = (double.NaN, double.NaN);

    private void NotifyChanged() => Changed?.Invoke();

    // The update notice is consent, not a display setting
    public void ResetDefaults()
    {
        var d = new HudSettings();
        (Corner, Opacity, FontScale, Interval, BenchSeconds) =
            (d.Corner, d.Opacity, d.FontScale, d.Interval, d.BenchSeconds);
        (ShowFps, ShowLows, ShowFrametime, ShowGraph, ShowMods, ShowPins, ToastMs) =
            (d.ShowFps, d.ShowLows, d.ShowFrametime, d.ShowGraph, d.ShowMods, d.ShowPins, d.ToastMs);
        PinnedMods.Clear();
        Knobs.UseDetected();
        ResetPositions();
    }

    public static HudSettings Load(ICoreClientAPI capi)
    {
        HardwareProfile.Detect(capi.Logger);
        var settings = Read(capi);
        settings.Knobs.Apply(capi.Logger, FileName);
        return settings;
    }

    // A komet-hud.json from before 3.0 becomes komet.json once
    private static void Renamed(ILogger logger)
    {
        var (old, path) = (Path.Combine(GamePaths.ModConfig, OldFileName), Path.Combine(GamePaths.ModConfig, FileName));
        try
        {
            if (!File.Exists(old) || File.Exists(path)) return;
            File.Move(old, path);
            logger.Notification("Komet: settings moved from {0} to {1}", OldFileName, FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.Warning("Komet: {0} not renamed to {1} ({2}); defaults apply", OldFileName, FileName, e.Message);
        }
    }

    private static HudSettings Read(ICoreClientAPI capi)
    {
        Renamed(capi.Logger);
        try
        {
            var loaded = capi.LoadModConfig<HudSettings>(FileName) ?? new HudSettings();
            _ = loaded.PinnedMods.RemoveAll(string.IsNullOrEmpty); // a null pin would throw in the overlay's mod lookup every frame
            if (loaded.PinnedMods.Count > MaxPinnedMods) loaded.PinnedMods.RemoveRange(0, loaded.PinnedMods.Count - MaxPinnedMods);
            return Assert(loaded.PinnedMods.Count <= MaxPinnedMods) ? loaded : new HudSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} unreadable, using defaults ({1})", FileName, e.Message);
            if (e is JsonException) Keep(Path.Combine(GamePaths.ModConfig, FileName)); // the next save would overwrite a hand edit's typo
            return new HudSettings();
        }
    }

    private static void Keep(string path)
    {
        if (!NotNull(path) || !Assert(path.Length > 0)) return;
        try
        {
            File.Copy(path, path + ".broken", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no copy: the warning above names the file
        }
    }

    [OnSerializing]
    private void Serializing(StreamingContext context) => Knobs.Writing();

    public void Save(ICoreClientAPI capi)
    {
        if (!NotNull(capi)) return;
        try
        {
            capi.StoreModConfig(this, FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} not written ({1})", FileName, e.Message);
        }
    }

    public void RegisterHotkeys(ICoreClientAPI capi, Action window, Action debug)
    {
        if (!NotNull(capi) || !NotNull(capi.Input) || !NotNull(window) || !NotNull(debug)) return;
        Hotkey(capi, "toggle", GlKeys.F7, false, () =>
        {
            Visible = !Visible;
            capi.ShowChatMessage(HudText.Translate($"hud-toggle-{(Visible ? "on" : "off")}"));
        });
        Hotkey(capi, "window", GlKeys.F7, true, window);
        Hotkey(capi, "debug", GlKeys.F8, true, debug);
    }

    private static void Hotkey(ICoreClientAPI capi, string name, GlKeys key, bool ctrl, Action act)
    {
        if (!Assert(name.Length > 0) || !NotNull(act)) return;
        var code = "komet-hud-" + name;
        capi.Input.RegisterHotKey(code, HudText.Translate("hotkey-hud-" + name), key, HotkeyType.HelpAndOverlays,
            ctrlPressed: ctrl);
        capi.Input.SetHotKeyHandler(code, _ =>
        {
            act();
            return true;
        });
    }
}
