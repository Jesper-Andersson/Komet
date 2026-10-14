using Komet.Chunks;
using Komet.Diagnostics;
using Komet.Host;
using Komet.Hud;
using Komet.Options;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Komet.Vulkan;
using Komet.World;

namespace Komet.Core;

// Every feature, from the modules' lists, in install order; their knobs form the Knobs table (bench arms write via Knobs.Write,
// never KometSettings). Stops run in reverse: PreJit first, GraphicsMenu before the HUD window, TessWorkers before the pool.
internal static partial class Features
{
    private const int MaxModules = 16;

    // One list a module, each in its own install order
    private static readonly Feature[][] Modules =
    [
        DiagnosticsFeatures.All, HudFeatures.All, OptionsFeatures.All, RenderingFeatures.All, VulkanFeatures.All, ChunkFeatures.All,
        WorldFeatures.All, ShapeFeatures.All, TessellationFeatures.All, CoreFeatures.All, HostFeatures.All
    ];

    // The install order across the modules, which is also the knobs' order in komet.json and on the settings pages. A feature
    // installs after those it Requires; ModTimes and RenderCost before the HUD, whose settings switch them on; IndirectDraw
    // before OcclusionCulling, whose draws its RenderMesh prefix hands over; GlQueryCache before VulkanTerrain, whose GlTap wraps
    // its entry points; TessSafety before TessSchedule and TessWorkers; PreJit last.
    private static readonly string[] InstallOrder =
    [
        "ModTimes", "RenderCost", "Hud", "GraphicsMenu", "MenuBlur", "ShaderUseCache", "DistantShadows", "LeanVaryings",
        "SharpUpscale", "SsaoLean", "LeanGodRays", "FrustumSweep", "FarShadowReuse", "NearShadowReuse", "EntityCasters",
        "IndirectDraw", "OcclusionCulling", "GlQueryCache", "VulkanCore", "VulkanShaderCache", "VulkanGpuTimes",
        "VulkanCompactFaces", "TerrainRebuild", "PoolTrim", "VulkanTerrain", "SunOcclusion", "AnimatableCulling",
        "PotCulling", "HelpCompose", "MapTileCulling", "MapTileUpload", "IdleAnimators", "WindowSizeCache", "PoolScale",
        "GlErrorPoll", "ChunkLookup", "MeshPool", "MeshRecycle", "FacePacking", "FaceSorting", "ClimateCache",
        "PartitionReuse", "HandlerLists", "CookingMatch", "AnimationFrames", "InitOnce", "ShapeInitMemo",
        "EntityTessBudget", "EntityTimes", "ChunkBudget", "ChunkLoadBudget", "RecyclerTrim", "ChunkPacking",
        "BlockEntityBudget", "EventBusSweep", "ListenerSlots", "BlockEntityCaches", "ChunkThreadClosure",
        "CloudTileScratch", "DecompressScratch", "ProtoListCache", "BushShapes", "ClutterMeshes", "CullerRays", "MapReads",
        "Ascii85Text", "TreeKeys", "MapPixels", "MapSaveScratch", "LightScratch", "LightRepair", "ParticleLight",
        "ColumnNoiseScratch", "TessSafety", "TessSeams", "ExtendedRows", "VisibleFaces", "FaceLight", "OwnTessellation",
        "TessBlockPos", "PartRecycling", "TessAccounting", "TessSchedule", "WorkerPool", "TessWorkers", "JitWarm",
        "OccludedChunks", "DeepCulling", "FarFoliage", "GcLatency", "GcCompact", "ServerProcess", "InventoryShapes",
        "WorldGenScratch", "PatchLookup", "IconBudget", "SelectionScratch", "ProfilerPool", "WeatherLock", "FramePacer",
        "FrameClock", "HangWatch", "PreJit"
    ];

    private static readonly Feature[] Table = Ordered();

    // A feature missing from InstallOrder, or listed twice, throws: Komet does not start with a table it cannot order
    private static Feature[] Ordered()
    {
        var byId = new Dictionary<string, Feature>(MaxFeatures, StringComparer.Ordinal);
        foreach (var module in Modules.Bounded(MaxModules))
        {
            var last = -1;
            foreach (var feature in module.Bounded(MaxFeatures))
            {
                if (!byId.TryAdd(feature.Id, feature)) throw new InvalidOperationException("feature listed twice: " + feature.Id);
                var at = Array.IndexOf(InstallOrder, feature.Id);
                _ = Assert(at > last); // a module lists its features in install order
                last = at;
            }
        }

        if (byId.Count != InstallOrder.Length) throw new InvalidOperationException("features not in InstallOrder");
        var table = new Feature[InstallOrder.Length];
        for (var i = 0; i < InstallOrder.Length; i++) table[i] = byId[InstallOrder[i]];
        return table;
    }
}
