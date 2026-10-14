using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// OpenGL's picture is never shown, so calls Vulkan does not take are missing; each kind logged once.
internal sealed partial class TerrainRenderer
{
    private const int MaxMissing = 64;
    private const string OtherKinds = "other kinds";

    private readonly Dictionary<string, long> _missing = new(StringComparer.Ordinal);

    public long Missed { get; private set; }

    // One more stands for all past MaxMissing
    public int MissedKind => Assert(_missing.Count <= MaxMissing + 1) ? _missing.Count : MaxMissing;

    // Missing only for the frames their port or pipeline was building
    public long MissedWaiting { get; private set; }

    public void Miss(string what)
    {
        if (!NotNull(what) || what.Length == 0) return;
        Missed++;
        ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(_missing, what);
        if (!Unsafe.IsNullRef(ref count))
        {
            count++;
            return;
        }

        var key = _missing.Count < MaxMissing ? what : OtherKinds;
        _missing[key] = _missing.GetValueOrDefault(key) + 1;
        if (key == what) _logger?.Warning("Komet: Vulkan cannot draw {0}: it is missing from the picture", what);
        _ = Assert(_missing.Count <= MaxMissing + 1) && Assert(Missed > 0);
    }

    private const int MaxReasons = 512;
    private static readonly Dictionary<(string, string, string, string), string> Reasons = [];

    // A rejected draw comes again every frame with the same reason: its text is formatted once (keyed by its format and
    // parts), not allocated per draw. Render thread only, like Miss.
    public static string Reason(string format, string a, string b = "", string c = "")
    {
        if (!NotNull(format) || !NotNull(a) || !NotNull(b) || !NotNull(c)) return "";
        if (Reasons.TryGetValue((format, a, b, c), out var made)) return made;
        made = string.Format(CultureInfo.InvariantCulture, format, a, b, c);
        if (Reasons.Count < MaxReasons) Reasons[(format, a, b, c)] = made;
        _ = Assert(made.Length > 0 || format.Length == 0);
        return made;
    }

    public void MissWaiting()
    {
        MissedWaiting++;
        _ = Assert(MissedWaiting >= 0) && Assert(Missed >= 0);
    }

    public string MissedKinds(int most)
    {
        if (_missing.Count == 0 || !Assert(most > 0)) return "";
        var kinds = _missing.OrderByDescending(static m => m.Value).Take(most).Select(static m => $"{m.Key} {m.Value}");
        return string.Join("; ", kinds.Bounded(MaxMissing));
    }
}
