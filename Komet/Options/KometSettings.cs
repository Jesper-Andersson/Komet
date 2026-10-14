using Komet.Core;
using Newtonsoft.Json.Linq;

namespace Komet.Options;

// The player's knob values, his preset and which knobs he set himself. komet.json holds them beside the HUD's display
// settings: HudSettings is the file's object and hands Preset, Chosen, Profiled and the knob keys through to this.
internal sealed class KometSettings
{
    private static readonly int[] KnobDefaults = Knobs.Snapshot(builtIn: true);

    private static string _preset = Presets.Auto; // the loaded settings' (one instance a client)

    // The knobs, as top-level keys of the file (HudSettings' extension data)
    private readonly Dictionary<string, JToken> _json = [];

    // The player's choice; the bench writes the statics, not these
    private readonly int[] _knobs = Followed();

    public event Action? Changed;

    internal Dictionary<string, JToken> Json => _json;

    // The value a knob the player never set follows: what this computer needs (HardwareProfile), else the preset's,
    // else Komet's default
    internal static int KnobDefault(int knob)
    {
        if (!Index(knob, KnobDefaults.Length) || !Assert(KnobDefaults.Length == Knobs.BuiltInCount)) return 0;
        var key = Knobs.At(knob).Key;
        var suggested = HardwareProfile.Suggestion(key) ?? Presets.Value(_preset, key);
        return suggested is { } value && Knobs.InRange(knob, value) ? value : KnobDefaults[knob];
    }

    private static int[] Followed()
    {
        var values = new int[KnobDefaults.Length];
        for (var i = 0; i < Math.Min(values.Length, Knobs.MaxKnobs); i++) values[i] = KnobDefault(i);
        _ = Assert(values.Length == Knobs.BuiltInCount);
        return values;
    }

    // Presets: auto, quality, balanced or speed
    public string Preset
    {
        get => _preset;
        set
        {
            if (!Presets.Known(value) || value == _preset) return;
            Activate(value);
            Changed?.Invoke();
        }
    }

    private static void Activate(string preset) => _preset = Assert(Presets.Known(preset)) ? preset : Presets.Auto;

    // A preset chosen: the knobs it governs follow it again, every knob not set by the player takes its value
    public void ChoosePreset(string name)
    {
        if (!Presets.Known(name)) return;
        Preset = name;
        _ = Chosen.RemoveAll(static key => Presets.Governs(key));
        for (var i = 0; i < Math.Min(_knobs.Length, Knobs.MaxKnobs); i++)
            if (!Chosen.Contains(Knobs.At(i).Persisted))
                SetKnob(i, KnobDefault(i));
        _ = Assert(Presets.Known(Preset));
    }

    // The player set a knob the preset governs: the preset shows as adjusted
    public bool Adjusted => Chosen.Exists(static key => Presets.Governs(key));

    // Knobs the player set (saved keys); the others follow KnobDefault, so the detected values move with the hardware
    public List<string> Chosen { get; } = [];

    // 0: a file from before Chosen (its values that differ from Komet's defaults count as chosen once); a file without
    // the key reads as 0 (Reading), new settings are 1
    public int Profiled { get; set; } = 1;

    // The file is about to be read into these
    internal void Reading()
    {
        Profiled = 0;
        _ = Assert(Chosen.Count == 0) && Assert(_json.Count == 0);
    }

    // Every knob back to what suits this computer (Komet's default where nothing is suggested)
    public void UseDetected()
    {
        for (var i = 0; i < Math.Min(_knobs.Length, Knobs.MaxKnobs); i++) SetKnob(i, KnobDefault(i));
        Chosen.Clear();
        _ = Assert(Chosen.Count == 0) && Assert(_knobs.Length <= Knobs.MaxKnobs);
    }

    public int Knob(int knob) =>
        Index(knob, _knobs.Length) && Assert(_knobs.Length == Knobs.BuiltInCount) ? _knobs[knob] : 0;

    public void SetKnob(int knob, int value)
    {
        if (!Index(knob, _knobs.Length) || !Knobs.InRange(knob, value)) return;
        var key = Knobs.At(knob).Persisted;
        if (value == KnobDefault(knob)) _ = Chosen.Remove(key); // back to following the computer
        else if (!Chosen.Contains(key)) Chosen.Add(key);
        _ = Knobs.Write(knob, value);
        if (_knobs[knob] != value)
        {
            _knobs[knob] = value;
            Changed?.Invoke();
        }

        Features.Poll();
    }

    // The values read from the file (or the defaults) written to the knobs; a key out of range is logged and its default
    // applies
    internal void Apply(ILogger logger, string fileName)
    {
        var knobs = Knobs.BuiltIn;
        List<string> rejected = [];
        var migrating = Profiled == 0;
        RendererMigrated();
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
        {
            var key = knobs[i].Persisted;
            var value = KnobDefault(i);
            var saved = int.MinValue;
            if (_json.TryGetValue(key, out var token) && !TryKnob(token, i, out saved)) rejected.Add(key);
            else if (token is not null && (Chosen.Contains(key) || (migrating && saved != KnobDefaults[i] && saved != value)))
            {
                value = saved;
                if (!Chosen.Contains(key)) Chosen.Add(key);
            }

            _knobs[i] = value;
            _ = Knobs.Write(i, value);
        }

        _json.Clear();
        HashSet<string> known = [];
        foreach (var knob in knobs.Bounded(Knobs.MaxKnobs)) _ = known.Add(knob.Persisted);
        _ = Chosen.RemoveAll(key => !known.Contains(key)); // knobs since gone
        Profiled = 1;
        if (rejected.Count > 0 && NotNull(logger))
            logger.Warning("Komet HUD: {0} holds values out of range for {1}, their defaults apply", fileName,
                string.Join(", ", rejected));
    }

    // A file from before 3.0's renderer choice: Vulkan-only also needed VulkanCore, VulkanTerrain and VulkanScene, and
    // the hybrid (all on but VulkanOnly) is gone. Vulkan only where VulkanOnly was on with all its parts, else OpenGL
    private void RendererMigrated()
    {
        if (!_json.TryGetValue(VulkanMode.Knob, out var only) || !NotNull(only)) return;
        foreach (var part in (ReadOnlySpan<string>)["VulkanCore", "VulkanTerrain", "VulkanScene"])
            if (_json.TryGetValue(part, out var on) && on is JValue { Type: JTokenType.Boolean, Value: false })
            {
                _json[VulkanMode.Knob] = new JValue(false);
                return;
            }

        _ = Assert(_json.ContainsKey(VulkanMode.Knob));
    }

    // Switch as bool, number as int; anything not a long is out of range (BigInteger throws on a (long) cast).
    private static bool TryKnob(JToken token, int knob, out int value)
    {
        value = token switch
        {
            JValue { Type: JTokenType.Boolean, Value: bool on } => on ? 1 : 0,
            JValue { Type: JTokenType.Integer, Value: long number and >= int.MinValue and <= int.MaxValue } =>
                (int)number,
            _ => int.MinValue
        };
        return NotNull(token) && Knobs.InRange(knob, value);
    }

    // The file is about to be written: every knob under its saved name, a switch as a bool
    internal void Writing()
    {
        var knobs = Knobs.BuiltIn;
        _json.Clear();
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            _json[knobs[i].Persisted] = knobs[i].IsSwitch ? new JValue(_knobs[i] != 0) : new JValue(_knobs[i]);
        _ = Assert(_json.Count == _knobs.Length);
    }
}
