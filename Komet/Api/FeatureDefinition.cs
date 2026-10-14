using System.Reflection;
using HarmonyLib;

namespace Komet.Api;

// Harmony patch kinds for StandDownFor. Body: changes the result; Replacing: a prefix that may skip or alter arguments.
[Flags]
public enum PatchKinds
{
    None = 0,
    Prefix = 1,
    Postfix = 2,
    Transpiler = 4,
    Finalizer = 8,
    InnerPrefix = 16,
    InnerPostfix = 32,
    Body = Transpiler | InnerPrefix | InnerPostfix,
    Replacing = Prefix | Body,
    All = Replacing | Postfix | Finalizer
}

// Another mod's feature for KometFeatures.Register. Id modid:name is also its Harmony id, unpatched at world close
// (after Uninstall) or when its code throws. Shaped/Fingerprint pin the replaced engine bodies (no install on a
// difference). Another mod's StandDownFor-kind patch on Watched stands it down until install/LevelFinalize finds them
// free. Page: RenderPage, ChunksPage, MiscPage, own page id or null. ServerOnly: needs the in-process server.
public sealed class FeatureDefinition(string modId, string name, string title)
{
    public string ModId { get; } = NotNull(modId) ? modId : "";
    public string Name { get; } = NotNull(name) ? name : "";
    public string Title { get; } = NotNull(title) ? title : "";
    public string Id { get; } = modId + ":" + name;
    public string? Hint { get; init; }
    public string? Page { get; init; }
    public string? Group { get; init; }
    public FeatureKnob? Knob { get; init; }
    public Action<Harmony, ILogger>? Install { get; init; }
    public Action? Uninstall { get; init; }
    public Func<IReadOnlyList<MethodBase?>>? Shaped { get; init; }
    public ulong Fingerprint { get; init; }
    public Func<IReadOnlyList<MethodBase?>>? Watched { get; init; }
    public PatchKinds StandDownFor { get; init; } = PatchKinds.Replacing;
    public bool ServerOnly { get; init; }
}

// Switch (1/0, engine 0) or slider. get/set: the player's value, saved by the mod. apply: what the patches read, the
// player's value or the engine value while held off or stood down; never saved. Main thread only.
public sealed class FeatureKnob
{
    private FeatureKnob(int min, int max, int engine, string unit, Func<int> get, Action<int> set, Action<int> apply)
    {
        (Min, Max, Engine, Unit, Get, Set, Apply) = (min, max, engine, unit, get, set, apply);
        _ = NotNull(unit) && NotNull(apply);
    }

    public int Min { get; }
    public int Max { get; }
    public int Engine { get; } // the value that leaves the game's own behaviour
    public string Unit { get; }
    public bool IsSwitch => Min == 0 && Max == 1;
    internal Func<int> Get { get; }
    internal Action<int> Set { get; }
    internal Action<int> Apply { get; }
    internal bool Valid => Min < Max && Engine >= Min && Engine <= Max;

    public static FeatureKnob Switch(Func<bool> get, Action<bool> set, Action<bool> apply) =>
        NotNull(get) && NotNull(set) && NotNull(apply)
            ? new FeatureKnob(0, 1, 0, "", () => get() ? 1 : 0, v => set(v != 0), v => apply(v != 0))
            : Refused();

    // engine lies in min..max; unit follows the value in the row (ms)
    public static FeatureKnob Range(int min, int max, int engine, Func<int> get, Action<int> set, Action<int> apply,
        string unit = "") =>
        NotNull(get) && NotNull(set) && NotNull(apply) && NotNull(unit)
            ? new FeatureKnob(min, max, engine, unit, get, set, apply)
            : Refused();

    private static FeatureKnob Refused()
    {
        var knob = new FeatureKnob(0, 0, 0, "", static () => 0, static _ => { }, static _ => { });
        _ = Assert(!knob.Valid) && Assert(!knob.IsSwitch);
        return knob;
    }
}
