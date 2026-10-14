using System.Runtime;
using Komet.Diagnostics;

namespace Komet.Hud;

internal static partial class DebugProtocol
{
    private const int ShownAllocFrames = 5, ShownLog = 60;
    private static readonly string[] GenNames = ["gen0", "gen1", "gen2", "loh", "poh"];

    private static void Memory(DebugText text, DebugInput input, DebugSummary s)
    {
        if (!NotNull(input.Frames) || !Assert(s.Frames >= 0)) return;
        Section(text, "hud-dbg-s-memory");
        var info = GC.GetGCMemoryInfo();
        _ = text.Row("hud-dbg-alloc", $"{N(s.AllocMbPerSec, "F1")} MB/s   {text.T("hud-dbg-main-thread")} {N(s.MainAllocMbPerSec, "F1")} MB/s")
            .Row("hud-dbg-collections", $"gen0 {s.Gen0}   gen1 {s.Gen1}   gen2 {s.Gen2}")
            .Row("hud-dbg-gc-pause", $"{N(s.GcMs, "F1")} ms  ({N(Share(s.GcMs, s.Seconds * 1000), "F2")} %)   max {N(s.GcMaxMs, "F1")} ms")
            .Row("hud-dbg-heap", $"{Mb(info.HeapSizeBytes)} MB   {text.T("hud-dbg-committed")} {Mb(info.TotalCommittedBytes)} MB" +
                                 $"   {text.T("hud-dbg-fragmented")} {Mb(info.FragmentedBytes)} MB")
            .Row("hud-dbg-generations", Generations(info))
            .Row("hud-dbg-gc-mode", $"{(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}, " +
                                    $"concurrent {AppContext.GetData("System.GC.Concurrent")?.ToString() ?? "default"}")
            .Row("hud-dbg-gc-last", $"#{info.Index} gen{info.Generation} {(info.Compacted ? "compacting" : "")}" +
                                    $"{(info.Concurrent ? " background" : "")}  promoted {Mb(info.PromotedBytes)} MB  " +
                                    $"pause {N(info.PauseDurations[0].TotalMilliseconds, "F1")} ms");
        var dearest = input.Frames.OrderByDescending(static f => f.AllocKb).Take(ShownAllocFrames).ToArray();
        _ = text.Line(text.T("hud-dbg-alloc-frames"));
        foreach (var frame in dearest.Bounded(ShownAllocFrames))
            _ = text.Line($"{N(frame.At, "F1"),7} s  {frame.AllocKb,8} KiB  ({text.T("hud-dbg-main-thread")} {frame.MainAllocKb} KiB)" +
                          $"  {N(frame.DtMs, "F1")} ms", 4);
    }

    private static string Mb(long bytes) => Assert(bytes >= 0) && Assert(bytes < long.MaxValue) ? N(bytes / 1048576.0, "F0") : "-";

