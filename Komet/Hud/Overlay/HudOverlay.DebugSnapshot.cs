using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Komet.Api;
using Komet.Diagnostics;
using Komet.Options;
using Vintagestory.Client;

namespace Komet.Hud;

// Main-thread half of a protocol, copied into a DebugInput when the capture ends
internal sealed partial class HudOverlay
{
    private const int MaxEntityKinds = 4096;

    private DebugInput DebugSnapshot(DebugFrames debug)
    {
        var frames = debug.Since(_debugMode == "spike" ? debug.Now - SpikeWindow - PostRoll : 0);
        var features = KometFeatures.Snapshot().ToArray();
        var (entities, total) = Entities();
        _ = Assert(frames.Length <= DebugFrames.Capacity) && NotNull(features);
        return new DebugInput
            {
                Code = _settings.DebugLanguage, Mode = DebugModeText(), Trigger = _debugTrigger, Started = _debugStarted,
                Komet = _build.Version + " " + Edition(_build.Version) + (_build.Commit.Length > 0 ? " " + _build.Commit : ""), Frames = frames,
                Spikes = debug.Spikes(), Causes = Causes(), ModTimes = DebugModTimes(), Scopes = KometDebug.TakeTimings(),
                Extra = [.. _profiled], HudMeans = Means(), System = SystemRows(), Runtime = RuntimeRows(), World = WorldRows(),
                Render = RenderRows(), Client = ClientRows(), Knobs = KnobRows(), Features = features, Mods = ModRows(),
                Entities = entities, EntityTotal = total,
                VramPercent = DebugProtocol.Share(_gpu.VramUsedMb, _gpu.VramTotalMb),
                RamFreePercent = DebugProtocol.Share(_resources.AvailableRamMb, _resources.TotalRamMb),
                TessBacklog = RuntimeStats.chunksAwaitingTesselation,
                Scene =
                [
                    ("s-vd", Vintagestory.Client.NoObf.ClientSettings.ViewDistance), ("s-pixels", (double)_capi.Render.FrameWidth * _capi.Render.FrameHeight),
                    ("s-renderer", VulkanMode.RendererCode()), ("s-tris", _frames.RenderedTriangles)
                ]
            };
    }

    private string DebugModeText() => Assert(_debugMode.Length > 0) && Assert(_debugLeft <= MaxSeconds || _debugMode == "spike")
        ? _debugMode switch
        {
            "spike" => $"spike >= {DebugText.Num(_debugSpikeMs, "F0")} ms",
            "now" => "now",
            _ => "record"
        }
        : "";

    private (string Name, int Count, double MaxMs, double AverageMs)[] Causes()
    {
        var causes = new List<(string, int, double, double)>(SpikeLedger.Shown);
        for (var i = 0; i < SpikeLedger.Shown; i++)
            if (_spikes.Ranked(i) is { Count: > 0 } cause) causes.Add(cause);
        return Assert(causes.Count <= SpikeLedger.Shown) && NotNull(causes) ? [.. causes] : [];
    }

    private (string Mod, double Ms, (string Name, double Ms)[] Top)[] DebugModTimes() =>
        Assert(_debugMods.Count <= HarmonyAudit.MaxLoadedMods) && NotNull(_debugModTop)
            ? [.. _debugMods.Select(m => (m.Key, m.Value.Sum / Math.Max(1, m.Value.Intervals), _debugModTop.GetValueOrDefault(m.Key, [])))
                .OrderByDescending(static m => m.Item2)]
            : [];

    private (string, string)[] SystemRows()
    {
        var process = Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture);
        return Assert(process.Length > 0) && NotNull(_capi.Render)
            ?
            [
                ("hud-dbg-os", RuntimeInformation.OSDescription),
                ("hud-dbg-runtime", $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}"),
                ("hud-dbg-cpu", $"{process} logical, {DebugText.Num(_resources.CpuPercent, "F0")} % " +
                                $"(system {DebugText.Num(_resources.SystemCpuPercent, "F0")} %)"),
                ("hud-dbg-ram", $"{_resources.TotalRamMb} MB, {_resources.AvailableRamMb} MB free"),
                ("hud-dbg-process", $"{_resources.WorkingSetMb} MB, managed {_resources.ManagedUsedMb}/{_resources.ManagedCommittedMb} MB, " +
                                    $"native {_resources.NativeMb} MB"),
                ("hud-dbg-gpu", (ScreenManager.Platform?.GetGraphicCardInfos() ?? "").Replace('\n', ' ').Trim()),
                ("hud-dbg-window", $"{_capi.Render.FrameWidth} x {_capi.Render.FrameHeight}"),
                ("hud-dbg-build", typeof(HudOverlay).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "")
            ]
            : [];
    }

    private static (string, string)[] RuntimeRows()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var gc = GcConfig(DebugText.Key);
        using var process = Process.GetCurrentProcess();
        return Assert(minWorkers > 0) && NotNull(process)
            ?
            [
                ("hud-dbg-gc-config", $"{gc.Mode}; {gc.Budget}"),
                ("hud-dbg-jit-methods", $"{System.Runtime.JitInfo.GetCompiledMethodCount()}, " +
                                        $"{DebugText.Num(System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds, "F0")} ms"),
                ("hud-dbg-threadpool", $"{ThreadPool.ThreadCount} (min {minWorkers}), {ThreadPool.PendingWorkItemCount} pending, " +
                                       $"{ThreadPool.CompletedWorkItemCount} done"),
                ("hud-dbg-locks", DebugText.Int(Monitor.LockContentionCount)),
                ("hud-dbg-timers", DebugText.Int(Timer.ActiveCount)),
                ("hud-dbg-process-threads", DebugText.Int(process.Threads.Count)),
                ("hud-dbg-driver-threads", DriverThreads())
            ]
            : [];
    }

    private static string DriverThreads()
    {
        if (!Assert(RenderCost.Kinds > 0) || !RenderCost.DriverThreads) return "";
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < RenderCost.Kinds; i++)
            if (RenderCost.Seen(i))
                _ = text.Append(RenderCost.KindKey(i)).Append(' ').Append(DebugText.Num(RenderCost.ThreadNs(i) / 1e6, "F1")).Append(" ms  ");
        return Assert(text.Length < 1024) ? text.ToString().TrimEnd() : "";
    }
}
