using Komet.Core;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Shapes;

// Shapes and entities: animation frames, shape init, bush and clutter meshes, inventory icons.
// In install order; Core's Features places them among the other modules'.
internal static class ShapeFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget BushBuilder = new(BushShapes.Builder), ClutterTarget = new(ClutterMeshes.Target),
        CreatureMeshes = new(static () => InventoryShapes.Method("Vintagestory.GameContent.ItemCreature", "CreateOverlaidMeshRef"));

    internal static readonly Feature[] All =
    [
        new("AnimationFrames")
        {
            Install = c => AnimationFrames.Install(c.Harmony, c.Logger),
            Knobs = [Switch("AnimationFrames", "misc", "entities", () => AnimationFrames.Enabled,
                on => AnimationFrames.Enabled = on)],
            Recheck = AnimationFrames.Recheck,
            Stop = _ => AnimationFrames.Clear(),
            Probe = () => Found(AnimationFrames.Matched, AnimationFrames.Blocked),
            Shapes = [new EngineBodies(AnimationFrames.Shaped, AnimationFrames.Fingerprint)]
        },
        new("InitOnce")
        {
            Install = c => InitOnce.Install(c.Harmony, c.Logger),
            Knobs = [Switch("InitOnce", "misc", "entities", () => InitOnce.Enabled, on => InitOnce.Enabled = on)],
            Recheck = InitOnce.Recheck,
            Probe = () => Found(InitOnce.Matched, InitOnce.Blocked),
            Shapes = [new EngineBodies(InitOnce.Shaped, InitOnce.Fingerprint)]
        },
        new("ShapeInitMemo")
        {
            Install = c => ShapeInitMemo.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ShapeInitMemo", "misc", "entities", () => ShapeInitMemo.Enabled,
                on => ShapeInitMemo.Enabled = on)],
            Recheck = ShapeInitMemo.Recheck,
            Probe = () => Found(ShapeInitMemo.Matched, ShapeInitMemo.Blocked),
            Shapes = [new EngineBodies(ShapeInitMemo.Shaped, ShapeInitMemo.Fingerprint)]
        },
        new("EntityTessBudget")
        {
            Install = c => EntityTessBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("EntityTessBudget", "misc", "entities", EntityTessBudget.Engine, EntityTessBudget.MaxMillis,
                EntityTessBudget.Engine, () => EntityTessBudget.Millis, v => EntityTessBudget.Millis = v)
                { Unit = "ms", Weighed = true, Presets = new(Quality: 6, Balanced: 4, Speed: 2) }],
            Probe = () => EntityTessBudget.Substituted ? Active : EngineChanged
        },
        new("BushShapes")
        {
            Install = c => BushShapes.Install(c.Harmony),
            Knobs = [Switch("BushShapes", "chunks", "tessellation", () => BushShapes.Enabled,
                on => BushShapes.Enabled = on)],
            Stop = _ => BushShapes.Clear(),
            Probe = () => BushBuilder.Missing ? NotApplicable : Found(BushShapes.Rewritten, false)
        },
        new("ClutterMeshes")
        {
            Install = c => ClutterMeshes.Install(c.Harmony),
            Knobs = [Switch("ClutterMeshes", "misc", "garbage", () => ClutterMeshes.Enabled,
                on => ClutterMeshes.Enabled = on)],
            Probe = () => ClutterTarget.Missing ? NotApplicable : Found(ClutterMeshes.Patched, false)
        },
        new("InventoryShapes")
        {
            Install = c => InventoryShapes.Install(c.Harmony),
            Knobs = [Switch("InventoryShapes", "misc", "garbage", () => InventoryShapes.Enabled,
                on => InventoryShapes.Enabled = on)],
            Probe = () => CreatureMeshes.Missing
                ? NotApplicable
                : Found(InventoryShapes.Creatures && InventoryShapes.Wearables, false)
        },
        new("IconBudget")
        {
            Install = c => IconBudget.Install(c.Harmony),
            Knobs = [Switch("IconBudget", "misc", "garbage", () => IconBudget.Enabled, on => IconBudget.Enabled = on)
                with { Weighed = true }],
            Probe = () => Found(IconBudget.Patched, false)
        }
    ];
}
