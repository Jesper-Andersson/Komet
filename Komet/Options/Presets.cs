using Komet.Core;

namespace Komet.Options;

// One choice for the knobs a player weighs, quality against speed against loading; every other knob follows the computer
// (HardwareProfile) or Komet's default. "auto" is balanced, or speed on a weak computer. A knob the player sets himself
// (KometSettings.Chosen) keeps his value whatever the preset.
internal static class Presets
{
    public const string Auto = "auto";
    public static readonly string[] Names = [Auto, "quality", "balanced", "speed"];

    // The knob's preset values (Knob.Presets: quality, balanced, speed), null where no preset sets it
    private static PresetValues? Of(string key) =>
        NotNull(key) && Knobs.Find(key) is var knob and >= 0 && Assert(knob < Knobs.MaxKnobs) ? Knobs.At(knob).Presets : null;

    public static bool Governs(string key) => Of(key) is not null;

    public static bool Known(string name) => NotNull(name) && Assert(Names.Length == 4) && Array.IndexOf(Names, name) >= 0;

    // A named preset's value for the knob, null where it sets none
    public static int? Value(string preset, string key)
    {
        if (Of(key) is not { } values || !Known(preset)) return null;
        var (quality, balanced, speed) = values;
        return Resolved(preset) switch
        {
            "quality" => quality,
            "speed" => speed,
            _ => Assert(balanced >= 0) ? balanced : 0
        };
    }

    public static string Resolved(string preset)
    {
        if (preset != Auto || !Assert(Known(preset))) return preset;
        return HardwareProfile.Weak ? "speed" : "balanced";
    }
}
