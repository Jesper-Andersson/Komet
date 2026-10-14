using System.Reflection;
using Komet.Api;

namespace Komet.Core;

// The guard of a feature that replaces or skips engine bodies: Matched when they are the verified ones, Blocked unless
// matched, ready (say a rewrite took) and patched by no other mod. Recheck runs at install and on LevelFinalize, when every
// mod has patched; patches of Kinds on the first Watched seams count as another mod's unless they are Own's. Unblocked:
// Blocked's value before the first install.
internal sealed class SeamGuard(string feature, PatchKinds kinds, Type[] own, int watched = EngineShape.MaxSeams,
    bool unblocked = false)
{
    private ILogger? _logger;
    private bool _foreign;

    public MethodBase?[] Seams { get; private set; } = [];
    public bool Matched { get; private set; }
    public bool Blocked { get; private set; } = !unblocked;

    // Install's start: the seams found, nothing matched, every call the engine's
    public MethodBase?[] Reset(MethodBase?[] seams, ILogger? logger)
    {
        (Seams, _logger, Matched, Blocked, _foreign) = (seams, logger, false, true, false);
        _ = NotNull(seams) && Assert(own.Length > 0);
        return seams;
    }

    // The bodies against the fingerprint of the version written against (a mismatch logged once)
    public bool Match(ReadOnlySpan<MethodBase?> bodies, ulong fingerprint)
    {
        Matched = EngineShape.Matches(bodies, fingerprint, feature, _logger);
        _ = Assert(!Matched || bodies.Length > 0) && Assert(!Matched || Seams.Length > 0);
        return Matched;
    }

    // ready: what else the feature needs to replace the bodies
    public void Recheck(bool ready = true)
    {
        var seamed = Matched && ready && Assert(watched > 0);
        var span = Seams.AsSpan(0, Math.Min(watched, Seams.Length));
        Report(seamed, seamed && EngineShape.Foreign(span, kinds, null, own));
    }

    // For a feature that asks its own question of which patches are foreign
    public void Report(bool seamed, bool foreign)
    {
        _foreign = EngineShape.Report(_logger, feature, _foreign, seamed && foreign);
        Blocked = !seamed || _foreign;
        _ = Assert(seamed || Blocked) && NotNull(feature);
    }
}
