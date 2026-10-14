using Komet.Core;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Rendering;

// OpenGL-side rendering: shaders, shadows, culling, the terrain pools and frame pacing.
// In install order; Core's Features places them among the other modules'.
internal static class RenderingFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget PotSeam = new(static () => PotCulling.Seams()[0]),
        MapSeam = new(static () => MapTileCulling.Seams()[0]), MapUploadSeam = new(static () => MapTileUpload.Seams()[0]);

    internal static readonly Feature[] All =
    [
        new("ShaderUseCache")
        {
            Install = c => ShaderUseCache.Install(c.Harmony),
            Knobs = [Switch("ShaderUseCache", "render", "drawing", () => ShaderUseCache.Enabled,
                on => ShaderUseCache.Enabled = on)]
        },
        new("DistantShadows")
        {
            Install = c => DistantShadows.Install(c.Harmony, c.Logger),
            Knobs = [Switch("DistantShadows", "render", "drawing", () => DistantShadows.Enabled,
                on => DistantShadows.Enabled = on)
                    with { Weighed = true, Presets = new(Quality: 1, Balanced: 1, Speed: 0) }],
            Probe = () => Found(DistantShadows.Matched && DistantShadows.Injected, DistantShadows.Failed, Failed),
            Shapes = [new EngineBodies(DistantShadows.Seams, DistantShadows.Fingerprint)],
            Stop = _ => DistantShadows.Forget()
        },
        new("LeanVaryings")
        {
            Install = c => LeanVaryings.Install(c.Harmony, c.Api),
            Knobs = [Switch("LeanVaryings", "render", "drawing", () => LeanVaryings.Enabled,
                on => LeanVaryings.Enabled = on)]
        },
        new("SharpUpscale")
        {
            Install = c => SharpUpscale.Install(c.Harmony, c.Api),
            Knobs = [Switch("SharpUpscale", "render", "drawing", () => SharpUpscale.Enabled, on => SharpUpscale.Enabled = on)]
        },
        new("SsaoLean")
        {
            Install = c => SsaoLean.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("SsaoLean", "render", "drawing", () => SsaoLean.Enabled, on => SsaoLean.Enabled = on)
                with { Weighed = true, Presets = new(Quality: 0, Balanced: 0, Speed: 1) }],
            Probe = () => Found(SsaoLean.Matched, false),
            Shapes = [new EngineBodies(SsaoLean.Seams, SsaoLean.Fingerprint)]
        },
        new("LeanGodRays")
        {
            Install = c => LeanGodRays.Install(c.Harmony, c.Api),
            Knobs = [Switch("LeanGodRays", "render", "drawing", () => LeanGodRays.Enabled, on => LeanGodRays.Enabled = on)]
        },
        new("FrustumSweep")
        {
            // the shadow passes' view too, which OcclusionCulling's rows read
            Install = c =>
            {
                FrustumSweep.Install(c.Harmony, c.Logger);
                ShadowView.Install(c.Harmony, c.Api, c.Logger);
            },
            Stop = _ => ShadowView.Clear(), // its api, and with it the world, is not kept until the next one
            Knobs =
            [
                Switch("FrustumSweep", "render", "culling", () => FrustumSweep.Enabled,
                    on => FrustumSweep.Enabled = on)
            ]
        },
        new("FarShadowReuse")
        {
            Install = c => FarShadowReuse.Install(c.Harmony),
            Stop = _ => FarShadowReuse.Clear(),
            Knobs = [Switch("FarShadowReuse", "render", "drawing", () => FarShadowReuse.Enabled,
                on => FarShadowReuse.Enabled = on) with { Weighed = true }]
        },
        new("NearShadowReuse")
        {
            Install = c => NearShadowReuse.Install(c.Harmony),
            Stop = _ => NearShadowReuse.Clear(),
            Knobs = [Switch("NearShadowReuse", "render", "drawing", () => NearShadowReuse.Enabled,
                on => NearShadowReuse.Enabled = on)
                    with { Weighed = true, Presets = new(Quality: 0, Balanced: 1, Speed: 1) }]
        },
        // reads the view FrustumSweep's ShadowView made for the terrain of the same shadow stage
        new("EntityCasters")
        {
            Install = c => EntityCasters.Install(c.Harmony, c.Logger),
            Requires = ["FrustumSweep"],
            Knobs = [Switch("EntityCasters", "render", "culling", () => EntityCasters.Enabled,
                on => EntityCasters.Enabled = on)],
            Recheck = EntityCasters.Recheck,
            Probe = () => Found(EntityCasters.Matched, EntityCasters.Blocked),
            Shapes = [new EngineBodies(EntityCasters.Seams, EntityCasters.Fingerprint)]
        },
        new("IndirectDraw")
        {
            Install = c => IndirectDraw.Install(c.Harmony),
            Knobs = [Switch("IndirectDraw", "render", "drawing", () => IndirectDraw.Enabled,
                on => IndirectDraw.Enabled = on)],
            Probe = () => IndirectDraw.Detected && !IndirectDraw.Supported ? NotApplicable : Active
        },
        // after IndirectDraw, whose RenderMesh prefix hands it the draws
        new("OcclusionCulling")
        {
            Install = c => OcclusionCulling.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("OcclusionCulling", "render", "culling", () => OcclusionCulling.Enabled,
                    on => OcclusionCulling.Enabled = on) with { Weighed = true },
                Switch("GpuTerrainCulling", "render", "culling", () => OcclusionCulling.RowsEnabled,
                    on => OcclusionCulling.RowsEnabled = on) with { Weighed = true }],
            Stop = _ => OcclusionCulling.Clear(),
            Probe = () => (OcclusionCulling.Installed, OcclusionCulling.Detected && !OcclusionCulling.Supported) switch
            {
                (false, _) => EngineChanged,
                (_, true) => NotApplicable,
                _ => Active
            }
        },
        // before VulkanTerrain, whose GlTap then wraps its entry points
        new("GlQueryCache")
        {
            Install = _ => GlQueryCache.Install(),
            Knobs = [Switch("GlQueryCache", "render", "drawing", () => GlQueryCache.Enabled,
                on => GlQueryCache.Enabled = on)],
            Probe = () => GlQueryCache.Installed ? Active : NotApplicable
        },
        // terrain rebuilt in-world (tools page, and switches that change what new pools hold)
        new("TerrainRebuild")
        {
            Install = c => TerrainRebuild.Install(c.Api),
            Stop = _ => TerrainRebuild.Stop()
        },
        // chunk pools empty for 10 s go back to the driver (run by TerrainRebuild's hook)
        new("PoolTrim")
        {
            Requires = ["TerrainRebuild"],
            Install = c => PoolTrim.Install(c.Harmony),
            // own name: "PoolTrim" was a switch, whose 1 now means the mildest level
            // little video memory: the chunk pools drained harder
            Knobs = [new("PoolTrim", "render", "drawing", 0, PoolTrim.Max, 0, () => PoolTrim.Level, v => PoolTrim.Level = v,
                "PoolTrimLevel")
            {
                Weighed = true, Suggest = hw => hw.VramGb switch { > 0 and <= 6 => 4, > 6 and <= 10 => 3, _ => null }
            }]
        },
        new("SunOcclusion")
        {
            Install = c => SunOcclusion.Install(c.Harmony),
            Knobs = [Switch("SunOcclusion", "render", "culling", () => SunOcclusion.Enabled,
                on => SunOcclusion.Enabled = on)]
        },
        new("AnimatableCulling")
        {
            Install = c => AnimatableCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("AnimatableCulling", "render", "culling", () => AnimatableCulling.Enabled,
                on => AnimatableCulling.Enabled = on)],
            Recheck = AnimatableCulling.Recheck,
            Probe = () => Found(AnimatableCulling.Matched, AnimatableCulling.Blocked),
            Shapes = [new EngineBodies(AnimatableCulling.Shaped, AnimatableCulling.Fingerprint)]
        },
        new("PotCulling")
        {
            Install = c => PotCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("PotCulling", "render", "culling", () => PotCulling.Enabled, on => PotCulling.Enabled = on)],
            Recheck = PotCulling.Recheck,
            Probe = () => PotSeam.Missing
                ? NotApplicable
                : Found(PotCulling.Matched, PotCulling.Blocked),
            Shapes = [new EngineBodies(PotCulling.Seams, PotCulling.Fingerprint)]
        },
        new("MapTileCulling")
        {
            Install = c => MapTileCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("MapTileCulling", "render", "culling", () => MapTileCulling.Enabled,
                on => MapTileCulling.Enabled = on)],
            Recheck = MapTileCulling.Recheck,
            Probe = () => MapSeam.Missing
                ? NotApplicable
                : Found(MapTileCulling.Matched, MapTileCulling.Blocked),
            Shapes = [new EngineBodies(MapTileCulling.Seams, MapTileCulling.Fingerprint)]
        },
        new("MapTileUpload")
        {
            Install = c => MapTileUpload.Install(c.Harmony, c.Logger),
            Knobs = [Switch("MapTileUpload", "render", "upload", () => MapTileUpload.Enabled,
                on => MapTileUpload.Enabled = on)],
            Recheck = MapTileUpload.Recheck,
            Probe = () => MapUploadSeam.Missing
                ? NotApplicable
                : Found(MapTileUpload.Matched, MapTileUpload.Blocked),
            Shapes = [new EngineBodies(MapTileUpload.Seams, MapTileUpload.Fingerprint)]
        },
        new("IdleAnimators")
        {
            Install = c => IdleAnimators.Install(c.Harmony, c.Logger),
            Knobs = [Switch("IdleAnimators", "render", "culling", () => IdleAnimators.Enabled,
                on => IdleAnimators.Enabled = on)],
            Recheck = IdleAnimators.Recheck,
            Stop = _ => IdleAnimators.Clear(),
            Probe = () => Found(IdleAnimators.Matched && IdleAnimators.Rewritten, IdleAnimators.Blocked),
            Shapes = [new EngineBodies(IdleAnimators.Seams, IdleAnimators.Fingerprint)]
        },
        new("WindowSizeCache")
        {
            Install = c => WindowSizeCache.Install(c.Harmony),
            Knobs = [Switch("WindowSizeCache", "render", "drawing", () => WindowSizeCache.Enabled,
                on => WindowSizeCache.Enabled = on)]
        },
        new("PoolScale")
        {
            Install = c => PoolScale.Install(c.Harmony),
            Knobs = [new("PoolScale", "render", "drawing", PoolScale.Engine, PoolScale.MaxScale, PoolScale.Engine,
                () => PoolScale.Scale, v => PoolScale.Scale = v) { Unit = "x", Weighed = true }],
            Probe = () => PoolScale.Rewritten ? Active : EngineChanged
        },
        new("GlErrorPoll")
        {
            Install = c => GlErrorPoll.Install(c.Harmony),
            Knobs = [Switch("GlErrorPoll", "render", "drawing", () => GlErrorPoll.Enabled,
                on => GlErrorPoll.Enabled = on)],
            Probe = () => GlErrorPoll.Rewritten ? Active : EngineChanged
        },
        new("MeshPool")
        {
            Install = c => MeshPool.Install(c.Harmony),
            Knobs = [Switch("MeshPool", "render", "upload", () => MeshPool.Enabled, on => MeshPool.Enabled = on,
                "PoolFragments")]
        },
        new("MeshRecycle")
        {
            Install = c => MeshRecycle.Install(c.Harmony),
            Knobs = [Switch("MeshRecycle", "render", "upload", () => MeshRecycle.Enabled,
                on => MeshRecycle.Enabled = on)]
        },
        new("FacePacking")
        {
            Install = c => FacePacking.Install(c.Harmony),
            Knobs = [Switch("FacePacking", "chunks", "arrival", () => FacePacking.Enabled,
                on => FacePacking.Enabled = on)]
        },
        new("FaceSorting")
        {
            Requires = ["FacePacking"],
            Install = c => FaceSorting.Install(c.Harmony),
            Knobs = [Switch("FaceSorting", "render", "culling", () => FaceSorting.Enabled,
                on => FaceSorting.Enabled = on)],
            Stop = _ => FaceSorting.Clear()
        },
        new("CullerRays")
        {
            Install = c => CullerRays.Install(c.Harmony, c.Logger),
            Knobs = [Switch("CullerRays", "render", "culling", () => CullerRays.Enabled, on => CullerRays.Enabled = on)],
            Recheck = CullerRays.Recheck,
            Probe = () => Found(CullerRays.Matched, CullerRays.Blocked),
            Shapes = [new EngineBodies(CullerRays.Seams, CullerRays.Fingerprint)]
        },
        new("FramePacer")
        {
            Install = c => FramePacer.Install(c.Harmony),
            Knobs = [Switch("FramePacer", "render", "drawing", () => FramePacer.Enabled, on => FramePacer.Enabled = on)
                with { Weighed = true }],
            Probe = () => Found(FramePacer.Patched, false)
        }
    ];
}
