using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet;

public sealed class KometModSystem : ModSystem, IDisposable
{
    internal const string ModId = "komet";
    private const int HudStageCount = 3, PoolMs = 250;

    private static readonly EnumRenderStage[] HudStages =
        [EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done];

    private ICoreClientAPI? _api;
    private FeatureContext? _context;
    private long _pool;

    // Server in this process (singleplayer or LAN; set before mods start): only then are server-side patches installed
    internal static bool LocalServer { get; private set; }

    public override void Dispose()
    {
        if (_api is not null && _pool != 0) _api.Event.UnregisterGameTickListener(_pool);
        Features.Stop(_context);
        _context?.Harmony.UnpatchAll(_context.Harmony.Id);
        Features.Unpatched();
        (_context, _api, _pool) = (null, null, 0);
    }

    // Before any mod's Start: the public API logs what it refuses from then on
    public override void StartPre(ICoreAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        ApiEvents.Logger = Mod.Logger;
    }

    // Besides modinfo "side": "Client": a singleplayer server instance's Dispose would clear the client's statics
    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        if (!NotNull(api) || !NotNull(Mod.Logger)) return;
        Attach(Mod.Logger);
        if (!Assert(_context is null) || !Assert(Mod.Info.ModID == ModId)) return;
        try
        {
            Install(api);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // All or nothing. RegisterRenderer throws after earlier stages when another mod took the HUD's order.
            Mod.Logger.Error("Komet: install failed, Komet stands down: {0}", e);
            api.Event.LevelFinalize -= Features.Recheck;
            if (_context?.Overlay is { } overlay)
                foreach (var stage in HudStages.Bounded(HudStageCount))
                    api.Event.UnregisterRenderer(overlay, stage);
            Dispose();
        }
    }

    private void Install(ICoreClientAPI api)
    {
        LocalServer = api.IsSinglePlayer;
        (_context, _api) = (new FeatureContext(new Harmony(Mod.Info.ModID), api, Mod.Logger, LocalServer), api);
        Mod.Logger.Notification(LocalServer
            ? "Komet: the server runs in this process, its chunk and worldgen patches are installed"
            : "Komet: remote server, Komet patches the client only");
        Features.Install(_context, FeatureStage.Main);
        Features.InstallQueued(_context);
        api.Event.LevelFinalize += Features.Recheck;
        _pool = api.Event.RegisterGameTickListener(dt =>
        {
            if (!NotNull(_api) || !Finite(dt)) return;
            WorkerPool.Steer(_api.World as ClientMain);
            Features.Poll();
        }, PoolMs);
        if (NotNull(_context.Overlay))
            foreach (var stage in HudStages.Bounded(HudStageCount))
                api.Event.RegisterRenderer(_context.Overlay, stage, "komet-hud");
        Mod.Logger.Notification("Komet HUD ready – F7 toggles it, .komet or Escape → Settings opens the options");
        Features.Install(_context, FeatureStage.Last);
    }
}
