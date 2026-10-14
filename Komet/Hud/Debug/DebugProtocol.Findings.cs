using Komet.Api;
using Komet.Diagnostics;

namespace Komet.Hud;

// What stands out, worst first; coarse thresholds say where to look, the section how bad.
internal static partial class DebugProtocol
{
    private const double ModMsMedium = 2, ModMsHigh = 5, AllocMedium = 200, AllocHigh = 500;

    private static Finding[] Findings(DebugInput input, DebugExtra extra, DebugSummary s)
    {
        var list = new List<Finding>();
        Performance(list, s);
        Context(list, input, extra);
        if (extra.Previous is { } previous && Unlike(static (_, _) => "", previous, input, s).Length > 0)
            list.Add(new Finding(Severity.Info, "hud-dbg-s-compare", "hud-dbg-f-unlike", []));
        if (list.Count == 0) list.Add(new Finding(Severity.Info, "hud-dbg-s-capture", "hud-dbg-f-none", []));
        list.Sort(static (a, b) => a.Level.CompareTo(b.Level));
        return Assert(list.Count > 0) && NotNull(list) ? [.. list.Take(MaxFindings)] : [];
    }

    private static void Performance(List<Finding> list, DebugSummary s)
    {
        if (!NotNull(list) || !Assert(s.Frames >= 0) || s.Frames == 0) return;
        var gc = Share(s.GcMs, s.Seconds * 1000);
        if (gc >= 2)
            list.Add(new Finding(gc >= 5 ? Severity.High : Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-gc",
                [N(gc, "F1"), s.Gen0, N(s.GcMaxMs, "F1")]));
        if (s.Gen2 > 0) list.Add(new Finding(Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-gen2", [s.Gen2]));
        if (s.AllocMbPerSec >= AllocMedium)
            list.Add(new Finding(s.AllocMbPerSec >= AllocHigh ? Severity.High : Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-alloc",
                [N(s.AllocMbPerSec, "F0"), N(Share(s.MainAllocMbPerSec, s.AllocMbPerSec), "F0")]));
        if (s.JitMs >= 50)
            list.Add(new Finding(s.JitMs >= 500 ? Severity.Medium : Severity.Low, "hud-dbg-s-runtime", "hud-dbg-f-jit", [N(s.JitMs, "F0")]));
        if (s.WorstMs >= 100) list.Add(new Finding(Severity.High, "hud-dbg-s-spikes", "hud-dbg-f-worst", [N(s.WorstMs, "F0")]));
        if (Share(s.Over50, s.Steady) >= 1)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-frames", "hud-dbg-f-over50", [s.Over50, N(Share(s.Over50, s.Steady), "F1")]));
        if (s.Low1Fps < 30) list.Add(new Finding(Severity.Medium, "hud-dbg-s-frames", "hud-dbg-f-low", [N(s.Low1Fps, "F0")]));
        if (s.RunQueueMs >= 1)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-runtime", "hud-dbg-f-runqueue",
                [N(s.RunQueueMs, "F1"), N(s.RunQueueMaxMs, "F0")]));
    }

    private static void Context(List<Finding> list, DebugInput input, DebugExtra extra)
    {
        if (!NotNull(list) || !NotNull(input) || !NotNull(extra)) return;
        foreach (var (mod, ms, _) in input.ModTimes.Bounded(MaxRows))
            if (ms >= ModMsMedium)
                list.Add(new Finding(ms >= ModMsHigh ? Severity.High : Severity.Medium, "hud-dbg-s-mods", "hud-dbg-f-mod", [mod, N(ms, "F2")]));
        var risky = extra.Audit.Methods.Count(static m => m.Risk == PatchRisk.High);
        if (risky > 0) list.Add(new Finding(Severity.Medium, "hud-dbg-s-harmony", "hud-dbg-f-harmony", [risky]));
        if (extra.LogErrors > 0)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-log", "hud-dbg-f-log", [extra.LogErrors, extra.LogWarnings]));
        if (input.VramPercent >= 90) list.Add(new Finding(Severity.High, "hud-dbg-s-render", "hud-dbg-f-vram", [N(input.VramPercent, "F0")]));
        if (input.RamFreePercent < 10)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-system", "hud-dbg-f-ram", [N(input.RamFreePercent, "F0")]));
        if (input.TessBacklog > 500) list.Add(new Finding(Severity.Low, "hud-dbg-s-world", "hud-dbg-f-backlog", [input.TessBacklog]));
        foreach (var feature in input.Features.Bounded(MaxRows))
            if (feature.State is FeatureState.Failed or FeatureState.StoodDown or FeatureState.EngineChanged)
                list.Add(new Finding(Severity.Info, "hud-dbg-s-komet", "hud-dbg-f-feature", [feature.Id, feature.State]));
        _ = Assert(list.Count <= MaxRows * 4);
    }

    private static string N(double value, string format) => DebugText.Num(value, format);

