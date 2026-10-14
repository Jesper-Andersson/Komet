using System.Runtime.Intrinsics;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Tessellation;

// Chunk tessellation: its safety and seams, Komet's own tessellator, the schedule and the worker threads.
// In install order; Core's Features places them among the other modules'.
internal static class TessellationFeatures
{
    internal static readonly Feature[] All =
    [
        // before TessSchedule and TessWorkers, which run passes on the worker pool
        new("TessSafety")
        {
            Install = c => TessSafety.Install(c.Harmony, c.Logger),
            Unpatched = TessSafety.Clear, // after the unpatch: the engine's palette table is the one read again
            Probe = () => (TessSafety.Broken, TessSafety.Installed) switch
            {
                (true, _) => Failed,
                (_, false) => EngineChanged,
                _ => Active
            }
        },
        new("TessSeams") { Install = c => TessSeams.Install(c.Harmony, c.Logger) },
        new("ExtendedRows")
        {
            Install = c => ExtendedRows.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ExtendedRows", "chunks", "tessellation", () => ExtendedRows.Enabled,
                on => ExtendedRows.Enabled = on)],
            Shapes = [new EngineBodies(ExtendedRows.Shaped, ExtendedRows.Shape)]
        },
        new("VisibleFaces")
        {
            Install = c => VisibleFaces.Install(c.Harmony, c.Logger),
            Knobs = [Switch("VisibleFaces", "chunks", "tessellation", () => VisibleFaces.Enabled,
                on => VisibleFaces.Enabled = on)],
            Shapes = [new EngineBodies(VisibleFaces.Shaped, VisibleFaces.Shape)]
        },
        new("FaceLight")
        {
            Install = c => FaceLight.Install(c.Harmony, c.Logger),
            Knobs = [Switch("FaceLight", "chunks", "tessellation", () => FaceLight.Enabled,
                on => FaceLight.Enabled = on)],
            Probe = () => Vector128.IsHardwareAccelerated ? Active : NotApplicable,
            Shapes =
            [
                new EngineBodies(FaceLight.Shaped, FaceLight.Shape),
                new EngineBodies(FaceLight.FusedShaped, FaceLight.FusedShape)
            ]
        },
        new("OwnTessellation")
        {
            Install = c => OwnTessellation.Install(c.Harmony, c.Logger, c.Api.World as ClientMain),
            Knobs = [Switch("OwnTessellation", "chunks", "tessellation", () => OwnTessellation.Enabled,
                on => OwnTessellation.Enabled = on)],
            Stop = _ => OwnTessellation.Installed = false,
            Probe = () => Found(OwnTessellation.Installed && OwnTessellation.JsonInstalled,
                OwnTessellation.StoodDown || OwnTessellation.JsonStoodDown),
            Shapes =
            [
                new EngineBodies(OwnTessellation.Shaped, OwnTessellation.Shape),
                new EngineBodies(OwnTessellation.JsonShaped, OwnTessellation.JsonShape)
            ]
        },
        new("TessBlockPos")
        {
            Install = c => TessBlockPos.Install(c.Harmony),
            Knobs = [Switch("TessBlockPos", "misc", "garbage", () => TessBlockPos.Enabled,
                on => TessBlockPos.Enabled = on)],
            Probe = () => TessBlockPos.Rewritten ? Active : EngineChanged
        },
        new("PartRecycling")
        {
            Install = c => PartRecycling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("PartRecycling", "chunks", "tessellation", () => PartRecycling.Enabled,
                on => PartRecycling.Enabled = on)],
            Recheck = PartRecycling.Recheck,
            Probe = () => Found(PartRecycling.Rewritten, PartRecycling.StoodDown),
            Shapes = [new EngineBodies(PartRecycling.Shaped, PartRecycling.Shape)]
        },
        new("TessAccounting") { Install = c => TessAccounting.Install(c.Harmony), Stop = _ => TessAccounting.Clear() },
        new("TessSchedule")
        {
            Install = c => TessSchedule.Install(c.Harmony),
            Requires = ["TessSafety"],
            Knobs = [Switch("TessSchedule", "chunks", "tessellation", () => TessSchedule.Enabled,
                on => TessSchedule.Enabled = on, "NearFirst")],
            Stop = _ => TessSchedule.Clear()
        },
        // only with TessSafety and TessSchedule installed completely
        new("TessWorkers")
        {
            Install = c => TessWorkers.Install(c.Harmony),
            Requires = ["TessSafety", "TessSchedule", "WorkerPool"],
            Knobs = [new("TessPriority", "chunks", "threads", 0, 100, 0, () => TessWorkers.Priority,
                v => TessWorkers.Priority = v)
                    { Unit = "%", Weighed = true, Presets = new(Quality: 100, Balanced: 25, Speed: 10) }],
            Stop = _ => TessWorkers.Stop()
        },
        new("JitWarm")
        {
            Install = c => JitWarm.Install(c.Harmony, c.Logger),
            Knobs = [Switch("JitWarm", "chunks", "tessellation", () => JitWarm.Enabled, on => JitWarm.Enabled = on)]
        },
        new("OccludedChunks")
        {
            Install = c => OccludedChunks.Install(c.Harmony, c.Logger),
            Knobs = [Switch("OccludedChunks", "chunks", "tessellation", () => OccludedChunks.Enabled,
                on => OccludedChunks.Enabled = on)],
            Shapes = [new EngineBodies(OccludedChunks.Shaped, OccludedChunks.Shape)]
        },
        // chunks deep under the surface, far from the player, kept without geometry
        new("DeepCulling")
        {
            Requires = ["OccludedChunks"],
            Install = c => DeepCulling.Install(c.Harmony, c.Api),
            Knobs = [Switch("DeepCulling", "render", "culling", () => DeepCulling.Enabled, on => DeepCulling.Enabled = on)
                with { Weighed = true }],
            Stop = _ => DeepCulling.Stop(),
            Probe = () => DeepCulling.Installed ? Active : EngineChanged
        },
        new("FarFoliage")
        {
            Install = c => FarFoliage.Install(c.Harmony, c.Api, c.Logger),
            // little video memory: distant foliage simplified from 2 px
            Knobs = [new("FarFoliage", "render", "drawing", 0, FarFoliage.MaxPixels, FarFoliage.Engine, () => FarFoliage.Pixels,
                v => FarFoliage.Pixels = v)
            {
                Unit = "px", Weighed = true, Presets = new(Quality: 0, Balanced: 1, Speed: 2),
                Suggest = hw => hw.VramGb is > 0 and <= 4 ? 2 : null
            }],
            Stop = _ => FarFoliage.Stop(),
            Probe = () => FarFoliage.Installed ? Active : EngineChanged
        }
    ];
}
