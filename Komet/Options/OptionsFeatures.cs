using Komet.Core;
using static Komet.Core.FeatureKit;

namespace Komet.Options;

// The rebuilt settings menu and its backdrop blur.
// In install order; Core's Features places them among the other modules'.
internal static class OptionsFeatures
{
    internal static readonly Feature[] All =
    [
        new("GraphicsMenu")
        {
            Install = c =>
            {
                if (NotNull(c.Overlay)) GraphicsMenu.Install(c.Harmony, c.Api, c.Overlay.Options, c.Logger);
            },
            Requires = ["Hud"],
            Knobs = [Switch("GraphicsMenu", "misc", "menu", () => GraphicsMenu.Enabled,
                on => GraphicsMenu.Enabled = on) with { General = true }],
            Stop = _ => GraphicsMenu.Clear(),
            Probe = () => Found(GraphicsMenu.Installed, GraphicsMenu.StoodDown),
            Shapes = [new EngineBodies(GraphicsMenu.Shaped, GraphicsMenu.Fingerprint)]
        },
        new("MenuBlur")
        {
            Knobs = [Switch("MenuBlur", "misc", "menu", () => Backdrop.Enabled, on => Backdrop.Enabled = on)
                with { General = true }]
        }
    ];
}
