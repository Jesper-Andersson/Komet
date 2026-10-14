using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Diagnostics;

// Diagnostics' features: the mod and render-cost probes the HUD reads, entity and frame clocks, the hang watch.
// In install order; Core's Features places them among the other modules'.
internal static class DiagnosticsFeatures
{
    internal static readonly Feature[] All =
    [
        // before the HUD, whose settings may switch the mod times on
        new("ModTimes")
        {
            Install = c => ModTimes.Install(c.Harmony, c.Api.ModLoader, c.Logger),
            Probe = () => ModTimes.Replays ? Active : EngineChanged,
            Shapes =
            [
                new EngineBodies(() => [AccessTools.Method(typeof(ClientEventManager),
                    nameof(ClientEventManager.TriggerRenderStage))], ModTimes.Shape)
            ]
        },
        // render cost panel probes; before the HUD, whose settings may enable them
        new("RenderCost") { Install = c => RenderCost.Install(c.Harmony, c.Logger), Stop = _ => RenderCost.Clear() },
        new("EntityTimes")
        {
            Install = c => EntityTimes.Install(c.Harmony),
            Probe = () => EntityTimes.Installed ? Active : EngineChanged
        },
        new("ProfilerPool")
        {
            Install = c => ProfilerPool.Install(c.Harmony),
            Knobs = [Switch("ProfilerPool", "misc", "garbage", () => ProfilerPool.Enabled, on => ProfilerPool.Enabled = on)],
            Probe = () => Found(ProfilerPool.Patched, false)
        },
        new("FrameClock") { Install = c => FrameClock.Install(c.Harmony) },
        new("HangWatch") { Install = c => HangWatch.Start(c.Logger), Stop = _ => HangWatch.Stop() }
    ];
}
