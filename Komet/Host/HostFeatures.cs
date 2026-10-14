using Komet.Core;
using static Komet.Core.FeatureKit;

namespace Komet.Host;

// The dedicated server process for singleplayer.
// In install order; Core's Features places them among the other modules'.
internal static class HostFeatures
{
    internal static readonly Feature[] All =
    [
        new("ServerProcess")
        {
            Install = _ => HostLaunch.Install(),
            Knobs = [Switch("ServerProcess", "misc", "server", () => HostLaunch.Enabled, on => HostLaunch.Enabled = on)
                with { General = true }]
        }
    ];
}
