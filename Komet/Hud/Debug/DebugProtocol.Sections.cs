using Komet.Api;
using Komet.Diagnostics;

namespace Komet.Hud;

internal static partial class DebugProtocol
{
    private const int ShownSpikes = 10, ShownMods = 40, ShownMethods = 40, ShownEntities = 12, HistogramWidth = 40;
    private static readonly double[] Buckets = [8, 12, 16.7, 20, 25, 33.3, 50, 100, double.PositiveInfinity];

    private static void Environment(DebugText text, DebugInput input)
    {
        if (!NotNull(input.System) || !NotNull(input.Client)) return;
        Rows(text, "hud-dbg-s-system", input.System);
        Section(text, "hud-dbg-s-game");
        foreach (var (name, value) in input.Client.Bounded(MaxRows)) _ = text.Pair(name, value);
    }

    private static void Komet(DebugText text, DebugInput input)
    {
        if (!NotNull(input.Knobs) || !NotNull(input.Features)) return;
        Section(text, "hud-dbg-s-komet");
        _ = text.Row("hud-dbg-knobs", $"{input.Knobs.Length}, {input.Knobs.Count(static k => k.Value != k.Engine)} " +
                                      text.T("hud-dbg-changed"));
        foreach (var (key, value, engine) in input.Knobs.Bounded(MaxRows))
            if (value != engine) _ = text.Pair(key, $"{value}  ({text.T("hud-dbg-engine")} {engine})", 4);
        var active = input.Features.Count(static f => f.State == FeatureState.Active);
        _ = text.Row("hud-dbg-features", $"{input.Features.Length}, {active} " + text.T("hud-dbg-active"));
        foreach (var feature in input.Features.Bounded(MaxRows))
            if (feature.State != FeatureState.Active)
                _ = text.Pair(feature.Id, feature.State + (feature.HeldBy is { } by ? $"  ({by}: {feature.Reason})" : ""), 4);
    }

    private static void Frames(DebugText text, DebugInput input, DebugSummary s)
    {
        if (!NotNull(input.Frames) || !Assert(s.Frames == input.Frames.Length)) return;
        Section(text, "hud-dbg-s-frames");
        _ = text.Row("hud-dbg-fps", $"{N(s.Fps, "F1")}   1 %: {N(s.Low1Fps, "F1")}   0.1 %: {N(s.Low01Fps, "F1")}")
            .Row("hud-dbg-frametime", $"{N(s.AvgMs, "F2")} ms  ± {N(s.StdevMs, "F2")}")
            .Row("hud-dbg-percentiles", $"50 %: {N(s.MedianMs, "F1")}   90 %: {N(s.P90Ms, "F1")}   99 %: {N(s.P99Ms, "F1")}" +
                                        $"   99.9 %: {N(s.P999Ms, "F1")}   max: {N(s.WorstMs, "F1")} ms")
            .Row("hud-dbg-over", $"16.7 ms: {s.Over17}   33 ms: {s.Over33}   50 ms: {s.Over50}   100 ms: {s.Over100}")
            .Row("hud-dbg-split", $"GC {N(s.GcMs / Math.Max(1, s.Frames), "F2")}  JIT {N(s.JitMs / Math.Max(1, s.Frames), "F2")}" +
                                  $"  {text.T("hud-dbg-runqueue")} {N(s.RunQueueMs, "F2")}  {text.T("hud-dbg-outside")} {N(s.OutsideMs, "F2")} ms")
            .Blank();
        Histogram(text, input.Frames);
    }

    private static void Histogram(DebugText text, DebugFrame[] frames)
    {
        Span<int> counts = stackalloc int[Buckets.Length];
        var steady = 0;
        foreach (var frame in frames.Bounded(DebugFrames.Capacity))
            if (frame.Steady && Assert(frame.DtMs >= 0))
            {
                var bucket = 0;
                for (var i = 0; i < Buckets.Length && frame.DtMs > Buckets[i]; i++) bucket = i + 1;
                counts[Math.Min(bucket, Buckets.Length - 1)]++;
                steady++;
            }

        if (steady == 0 || !Assert(counts.Length == Buckets.Length)) return;
        for (var i = 0; i < Buckets.Length; i++)
        {
            var bound = double.IsFinite(Buckets[i]) ? Buckets[i] : Buckets[i - 1];
            var label = (double.IsFinite(Buckets[i]) ? "<= " : " > ") +
                        bound.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).PadLeft(5) + " ms";
            var bar = new string('#', (int)Math.Ceiling((double)counts[i] * HistogramWidth / steady));
            _ = text.Line($"{label}  {bar,-HistogramWidth} {counts[i],7} {N(Share(counts[i], steady), "F1"),6} %", 4);
        }
    }

    private static void Spikes(DebugText text, DebugInput input)
    {
        if (!NotNull(input.Spikes) || !NotNull(input.Causes)) return;
        Section(text, "hud-dbg-s-spikes");
        foreach (var (spike, marks) in First(input.Spikes, ShownSpikes).Bounded(ShownSpikes))
        {
            var f = spike.Frame;
            _ = text.Line($"{N(f.At, "F1"),7} s  {N(f.DtMs, "F1"),7} ms   GC {N(f.GcMs, "F1")}  JIT {N(f.JitMs, "F1")}  " +
                          $"{text.T("hud-dbg-runqueue")} {N(f.RunQueueMs, "F1")}  {text.T("hud-dbg-outside")} {N(f.OutsideMs, "F1")}" +
                          $"  {f.AllocKb} KiB{Gen(f.Gens)}");
            foreach (var (name, ms) in marks.Bounded(DebugFrames.Marks))
                if (name is not null) _ = text.Line($"{Mark(text, name),-40} {N(ms, "F1"),7} ms", 14);
        }

        if (Array.Exists(input.Causes, static c => c.Count > 0)) _ = text.Blank().Line(text.T("hud-dbg-causes"));
        foreach (var (name, count, max, average) in input.Causes.Bounded(MaxRows))
            if (count > 0) _ = text.Pair(Mark(text, name), $"{count,5} x   avg {N(average, "F1")} ms   max {N(max, "F1")} ms", 4);
    }

    private static string Gen(byte gens) => Assert(gens < 8) && gens != 0 ? "  gen" + (31 - int.LeadingZeroCount(gens)) : "";

    private static string Mark(DebugText text, string name) =>
        Assert(name.Length > 0) && name[0] == '~' && NotNull(text) && text.T("hud-dbg-mark-" + name[1..]) is var shown &&
        !shown.StartsWith("hud-dbg-", StringComparison.Ordinal) ? shown : name;

    internal static ReadOnlySpan<T> First<T>(T[] items, int count) =>
        NotNull(items) && Assert(count >= 0) ? items.AsSpan(0, Math.Min(items.Length, count)) : [];
}
