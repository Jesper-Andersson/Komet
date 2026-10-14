using System.Reflection;
using Komet.Core;

namespace Komet.Api;

// What a feature does right now; the first that applies wins. Unknown: no feature has the id.
public enum FeatureState
{
    Unknown,
    Pending, // not installed (yet)
    Active,
    Off, // every knob at its engine value: the player switched it off
    HeldOff, // a mod holds it at the game's own behaviour (KometFeatures.HoldOff)
    StoodDown, // another mod patches what it relies on
    EngineChanged, // the game's bodies are not the ones it was written against: not installed
    NotApplicable, // server code on a remote server, hardware or a driver without what it needs
    Failed // it threw, or its own guard gave up
}

// Komet's features (README names) and other mods' (modid:name). Call from an own class only when IsModEnabled("komet"),
// main thread only. Holds, registrations and subscriptions end when the world closes.
public static class KometFeatures
{
    // Komet's options pages a FeatureDefinition.Page may name; any other id is one of the mod's own KometOptions pages
    public const string RenderPage = "komet-render", ChunksPage = "komet-chunks", MiscPage = "komet-misc";
    public const int MaxFeatures = Features.MaxFeatures, MaxExternal = Features.MaxExternal;

    // Installed, switched, held or released, stood down or back. A handler that throws is logged once and unsubscribed.
    public static event EventHandler<FeatureStateChangedEventArgs>? StateChanged;

    // Every feature in install order, Komet's first; a copy
    public static IReadOnlyList<FeatureInfo> Snapshot()
    {
        var features = Features.Snapshot();
        return Assert(features.Length <= MaxFeatures) && Assert(features.Length > 0) ? features : [];
    }

    // By feature id, or by the name of one of its switches (UploadCap: ChunkBudget); Unknown for neither
    public static FeatureState StateOf(string id)
    {
        if (!NotNull(id)) return FeatureState.Unknown;
        var feature = Features.Find(id);
        return feature >= 0 && Assert(feature < MaxFeatures) ? Features.Evaluate(feature) : FeatureState.Unknown;
    }

    // Engine behaviour until released or world close, also before Komet started; switchless features can't be held
    public static FeatureHold HoldOff(string id, string modId, string reason) =>
        NotNull(id) && NotNull(modId) && NotNull(reason)
            ? Features.HoldOff(id, modId, reason)
            : new FeatureHold("", "", "", -1);

    // Before Komet started: installed after Komet's own; while Komet runs: at once. false = refused, logged why.
    public static bool Register(FeatureDefinition definition) => NotNull(definition) && Features.Register(definition);

    // What FeatureDefinition.Fingerprint compares, to pin in a mod's own test; 0 for a missing or bodiless method
    public static ulong Fingerprint(IReadOnlyList<MethodBase?> methods) =>
        NotNull(methods) && Assert(methods.Count <= EngineShape.MaxMethods) ? EngineShape.Of([.. methods]) : 0;

    internal static void Raise(string id, FeatureState previous, FeatureState state)
    {
        if (StateChanged is null || !NotNull(id) || !Assert(previous != state)) return;
        var args = new FeatureStateChangedEventArgs(id, previous, state);
        ApiEvents.Raise(StateChanged, h => h(null, args), h => StateChanged -= h, "KometFeatures.StateChanged");
    }

    internal static void Forget() => StateChanged = null;
}

// One feature as Snapshot found it. Owner: mod id ("komet"). HeldBy/Reason: the first hold in place, else null.
public sealed class FeatureInfo
{
    internal FeatureInfo(string id, string title, string owner, FeatureState state, bool canHoldOff)
    {
        (Id, Title, Owner, State, CanHoldOff) = (id, title, owner, state, canHoldOff);
        _ = Assert(id.Length > 0) && Assert(owner.Length > 0);
    }

    public string Id { get; }
    public string Title { get; }
    public string Owner { get; }
    public FeatureState State { get; }
    public bool CanHoldOff { get; } // it has a switch
    public string? HeldBy { get; internal init; }
    public string? Reason { get; internal init; }
}

// A hold from KometFeatures.HoldOff. Release/Dispose let go once, then no-op. IsHolding false for a refused hold too.
public sealed class FeatureHold : IDisposable
{
    internal FeatureHold(string featureId, string modId, string reason, int feature)
    {
        (FeatureId, ModId, Reason, Feature) = (featureId, modId, reason, feature);
        _ = Assert(feature >= -1) && Assert(feature < Features.MaxFeatures);
    }

    public string FeatureId { get; }
    public string ModId { get; }
    public string Reason { get; }
    public bool IsHolding => Features.Holding(this);
    internal int Feature { get; }

    public void Release()
    {
        if (!Assert(Feature >= -1) || !NotNull(ModId)) return;
        _ = Features.Release(this);
    }

    public void Dispose()
    {
        Release();
        _ = Assert(!IsHolding) && NotNull(FeatureId);
    }
}

public sealed class FeatureStateChangedEventArgs(string id, FeatureState previous, FeatureState state) : EventArgs
{
    public string Id { get; } = id;
    public FeatureState Previous { get; } = previous;
    public FeatureState State { get; } = state;
}
