using Komet.Core;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.World;

// World-side garbage and lookups: events, block entities, the map, light, world generation, weather.
// In install order; Core's Features places them among the other modules'.
internal static class WorldFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget MapSaveSeam = new(static () => MapSaveScratch.Seams()[0]),
        MapReadsTarget = new(MapReads.Target), WeatherTarget = new(WeatherLock.Target);

    internal static readonly Feature[] All =
    [
        new("ClimateCache")
        {
            Install = c => ClimateCache.Install(c.Harmony),
            Knobs = [Switch("ClimateCache", "misc", "garbage", () => ClimateCache.Enabled,
                on => ClimateCache.Enabled = on)]
        },
        new("PartitionReuse")
        {
            Install = c => PartitionReuse.Install(c.Harmony),
            Knobs = [Switch("PartitionReuse", "misc", "garbage", () => PartitionReuse.Enabled,
                on => PartitionReuse.Enabled = on)],
            Probe = () => PartitionReuse.Rewritten ? Active : EngineChanged
        },
        new("HandlerLists")
        {
            Install = c => HandlerLists.Install(c.Harmony),
            Knobs = [Switch("HandlerLists", "misc", "garbage", () => HandlerLists.Enabled,
                on => HandlerLists.Enabled = on)],
            Probe = () => HandlerLists.Rewritten ? Active : EngineChanged,
            Stop = _ => HandlerLists.Clear()
        },
        new("CookingMatch")
        {
            Install = c => CookingMatch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("CookingMatch", "misc", "garbage", () => CookingMatch.Enabled,
                on => CookingMatch.Enabled = on)],
            Probe = () => CookingMatch.Matched && CookingMatch.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(CookingMatch.Seams, CookingMatch.Fingerprint)]
        },
        new("EventBusSweep")
        {
            Install = c => EventBusSweep.Install(c.Harmony, c.Logger),
            Knobs = [Switch("EventBusSweep", "misc", "garbage", () => EventBusSweep.Enabled,
                on => EventBusSweep.Enabled = on)],
            Stop = _ => EventBusSweep.Clear(),
            Probe = () => EventBusSweep.Installed ? Active : EngineChanged,
            Shapes = [new EngineBodies(EventBusSweep.Seams, EventBusSweep.Fingerprint)]
        },
        new("ListenerSlots")
        {
            Install = c => ListenerSlots.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ListenerSlots", "chunks", "blockentities", () => ListenerSlots.Enabled,
                on => ListenerSlots.Enabled = on)],
            Recheck = ListenerSlots.Recheck,
            Stop = _ => ListenerSlots.Clear(),
            Probe = () => Found(ListenerSlots.Matched, ListenerSlots.Blocked),
            Shapes =
            [
                new EngineBodies(ListenerSlots.TickSeams, ListenerSlots.TickFingerprint),
                new EngineBodies(ListenerSlots.RendererSeams, ListenerSlots.RendererFingerprint),
                new EngineBodies(ListenerSlots.BusSeams, ListenerSlots.BusFingerprint)
            ]
        },
        new("BlockEntityCaches")
        {
            Install = c => BlockEntityCaches.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("BlockEntityCaches", "chunks", "blockentities", () => BlockEntityCaches.Enabled,
                on => BlockEntityCaches.Enabled = on)],
            Recheck = BlockEntityCaches.Recheck,
            Stop = _ => BlockEntityCaches.Clear(),
            Probe = () => BlockEntityCaches.Survival
                ? Found(BlockEntityCaches.Matched, BlockEntityCaches.Blocked)
                : NotApplicable,
            Shapes =
            [
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Ignitable),
                    BlockEntityCaches.IgnitableFingerprint),
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Meshes), BlockEntityCaches.MeshFingerprint),
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Pots), BlockEntityCaches.PotFingerprint)
            ]
        },
        new("CloudTileScratch")
        {
            Install = c => CloudTileScratch.Install(c.Harmony),
            Knobs = [Switch("CloudTileScratch", null, null, () => CloudTileScratch.Enabled,
                on => CloudTileScratch.Enabled = on)]
        },
        new("ProtoListCache")
        {
            Install = c => ProtoListCache.Install(c.Harmony),
            Knobs = [Switch("ProtoListCache", "chunks", "arrival", () => ProtoListCache.Enabled,
                on => ProtoListCache.Enabled = on)],
            Probe = () => ProtoListCache.Patched ? Active : NotApplicable
        },
        new("MapReads")
        {
            Install = c => MapReads.Install(c.Harmony),
            Knobs = [Switch("MapReads", "chunks", "arrival", () => MapReads.Enabled, on => MapReads.Enabled = on)],
            Probe = () => MapReadsTarget.Missing ? NotApplicable : Found(MapReads.Rewritten, false)
        },
        new("Ascii85Text")
        {
            Install = c => Ascii85Text.Install(c.Harmony),
            Knobs = [Switch("Ascii85Text", "misc", "garbage", () => Ascii85Text.Enabled, on => Ascii85Text.Enabled = on)],
            Probe = () => Found(Ascii85Text.Patched, false)
        },
        new("TreeKeys")
        {
            Install = c => TreeKeys.Install(c.Harmony),
            Knobs = [Switch("TreeKeys", "chunks", "arrival", () => TreeKeys.Enabled, on => TreeKeys.Enabled = on)],
            Probe = () => TreeKeys.Rewritten || TreeKeys.Reads ? Active : EngineChanged
        },
        new("MapPixels")
        {
            Install = c => MapPixels.Install(c.Harmony),
            Knobs = [Switch("MapPixels", "chunks", "arrival", () => MapPixels.Enabled, on => MapPixels.Enabled = on)],
            Probe = () => MapPixels.Rewritten ? Active : EngineChanged
        },
        new("MapSaveScratch")
        {
            Install = c => MapSaveScratch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("MapSaveScratch", "chunks", "arrival", () => MapSaveScratch.Enabled,
                on => MapSaveScratch.Enabled = on)],
            Recheck = MapSaveScratch.Recheck,
            Probe = () => MapSaveSeam.Missing
                ? NotApplicable
                : Found(MapSaveScratch.Matched, MapSaveScratch.Blocked),
            Shapes = [new EngineBodies(MapSaveScratch.Seams, MapSaveScratch.Fingerprint)]
        },
        new("LightScratch")
        {
            Install = c => LightScratch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("LightScratch", "chunks", "light", () => LightScratch.Enabled,
                on => LightScratch.Enabled = on)],
            Probe = () => LightScratch.StoodDown ? StoodDown : Active
        },
        new("LightRepair")
        {
            Install = c => LightRepair.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch("LightRepair", "chunks", "light", () => LightRepair.Enabled,
                on => LightRepair.Enabled = on) with { Weighed = true }],
            Recheck = LightRepair.Recheck,
            Probe = () => Found(LightRepair.Matched, LightRepair.Failed, Failed),
            Shapes = [new EngineBodies(LightRepair.Seams, LightRepair.Fingerprint)]
        },
        new("ParticleLight")
        {
            Install = c => ParticleLight.Install(c.Harmony),
            Knobs = [Switch("ParticleLight", "chunks", "light", () => ParticleLight.Enabled,
                on => ParticleLight.Enabled = on)],
            Stop = _ => ParticleLight.Forget()
        },
        new("ColumnNoiseScratch")
        {
            Install = c => ColumnNoiseScratch.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch("ColumnNoiseScratch", "misc", "garbage", () => ColumnNoiseScratch.Enabled,
                on => ColumnNoiseScratch.Enabled = on)],
            Probe = () => ColumnNoiseScratch.StoodDown ? StoodDown : Active
        },
        new("WorldGenScratch")
        {
            Install = c => WorldGenScratch.Install(c.Harmony),
            ServerOnly = true,
            Knobs = [Switch("WorldGenScratch", "misc", "garbage", () => WorldGenScratch.Enabled, on => WorldGenScratch.Enabled = on)],
            Probe = () => WorldGenScratch.Rewritten == WorldGenScratch.AllBits ? Active : StoodDown
        },
        new("PatchLookup")
        {
            Install = c => PatchLookup.Install(c.Harmony),
            ServerOnly = true,
            Knobs = [Switch("PatchLookup", "misc", "garbage", () => PatchLookup.Enabled, on => PatchLookup.Enabled = on)],
            Probe = () => PatchLookup.Rewritten ? Active : StoodDown
        },
        new("SelectionScratch")
        {
            Install = c => SelectionScratch.Install(c.Harmony),
            Knobs = [Switch("SelectionScratch", "misc", "garbage", () => SelectionScratch.Enabled, on => SelectionScratch.Enabled = on)],
            Probe = () => Found(SelectionScratch.Rewritten, false)
        },
        new("WeatherLock")
        {
            Install = c => WeatherLock.Install(c.Harmony),
            Stop = _ => WeatherLock.Forget(),
            Probe = () => WeatherTarget.Missing ? NotApplicable : Found(WeatherLock.Patched, false)
        }
    ];
}
