namespace Komet.Core;

// A preset's value for a knob it governs (Options.Presets; "auto" is balanced or speed)
internal readonly record struct PresetValues(int Quality, int Balanced, int Speed);

// Page/Group: settings placement (null: bench only); label "settings-" + lower-case Key. Engine: the game's behaviour.
// Json: legacy key. Order: table index, Owner: feature index. Other mods' knobs: modid:name.
// Weighed: a trade-off (quality, memory or loading against speed) with a row of its own on its area's page; every other
// switch of a page only makes the engine's own result faster and is exact (listed under one master switch with
// "advanced"). General: a preference on the Overview beside the preset. Presets: the values the presets give it (the
// player's own choice wins). Suggest: the value this computer needs (HardwareProfile), null for Komet's default.
internal sealed record Knob(
    string Key, string? Page, string? Group, int Min, int Max, int Engine, Func<int> Get, Action<int> Set,
    string? Json = null)
{
    public string Persisted => Json ?? Key;
    public bool IsSwitch => Min == 0 && Max == 1;
    public string Unit { get; init; } = "";
    public int Order { get; init; }
    public int Owner { get; init; }
    public bool Weighed { get; init; }
    public bool General { get; init; }
    public PresetValues? Presets { get; init; }
    public System.Func<HardwareProfile.Facts, int?>? Suggest { get; init; }

    public string Label => Key.ToLowerInvariant();
}

// Komet's knobs in dialog order, then other mods' (per world). Write is the one path to a feature static: a held knob
// stays at engine value and writes become the wanted value.
internal static class Knobs
{
    public const int MaxKnobs = 160;

    private static readonly Knob[] Table = Collect();
    private static readonly List<Knob> Added = [];
    private static readonly int[] Wanted = new int[MaxKnobs];

    public static int BuiltInCount => Table.Length; // What komet.json saves and the settings pages show

    public static int Count => Table.Length + Added.Count;

    public static ReadOnlySpan<Knob> BuiltIn => Table;

    private static Knob[] Collect()
    {
        var features = Features.All;
        var table = new List<Knob>(MaxKnobs);
        for (var f = 0; f < Math.Min(features.Length, Features.MaxFeatures); f++)
            foreach (var knob in features[f].Knobs.Bounded(MaxKnobs))
                if (Assert(table.Count < MaxKnobs) && Assert(table.TrueForAll(k => k.Key != knob.Key)))
                    table.Add(knob with { Order = table.Count, Owner = f });
        return [.. table];
    }

    public static Knob At(int knob)
    {
        if (!Index(knob, Count) || !Assert(Count <= MaxKnobs)) return Table[0];
        return knob < Table.Length ? Table[knob] : Added[knob - Table.Length];
    }

    public static string Name(int knob) => Index(knob, Count) && Assert(Count <= MaxKnobs) ? At(knob).Key : "";

    public static int Find(string key)
    {
        if (!NotNull(key) || !Assert(Count <= MaxKnobs)) return -1;
        for (var i = 0; i < Math.Min(Count, MaxKnobs); i++)
            if (string.Equals(At(i).Key, key, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public static bool InRange(int knob, int value) =>
        Index(knob, Count) && Assert(At(knob).Min <= At(knob).Max) && value >= At(knob).Min && value <= At(knob).Max;

    // held: wanted value; engine: Engine values; builtIn: Komet's knobs alone
    public static int[] Snapshot(bool engine = false, bool builtIn = false)
    {
        var values = new int[builtIn ? Table.Length : Count];
        if (!Assert(values.Length <= MaxKnobs)) return values;
        if (engine)
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++) values[i] = At(i).Engine;
        else
            for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
                values[i] = Features.Held(i) ? Wanted[i] : At(i).Get();

        return values;
    }

    // Only differences: ShaderUseCache.Enabled = true empties its cache even when already true.
    public static int Apply(ReadOnlySpan<int> values)
    {
        if (!Assert(values.Length == Count) || !Assert(values.Length <= MaxKnobs)) return 0;
        var changed = 0;
        for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
            if (InRange(i, values[i]) && Write(i, values[i]))
                changed++;
        Features.Poll();
        return changed;
    }

    public static bool Write(int knob, int value)
    {
        if (!Index(knob, Count) || !Assert(Count <= MaxKnobs)) return false;
        if (Features.Held(knob))
        {
            Wanted[knob] = value; // until the last hold goes
            return false;
        }

        if (At(knob).Get() == value) return false;
        At(knob).Set(value);
        return true;
    }

    internal static void Park(int knob)
    {
        if (!Index(knob, Count) || !Assert(At(knob).Engine >= At(knob).Min)) return;
        Wanted[knob] = At(knob).Get();
        if (Wanted[knob] != At(knob).Engine) At(knob).Set(At(knob).Engine);
    }

    internal static void Restore(int knob)
    {
        if (!Index(knob, Count) || !Assert(InRange(knob, Wanted[knob]))) return;
        if (At(knob).Get() != Wanted[knob]) At(knob).Set(Wanted[knob]);
    }

    internal static int Add(Knob knob)
    {
        if (!NotNull(knob) || !Assert(Added.Count < MaxKnobs) || Count >= MaxKnobs) return -1;
        Added.Add(knob with { Order = Count });
        return Count - 1;
    }

    internal static void Truncate()
    {
        Added.Clear();
        _ = Assert(Count == Table.Length) && Assert(Added.Count == 0);
    }

    // Player value wanted; the mod's apply gets it once (engine value while held)
    internal static void Seed(int knob, int wanted)
    {
        if (!Index(knob, Count) || !Assert(knob >= Table.Length) || !InRange(knob, wanted)) return;
        Wanted[knob] = wanted;
        At(knob).Set(Features.Held(knob) ? At(knob).Engine : wanted);
    }
}
