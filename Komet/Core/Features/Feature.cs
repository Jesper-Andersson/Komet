using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Hud;

namespace Komet.Core;

// Main: ModTimes to HangWatch; Last: PreJit, once the HUD renders and every patch is in
internal enum FeatureStage
{
    Main,
    Last
}

// Engine bodies a feature reproduces, skips or replays, and their fingerprint in the version written against
internal readonly record struct EngineBodies(Func<MethodBase?[]> Methods, ulong Expected);

// Overlay: set by the Hud feature for those after it (GraphicsMenu, the renderers).
internal sealed class FeatureContext(Harmony harmony, ICoreClientAPI api, ILogger logger, bool localServer)
{
    // The server process's (HostSystem): no client API there, which no ServerOnly feature's Install reads
    public static FeatureContext Server(Harmony harmony, ILogger logger) =>
        NotNull(harmony) && NotNull(logger) ? new FeatureContext(harmony, null!, logger, true) : throw new ArgumentNullException(nameof(harmony));

    public Harmony Harmony { get; } = harmony;
    public ICoreClientAPI Api { get; } = api;
    public ILogger Logger { get; } = logger;
    public bool LocalServer { get; } = localServer;
    public HudOverlay? Overlay { get; set; }
}

// Install null: the knob is the feature. Stop: before UnpatchAll, reverse order; Unpatched: after. Recheck:
// LevelFinalize.
internal sealed record Feature(string Id)
{
    public Action<FeatureContext>? Install { get; init; }
    public FeatureStage Stage { get; init; }
    public bool ServerOnly { get; init; } // installed only when the server runs in this process
    public string[] Requires { get; init; } = [];
    public Knob[] Knobs { get; init; } = [];
    public Action? Recheck { get; init; }
    public Action<FeatureContext?>? Stop { get; init; }
    public Action? Unpatched { get; init; }
    public Func<FeatureState>? Probe { get; init; }
    public EngineBodies[] Shapes { get; init; } = [];
}
