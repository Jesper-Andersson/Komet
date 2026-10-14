using System.Globalization;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Hud;

internal sealed partial class HudOverlay
{
    private (string, string)[] WorldRows()
    {
        var loaded = RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded;
        var session = _capi.IsSinglePlayer ? "singleplayer" : "multiplayer";
        return Assert(loaded >= 0 || RuntimeStats.chunksReceived >= 0) && NotNull(_capi.World)
            ?
            [
                ("hud-dbg-chunks", $"{loaded} loaded, {RuntimeStats.chunksReceived} received, {RuntimeStats.chunksUnloaded} unloaded"),
                ("hud-dbg-tess-queue", $"{RuntimeStats.chunksAwaitingTesselation} tessellation, {RuntimeStats.chunksAwaitingPooling} upload"),
                ("hud-dbg-tess-total", DebugText.Int(RuntimeStats.chunksTesselatedTotal)),
                ("hud-dbg-chunks-packed", Packed()),
                ("hud-dbg-view-distance", DebugText.Int(ClientSettings.ViewDistance)),
                ("hud-dbg-session", session),
                ("hud-dbg-world-time", $"{DebugText.Num(_capi.World.Calendar?.TotalDays ?? double.NaN, "F2")} days")
            ]
            : [];
    }

    private string Packed()
    {
        if (_capi.World is not ClientMain game) return "-";
        var (packed, total) = Komet.Rendering.TerrainRebuild.Census(game);
        if (total <= 0) return "-";
        return Assert(packed <= total) ? $"{packed} of {total} ({DebugText.Num(100.0 * packed / total, "F0")} %)" : "-";
    }

    private (string, string)[] RenderRows()
    {
        var calls = _frames.DrawCallsPerFrame;
        return Assert(calls >= 0) && NotNull(_gpu)
            ?
            [
                ("hud-dbg-renderer", DebugText.Key(VulkanMode.RendererKey())),
                ("hud-dbg-draw-calls", DebugText.Int(calls)),
                ("hud-dbg-triangles", $"{_frames.RenderedTriangles} rendered, {_frames.AvailableTriangles} allocated"),
                ("hud-dbg-gpu-time", $"{DebugText.Num(RenderCost.FrameGpuMs(_gpu.Ms), "F2")} ms"),
                ("hud-dbg-vram", $"{DebugText.Num(_gpu.VramUsedMb, "F0")} / {DebugText.Num(_gpu.VramTotalMb, "F0")} MB, " +
                                 $"{DebugText.Num(_gpu.EvictionsPerSec, "F1")} evictions/s"),
                ("hud-dbg-entities-rendered", DebugText.Int(RuntimeStats.renderedEntities)),
                ("hud-dbg-vram-clients", VramClients())
            ]
            : [];
    }

    private string VramClients()
    {
        var clients = Diagnostics.DrmMemory.Read();
        if (clients.Length == 0) return "-";
        var own = Diagnostics.DrmMemory.TotalVramMb(clients);
        var ledger = Komet.Vulkan.VulkanRenderer.Snapshot().MemoryMb;
        var rest = _gpu.VramUsedMb - own;
        var tail = double.IsFinite(rest) ? $"; rest of the device {DebugText.Num(Math.Max(0, rest), "F0")} MB" : "";
        return Assert(own >= 0) && NotNull(clients)
            ? $"{own} MB: {Diagnostics.DrmMemory.Text(clients, ledger)}; Komet's Vulkan ledger {ledger} MB" + tail
            : "-";
    }

    // Never the whole file, which holds the session key
    private static (string, string)[] ClientRows()
    {
        var rows = new (string Name, double Value)[]
        {
            ("viewDistance", ClientSettings.ViewDistance), ("maxFps", ClientSettings.MaxFPS), ("vsyncMode", ClientSettings.VsyncMode),
            ("gameWindowMode", ClientSettings.GameWindowMode), ("ssaa", ClientSettings.SSAA),
            ("shadowMapQuality", ClientSettings.ShadowMapQuality), ("ssaoQuality", ClientSettings.SSAOQuality),
            ("godRayQuality", ClientSettings.GodRayQuality), ("bloom", ClientSettings.Bloom ? 1 : 0),
            ("fieldOfView", ClientSettings.FieldOfView), ("particleLevel", ClientSettings.ParticleLevel),
            ("chunkVerticesUploadRateLimiter", ClientSettings.ChunkVerticesUploadRateLimiter),
            ("optimizeRamMode", ClientSettings.OptimizeRamMode), ("guiScale", ClientSettings.GUIScale)
        };
        return Assert(rows.Length > 0) && NotNull(rows)
            ? Array.ConvertAll(rows, static r => (r.Name, r.Value.ToString(CultureInfo.InvariantCulture)))
            : [];
    }

    private static (string, int, int)[] KnobRows()
    {
        var count = Math.Min(Knobs.Count, Knobs.MaxKnobs);
        var rows = new (string, int, int)[count];
        for (var i = 0; i < Math.Min(count, Knobs.MaxKnobs); i++) rows[i] = (Knobs.At(i).Key, Knobs.At(i).Get(), Knobs.At(i).Engine);
        return Assert(rows.Length <= Knobs.MaxKnobs) && NotNull(rows) ? rows : [];
    }

    private (string, string, string, string, string)[] ModRows()
    {
        var mods = new List<(string, string, string, string, string)>();
        foreach (var mod in _capi.ModLoader.Mods.Bounded(HarmonyAudit.MaxLoadedMods))
            if (mod.Info is { } info)
                mods.Add((info.ModID, info.Name, info.Version, info.Type.ToString(), mod.FileName ?? ""));
        mods.Sort(static (a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return Assert(mods.Count <= HarmonyAudit.MaxLoadedMods) && NotNull(mods) ? [.. mods] : [];
    }

    private ((string, int)[], int) Entities()
    {
        var byCode = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var entity in _capi.World.LoadedEntities.Values.Bounded(DebugFrames.Capacity))
        {
            var code = entity.Code?.ToShortString() ?? entity.GetType().Name;
            if (byCode.Count < MaxEntityKinds || byCode.ContainsKey(code)) byCode[code] = byCode.GetValueOrDefault(code) + 1;
            total++;
        }

        var ranked = byCode.OrderByDescending(static e => e.Value).Select(static e => (e.Key, e.Value)).ToArray();
        return Assert(total >= 0) && NotNull(ranked) ? (ranked, total) : ([], 0);
    }
}
