using Komet.Core;
using Komet.Rendering;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Vulkan;

// The Vulkan device, its shader cache and timings, and the Vulkan-only renderer (VulkanOnly).
// In install order; Core's Features places them among the other modules'.
internal static class VulkanFeatures
{
    internal static readonly Feature[] All =
    [
        // the device, started on the first frame after VulkanOnly goes on
        new("VulkanCore")
        {
            Install = c => VulkanCore.Install(c.Api, c.Logger),
            Stop = _ => VulkanCore.Stop(),
            Probe = () => VulkanCore.Failed ? NotApplicable : Active
        },
        // ported shaders, hot uniforms and pipeline cache on disk: known programs skip shaderc
        new("VulkanShaderCache")
        {
            Requires = ["VulkanCore"],
            Install = c => ShaderCache.Install(c.Logger, c.Api),
            Knobs = [Switch("VulkanShaderCache", "vulkan", "vulkan-advanced", () => ShaderCache.Enabled,
                on => ShaderCache.Enabled = on)]
        },
        new("VulkanGpuTimes")
        {
            Requires = ["VulkanCore"],
            Knobs = [Switch("VulkanGpuTimes", "vulkan", "vulkan-advanced", () => VulkanFrame.Measuring,
                on => VulkanFrame.Measuring = on) with { Weighed = true }]
        },
        // Vulkan-only: the terrain pools' face data in 48 bytes a quad (CompactFaces), from the next Vulkan-only start
        new("VulkanCompactFaces")
        {
            Requires = ["VulkanCore"],
            Knobs = [Switch("VulkanCompactFaces", "vulkan", "vulkan-advanced", () => CompactFaces.Enabled, on =>
            {
                var changed = CompactFaces.Enabled != on;
                CompactFaces.Enabled = on;
                if (changed && VulkanRenderer.OnlyOn) TerrainRebuild.Request(); // the pools are made anew in the other format
            })]
        },
        // the renderer: VulkanOnly is the settings page's renderer choice (VulkanMode)
        new("VulkanTerrain")
        {
            Requires = ["VulkanCore"],
            Install = c => VulkanRenderer.Install(c.Harmony, c.Api, c.Logger),
            Shapes =
            [
                new EngineBodies(PoolUpdates.Seams, PoolUpdates.Fingerprint),
                new EngineBodies(VulkanRenderer.BirthSeams, VulkanRenderer.BirthFingerprint)
            ],
            Knobs = [Switch("VulkanOnly", null, null, () => VulkanRenderer.Only,
                    on => VulkanRenderer.Only = on),
                Switch("VulkanPacing", "vulkan", "vulkan-advanced", () => VulkanRenderer.Pacing,
                    on => VulkanRenderer.Pacing = on),
                Switch("VulkanOnlyLean", "vulkan", "vulkan-advanced", () => VulkanRenderer.Lean, on =>
                {
                    var leaner = on && !VulkanRenderer.Lean;
                    VulkanRenderer.Lean = on;
                    if (leaner && VulkanRenderer.OnlyOn) TerrainRebuild.Request(); // OpenGL's copies of the pools go
                }),
                // the bridge pays off on fast monitors
                Switch("VulkanFrameBridge", "vulkan", "vulkan-advanced", () => VulkanRenderer.Bridging,
                    on => VulkanRenderer.Bridging = on) with { Weighed = true, Suggest = hw => hw.RefreshHz >= 100 ? 1 : 0 },
                // the far terrain cache costs one screen-sized layer: off with little video memory, the preset decides above
                Switch("VulkanFarCache", "vulkan", "vulkan-advanced", () => VulkanRenderer.FarCaching,
                    on => VulkanRenderer.FarCaching = on) with
                {
                    Weighed = true, Presets = new(Quality: 0, Balanced: 1, Speed: 1),
                    Suggest = hw => hw.VramGb is > 0 and < 3 ? 0 : null
                },
                Switch("VulkanQuietUniforms", "vulkan", "vulkan-advanced", () => GlTap.QuietUniforms,
                    on => GlTap.QuietUniforms = on)],
            Stop = _ => VulkanRenderer.Stop(),
            Probe = () => VulkanRenderer.Unavailable ? NotApplicable : Active
        }
    ];
}
