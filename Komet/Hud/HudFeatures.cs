using Komet.Api;
using Komet.Core;
using static Komet.Core.FeatureKit;

namespace Komet.Hud;

// The HUD itself (its overlay owns the settings and windows) and the interaction-help recompose skip.
// In install order; Core's Features places them among the other modules'.
internal static class HudFeatures
{
    internal static readonly Feature[] All =
    [
        new("Hud")
        {
            Install = c => c.Overlay = new HudOverlay(c.Api),
            Stop = c =>
            {
                c?.Overlay?.Dispose();
                KometOptions.Clear(); // the mods' pages belong to the world that closes
            }
        },
        new("HelpCompose")
        {
            Install = c => HelpCompose.Install(c.Harmony, c.Logger),
            Knobs = [Switch("HelpCompose", "render", "drawing", () => HelpCompose.Enabled, on => HelpCompose.Enabled = on)],
            Recheck = HelpCompose.Recheck,
            Probe = () => Found(HelpCompose.Matched, HelpCompose.Blocked),
            Shapes = [new EngineBodies(HelpCompose.Seams, HelpCompose.Fingerprint)]
        }
    ];
}
