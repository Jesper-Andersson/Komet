using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Komet.Api;
using Komet.Hud;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// Main thread only. A hold pins a feature's knobs at engine values (counted); the last release writes back the wanted
// value. HUD rows are translated lazily, so drawing allocates nothing.
internal static partial class Features
{
    public const int MaxFeatures = 128, MaxHolds = 96;
    private const int MaxId = 128;

    private static readonly bool[] Installed = new bool[MaxFeatures];
    private static readonly int[] Holds = new int[MaxFeatures];
    private static readonly FeatureState[] States = new FeatureState[MaxFeatures];
    private static bool _remote; // the server runs elsewhere: server features stay out

    public static ReadOnlySpan<Feature> All => Table;

    public static int NotActive { get; private set; }

    // A throw propagates: Komet stands down as a whole.
    public static void Install(FeatureContext context, FeatureStage stage)
    {
        if (!NotNull(context) || !Assert(Table.Length <= MaxFeatures)) return;
        if (stage == FeatureStage.Main) ProbeTarget.Epoch++; // a new world: the probes look again
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[i].Stage == stage)
                InstallAt(context, i);
        Poll();
    }

    // Remote server: marked installed without patches
    internal static void InstallAt(FeatureContext context, int feature)
    {
        if (!NotNull(context) || !Index(feature, Count)) return;
        _remote = !context.LocalServer;
        if (!At(feature).ServerOnly || context.LocalServer) At(feature).Install?.Invoke(context);
        Installed[feature] = true;
    }

    // Every mod has patched now; registrations end
    public static void Recheck()
    {
        if (!Assert(Count <= MaxFeatures) || !Assert(Installed.Length == MaxFeatures)) return;
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Installed[i])
                At(i).Recheck?.Invoke();
        _sealed = true;
        Poll();
    }

    // Reverse install order, before UnpatchAll; every feature, installed or not (a stand-down clears all).
    public static void Stop(FeatureContext? context)
    {
        if (!Assert(Table.Length <= MaxFeatures) || !Assert(Table.Length > 0)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[^(i + 1)].Stage != FeatureStage.Main)
                Table[^(i + 1)].Stop?.Invoke(context);
        for (var i = 0; i < Math.Min(Added.Count, MaxExternal); i++) Added[^(i + 1)].Feature.Stop?.Invoke(context);
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[^(i + 1)].Stage == FeatureStage.Main)
                Table[^(i + 1)].Stop?.Invoke(context);
    }

    public static void Unpatched()
    {
        if (!Assert(Table.Length <= MaxFeatures) || !Assert(Table.Length > 0)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++) Table[i].Unpatched?.Invoke();
        Close();
    }

    // Silently: holds end and write back, other mods' features go
    internal static void Close()
    {
        if (!Assert(Holds.Length == MaxFeatures) || !Assert(Count <= MaxFeatures)) return;
        KometFeatures.Forget();
        KometDebug.Forget();
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Holds[i] > 0)
            {
                Holds[i] = 1;
                _ = Release(i);
            }

        Drop();
        Array.Clear(Installed);
        _remote = false;
        Poll();
    }

    public static void Poll()
    {
        if (!Assert(Count <= MaxFeatures) || !Assert(States.Length == MaxFeatures)) return;
        var (changed, off) = (false, 0);
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
        {
            var (state, previous) = (Evaluate(i), States[i]);
            if (state != Active) off++;
            if (state == previous) continue;
            (States[i], changed) = (state, true);
            KometFeatures.Raise(At(i).Id, previous, state);
        }

        NotActive = off;
        _ = Assert(off <= Count) && Assert(!changed || Count > 0);
    }

    internal static FeatureState Evaluate(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] >= 0)) return Unknown;
        var f = At(feature);
        if (!Installed[feature]) return Pending;
        if (f.ServerOnly && _remote) return NotApplicable;
        if (f.Probe?.Invoke() is { } probed && probed != Active) return probed;
        if (Holds[feature] > 0) return HeldOff;
        if (f.Knobs.Length == 0) return Active;
        foreach (var knob in f.Knobs.Bounded(Knobs.MaxKnobs))
            if (knob.Get() != knob.Engine)
                return Active;
        return Off;
    }

    public static FeatureState StateOf(int feature) =>
        Index(feature, Count) && Assert(States.Length == MaxFeatures) ? States[feature] : Unknown;

    // For the HUD's hint; "" when all are active
    public static string FirstNotActive()
    {
        if (!Assert(Count <= MaxFeatures) || !Assert(States.Length == MaxFeatures)) return "";
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
        {
            var key = States[i] switch
            {
                Active => "",
                Pending => "hud-feature-pending",
                Off => "hud-feature-off",
                HeldOff => "hud-feature-held",
                StoodDown => "hud-feature-stooddown",
                EngineChanged => "hud-feature-changed",
                NotApplicable => "hud-feature-notapplicable",
                _ => "hud-feature-failed"
            };
            if (key.Length > 0) return At(i).Id + ": " + HudText.Translate(key);
        }

        return "";
    }

    // Feature id or knob key (UploadCap: ChunkBudget); -1 for neither
    public static int Find(string id)
    {
        if (!NotNull(id) || !Assert(Count <= MaxFeatures)) return -1;
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (string.Equals(At(i).Id, id, StringComparison.Ordinal))
                return i;
        var knob = Knobs.Find(id);
        return knob >= 0 ? Knobs.At(knob).Owner : -1;
    }

    internal static bool Hold(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] < MaxHolds)) return false;
        var knobs = At(feature).Knobs;
        if (knobs.Length == 0) return false;
        if (Holds[feature]++ == 0)
            foreach (var knob in knobs.Bounded(Knobs.MaxKnobs))
                Knobs.Park(Knobs.Find(knob.Key));
        Poll();
        return true;
    }

    internal static bool Release(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] >= 0) || Holds[feature] == 0) return false;
        if (--Holds[feature] == 0)
            foreach (var knob in At(feature).Knobs.Bounded(Knobs.MaxKnobs))
                Knobs.Restore(Knobs.Find(knob.Key));
        Poll();
        return true;
    }

    public static bool Held(int knob) =>
        Index(knob, Knobs.Count) && Index(Knobs.At(knob).Owner, Count) && Holds[Knobs.At(knob).Owner] > 0;

    // First holder and reason under a held knob's option; null while free
    public static string? LockText(int knob)
    {
        if (!Index(knob, Knobs.Count) || !Held(knob)) return null;
        var text = HudText.Translate("settings-held");
        if (Holder(Knobs.At(knob).Owner) is { } hold) text += "\n" + hold.ModId + ": " + hold.Reason;
        return Assert(text.Length > 0) ? text : null;
    }

    public static string HoldsText()
    {
        var text = new StringBuilder();
        if (!Assert(Count <= MaxFeatures) || !Assert(Holds.Length == MaxFeatures)) return "";
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Holds[i] > 0)
                _ = text.Append(text.Length > 0 ? ", " : "").Append(At(i).Id)
                    .Append(CultureInfo.InvariantCulture, $" ({Holds[i]})");
        return text.Length > 0 ? text.ToString() : "none";
    }

    public static bool IsExternalId(string id)
    {
        if (!NotNull(id) || id.Length > MaxId) return false;
        var match = ExternalId().IsMatch(id);
        return Assert(!match || id.IndexOf(':', StringComparison.Ordinal) > 0) && match;
    }

    [GeneratedRegex("^[a-z0-9]+:[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ExternalId();
}