    internal static (string Key, double Value)[] Numbers(DebugSummary s) =>
        Assert(s.Frames >= 0) && Assert(MaxRows > 0)
            ?
            [
                ("fps", s.Fps), ("low1", s.Low1Fps), ("low01", s.Low01Fps), ("avg", s.AvgMs), ("p99", s.P99Ms),
                ("worst", s.WorstMs), ("over50", Share(s.Over50, s.Steady)), ("gc", Share(s.GcMs, s.Seconds * 1000)),
                ("alloc", s.AllocMbPerSec), ("jit", s.JitMs), ("runqueue", s.RunQueueMs)
            ]
            : [];

    internal static (string Key, double Value)[] Scene(DebugInput input, DebugSummary s) =>
        NotNull(input) && Assert(input.Scene.Length <= MaxRows)
            ? [("s-mode", ModeCode(input.Mode)), ("s-seconds", s.Seconds), ("s-backlog", input.TessBacklog), .. input.Scene]
            : [];

    private static double ModeCode(string mode)
    {
        if (!NotNull(mode) || mode.StartsWith("spike", StringComparison.Ordinal)) return 2;
        return Assert(mode.Length < 64) && mode == "now" ? 1 : 0;
    }

    private static bool Differ(double before, double after) => Assert(double.IsFinite(before)) && Math.Abs(before - after) > 0.5;

    private const double ShortSeconds = 9.5, TrianglesRatio = 1.3, LoadingBacklog = 100;

    // Why the captures are not comparable; empty when they are. Older summaries are checked for what they hold.
    internal static string Unlike(System.Func<string, object[], string> t, Dictionary<string, double> previous, DebugInput input, DebugSummary s)
    {
        if (!NotNull(t) || !NotNull(previous) || !NotNull(input)) return "";
        var now = Scene(input, s).ToDictionary(static n => n.Key, static n => n.Value, StringComparer.Ordinal);
        var reasons = new List<string>();
        bool Both(string key, out double before, out double after)
        {
            after = now.GetValueOrDefault(key, double.NaN);
            _ = Assert(key.Length > 0);
            return previous.TryGetValue(key, out before) && double.IsFinite(before) && double.IsFinite(after);
        }

        string[] modes = ["record", "now", "spike"];
        if (Both("s-mode", out var m0, out var m1) && Differ(m0, m1) && (uint)m0 < modes.Length && (uint)m1 < modes.Length)
            reasons.Add(t("hud-dbg-unlike-mode", [modes[(int)m0], modes[(int)m1]]));
        if (Both("s-seconds", out var t0, out var t1) && Math.Min(t0, t1) < ShortSeconds)
            reasons.Add(t("hud-dbg-unlike-short", [N(t0, "F1"), N(t1, "F1")]));
        if (Both("s-backlog", out var b0, out var b1) && Math.Max(b0, b1) > LoadingBacklog)
            reasons.Add(t("hud-dbg-unlike-loading", [N(b0, "F0"), N(b1, "F0")]));
        if (Both("s-vd", out var v0, out var v1) && Differ(v0, v1)) reasons.Add(t("hud-dbg-unlike-vd", [N(v0, "F0"), N(v1, "F0")]));
        if (Both("s-pixels", out var p0, out var p1) && Math.Abs(p0 - p1) > 0.01 * Math.Max(p0, p1))
            reasons.Add(t("hud-dbg-unlike-window", [N(p0 / 1e6, "F1"), N(p1 / 1e6, "F1")]));
        if (Both("s-renderer", out var r0, out var r1) && Differ(r0, r1)) reasons.Add(t("hud-dbg-unlike-renderer", []));
        if (Both("s-tris", out var g0, out var g1) && Math.Min(g0, g1) > 0 && Math.Max(g0, g1) / Math.Min(g0, g1) >= TrianglesRatio)
            reasons.Add(t("hud-dbg-unlike-tris", [N(g0 / 1e6, "F0"), N(g1 / 1e6, "F0")]));
        return Assert(reasons.Count <= 8) && reasons.Count > 0 ? string.Join("; ", reasons) : "";
    }

    private static void Compare(DebugText text, DebugExtra extra, DebugInput input, DebugSummary s)
    {
        Section(text, "hud-dbg-s-compare");
        if (extra.Previous is not { } previous || !Assert(previous.Count <= MaxRows))
        {
            _ = text.Line(text.T("hud-dbg-compare-none"));
            return;
        }

        _ = text.Line(text.T("hud-dbg-compare-with", extra.PreviousStamp));
        if (Unlike(text.T, previous, input, s) is { Length: > 0 } unlike) _ = text.Line(text.T("hud-dbg-compare-unlike", unlike));
        foreach (var (key, now) in Numbers(s).Bounded(MaxRows))
        {
            if (!previous.TryGetValue(key, out var before) || !double.IsFinite(before) || !double.IsFinite(now)) continue;
            var change = before != 0 ? 100 * (now - before) / Math.Abs(before) : double.NaN;
            var shown = double.IsFinite(change) ? change.ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture) + " %" : "";
            _ = text.Row("hud-dbg-n-" + key, $"{N(before, "F2"),10} -> {N(now, "F2"),10}   {shown}");
        }
    }
}
