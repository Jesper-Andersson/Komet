using Komet.Core;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Chunks;

// Chunk arrival: lookups, upload and load budgets, packing in RAM, block entity budgets.
// In install order; Core's Features places them among the other modules'.
internal static class ChunkFeatures
{
    internal static readonly Feature[] All =
    [
        new("ChunkLookup")
        {
            Install = c => ChunkLookup.Install(c.Harmony),
            Knobs = [Switch("ChunkLookup", "chunks", "arrival", () => ChunkLookup.Enabled,
                on => ChunkLookup.Enabled = on)],
            Stop = _ => ChunkLookup.Clear()
        },
        new("ChunkBudget")
        {
            Install = c => ChunkBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("UploadCap", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis,
                    ChunkBudget.Uncapped, () => ChunkBudget.CapMillis, v => ChunkBudget.CapMillis = v)
                    { Unit = "ms", Weighed = true, Presets = new(Quality: 5, Balanced: 3, Speed: 2) },
                Switch("UploadYield", "chunks", "arrival", () => ChunkBudget.Yield, on => ChunkBudget.Yield = on)
                    with { Weighed = true }],
            Probe = () => ChunkBudget.Capped ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkBudget.Seams, ChunkBudget.Fingerprint)]
        },
        new("ChunkLoadBudget")
        {
            Install = c => ChunkLoadBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("ChunkLoadBudget", "chunks", "arrival", ChunkLoadBudget.Engine, ChunkLoadBudget.MaxMillis,
                ChunkLoadBudget.Engine, () => ChunkLoadBudget.Millis, v => ChunkLoadBudget.Millis = v)
                { Unit = "ms", Weighed = true, Presets = new(Quality: 5, Balanced: 3, Speed: 2) }],
            Probe = () => ChunkLoadBudget.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkLoadBudget.Seams, ChunkLoadBudget.Fingerprint)]
        },
        // the engine's recycled meshes kept to a share of its caps and dropped sooner
        new("RecyclerTrim")
        {
            Install = c => RecyclerTrim.Install(c.Harmony),
            Knobs = [new("RecyclerTrim", "chunks", "arrival", 0, RecyclerTrim.Max, 0, () => RecyclerTrim.Level,
                v => RecyclerTrim.Level = v) { Weighed = true }],
            Probe = () => RecyclerTrim.Installed ? Active : EngineChanged
        },
        // client chunk data packed in RAM sooner and faster than SystemCompressChunks alone
        new("ChunkPacking")
        {
            Install = c => ChunkPacking.Install(c.Harmony),
            // little RAM: chunks packed sooner and harder (less memory, a little more CPU when flying)
            Knobs = [new("ChunkPacking", "chunks", "arrival", 0, ChunkPacking.Max, 0, () => ChunkPacking.Level,
                v => ChunkPacking.Level = v)
            {
                Weighed = true, Suggest = hw => hw.RamGb switch { > 0 and <= 12 => 3, > 12 and <= 20 => 2, _ => null }
            }],
            Stop = _ => ChunkPacking.Stop(),
            Probe = () => ChunkPacking.Installed ? Active : EngineChanged
        },
        new("BlockEntityBudget")
        {
            Install = c => BlockEntityBudget.Install(c.Harmony, c.Logger),
            Requires = ["ChunkLoadBudget"],
            Knobs = [Switch("BlockEntityBudget", "chunks", "blockentities", () => BlockEntityBudget.Enabled,
                on => BlockEntityBudget.Enabled = on) with { Weighed = true }],
            Recheck = BlockEntityBudget.Recheck,
            Stop = _ => BlockEntityBudget.Leave(),
            Probe = () => Found(BlockEntityBudget.Matched && BlockEntityBudget.Rewritten && ChunkLoadBudget.Rewritten,
                BlockEntityBudget.Blocked),
            Shapes = [new EngineBodies(BlockEntityBudget.Seams, BlockEntityBudget.Fingerprint)]
        },
        new("ChunkThreadClosure") { Install = c => ChunkThreadClosure.Install(c.Harmony), ServerOnly = true },
        new("DecompressScratch")
        {
            Install = c => DecompressScratch.Install(c.Harmony),
            Knobs = [Switch("DecompressScratch", "chunks", "arrival", () => DecompressScratch.Enabled,
                on => DecompressScratch.Enabled = on)]
        }
    ];
}
