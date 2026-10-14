using Komet.Api;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// What the modules' feature lists build their entries with (using static)
internal static class FeatureKit
{
    // A probe's answer from a guard's Matched and Blocked
    public static FeatureState Found(bool matched, bool blocked, FeatureState blockedAs = StoodDown) =>
        (matched, blocked) switch
        {
            (false, _) => EngineChanged,
            (_, true) => blockedAs,
            _ => Active
        };

    // A 0/1 knob over a bool static
    public static Knob Switch(string key, string? page, string? group, Func<bool> get, Action<bool> set,
        string? json = null) =>
        Assert(key.Length > 0) && NotNull(get) && NotNull(set)
            ? new Knob(key, page, group, 0, 1, 0, () => get() ? 1 : 0, value => set(value != 0), json)
            : throw new ArgumentException("knob without a name", nameof(key));
}

// A probe's reflection answer, asked again only after Epoch moved (each world's install)
internal sealed class ProbeTarget(Func<object?> find)
{
    public static int Epoch { get; set; }
    private object? _value;
    private int _at = -1;

    public bool Missing
    {
        get
        {
            if (_at != Epoch) (_value, _at) = (find(), Epoch);
            return _value is null;
        }
    }
}