    private static string Generations(GCMemoryInfo info)
    {
        var sizes = info.GenerationInfo;
        if (!Assert(sizes.Length >= 3) || !Assert(sizes.Length <= 8)) return "";
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Math.Min(sizes.Length, GenNames.Length); i++)
            _ = text.Append(GenNames[i]).Append(' ').Append(Mb(sizes[i].SizeAfterBytes)).Append("  ");
        return text.ToString().TrimEnd() + " MB";
    }

    private static void World(DebugText text, DebugInput input)
    {
        if (!NotNull(input.World) || !NotNull(input.Entities)) return;
        Rows(text, "hud-dbg-s-world", input.World);
        _ = text.Row("hud-dbg-entities", DebugText.Int(input.EntityTotal));
        foreach (var (code, count) in First(input.Entities, ShownEntities).Bounded(ShownEntities)) _ = text.Pair(code, DebugText.Int(count), 4);
    }

    private static void Mods(DebugText text, DebugInput input)
    {
        if (!NotNull(input.Mods) || !NotNull(input.ModTimes)) return;
        Section(text, "hud-dbg-s-mods");
        _ = text.Line(text.T("hud-dbg-mod-times"));
        foreach (var (mod, ms, top) in input.ModTimes.Bounded(MaxRows))
        {
            _ = text.Pair(mod, $"{N(ms, "F3")} ms/frame", 4);
            foreach (var (name, detail) in top.Bounded(ModTimes.DetailCount)) _ = text.Pair(name, $"{N(detail, "F3")} ms", 8);
        }

        if (input.Scopes.Length > 0) _ = text.Blank().Line(text.T("hud-dbg-scopes"));
        foreach (var (name, ms, calls) in input.Scopes.Bounded(MaxRows))
            _ = text.Pair(name, $"{N(ms, "F2")} ms   {calls} x   {N(calls > 0 ? ms / calls * 1000 : double.NaN, "F1")} us/call", 4);
        var code = input.Mods.Count(static m => m.Kind == "Code");
        _ = text.Blank().Row("hud-dbg-mods-loaded", $"{input.Mods.Length}  ({code} code, {input.Mods.Length - code} content)");
        foreach (var (id, name, version, kind, source) in input.Mods.Bounded(HarmonyAudit.MaxLoadedMods))
            if (kind == "Code") _ = text.Pair(id, $"{version,-12} {name}  [{source}]", 4);
        var content = string.Join(", ", input.Mods.Where(static m => m.Kind != "Code").Select(static m => m.Id + " " + m.Version));
        if (content.Length > 0) _ = text.Line(text.T("hud-dbg-content-mods") + " " + content, 4);
    }

    private static void Harmony(DebugText text, HarmonyAudit audit)
    {
        if (!NotNull(audit) || !Assert(audit.Methods.Length <= HarmonyAudit.MaxMethods)) return;
        Section(text, "hud-dbg-s-harmony");
        _ = text.Row("hud-dbg-patched", $"{audit.Methods.Length}   {text.T("hud-dbg-owners")} {audit.Owners.Length}" +
                                        $"   {text.T("hud-dbg-shared")} {audit.Shared}");
        foreach (var (owner, mod, methods) in First(audit.Owners, ShownMods).Bounded(ShownMods))
            _ = text.Pair(owner, $"{methods,5}  {mod}", 4);
        if (Array.Exists(audit.Methods, static m => m.Owners >= 2)) _ = text.Blank().Line(text.T("hud-dbg-shared-methods"));
        var shown = 0;
        foreach (var method in audit.Methods.Bounded(HarmonyAudit.MaxMethods))
        {
            if (method.Owners < 2 || ++shown > ShownMethods) continue;
            _ = text.Line($"{text.T("hud-dbg-risk-" + method.Risk.ToString().ToLowerInvariant()),-7} {method.Name}", 4);
            foreach (var link in method.Chain.Bounded(HarmonyAudit.MaxPatches))
                _ = text.Line($"{link.Kind,-10} {link.Owner,-24} prio {link.Priority,4}{(link.Skips ? "  " + text.T("hud-dbg-can-skip") : "")}" +
                              $"  {link.Method}", 12);
            if (method.Risk == PatchRisk.High) _ = text.Line(text.T(Effect(method)), 12);
        }

        if (shown > ShownMethods) _ = text.Line(text.T("hud-dbg-more-in-file", shown - ShownMethods, "harmony.txt"), 4);
    }

    internal static string Effect(PatchedMethod method) =>
        NotNull(method.Chain) && Assert(method.Risk == PatchRisk.High) &&
        method.Chain.Where(static l => l.Kind == PatchKind.Transpiler).Select(static l => l.Owner).Distinct().Count() > 1
            ? "hud-dbg-effect-transpilers"
            : "hud-dbg-effect-skip";

    private static void Log(DebugText text, DebugExtra extra)
    {
        if (!NotNull(extra.Log) || !Assert(extra.LogErrors >= 0)) return;
        Section(text, "hud-dbg-s-log");
        _ = text.Row("hud-dbg-log-counts", $"{extra.LogErrors} / {extra.LogWarnings}");
        var from = Math.Max(0, extra.Log.Length - ShownLog);
        foreach (var line in extra.Log.AsSpan(from).Bounded(ShownLog)) _ = text.Line(line.Length > DebugText.MaxWidth * 2
            ? line[..(DebugText.MaxWidth * 2)] + " ..." : line, 4);
    }
}
