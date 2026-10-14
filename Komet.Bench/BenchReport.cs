using System.Globalization;
using System.Text;
using System.Text.Json;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Bench;

// Lows come from FrameStats over the same FrameClock dt, so they match the HUD's
internal readonly record struct BenchSummary(
    int N, double TotalMs, float P99Ms, float MaxMs, float Low1Fps, float Low01Fps, int Over25)
{
    public double AvgMs => N > 0 ? TotalMs / N : double.NaN;
    public double Over25PerMin => TotalMs > 0 ? Over25 * 60_000.0 / TotalMs : double.NaN;
}

// A segment's frames summed in one pass: all frames for queues and tessellation, the counted ones for GC
internal record struct BenchSums(
    int Frames, int Paused, int Discarded, int Focused, double Ms, double GcMs, float MaxGcMs, int Gen0, int Gen1,
    int Gen2, long AllocKb, long MainAllocKb, long TessQ, int TessQMax, long TessNear, int TessNearMax, long UploadQ,
    int UploadQMax, long Received, long TessPasses, double TessMs);

// result.json and frames.csv; deltas pair each arm with arm 0 within one mirrored block, so pairs share drift
internal static class BenchReport
{
    public const string Schema = "komet-bench/2";
    private const int MaxFrames = BenchRecorder.MaxFrames, MaxSegments = BenchScenario.MaxSegments;
    private const float OverMs = 25;

    private static readonly string[] Metrics =
        ["avgMs", "p99Ms", "low1Fps", "low01Fps", "over25PerMin", "tessMsPerPass"];

    private static readonly BenchKind[] PooledKinds =
        [BenchKind.Still, BenchKind.Rotate, BenchKind.Out, BenchKind.Turn, BenchKind.Back];

    // frames.csv first: result.json, which the script waits for, must appear last
    public static void Write(BenchRun run, BenchRecorder recorder)
    {
        if (!NotNull(run) || !NotNull(recorder)) return;
        if (recorder.Count > 0) Atomic(run.Config.Frames, stream => WriteCsv(stream, recorder, run));
        Atomic(run.Config.Output, stream => WriteJson(stream, run, recorder));
    }

    private static void Atomic(string path, Action<Stream> write)
    {
        if (!Assert(path.Length > 0) || !NotNull(write)) return;
        var temporary = path + ".tmp";
        using (var stream = File.Create(temporary)) write(stream);
        File.Move(temporary, path, true);
    }

    public static void WriteCsv(Stream stream, BenchRecorder recorder, BenchRun run)
    {
        if (!NotNull(stream) || !NotNull(recorder) || !Assert(run.Segments.Length > 0)) return;
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 1 << 16, true);
        writer.Write(
            "frame,segment,kind,lap,arm,t,dtMs,gcMs,allocKB,mainAllocKB,gc0,gc1,gc2,jitMs,runQueueMs,tessQ,tessNear,uploadQ," +
            "received,tessPasses,tessMs,x,z,yaw,flags,gpuMs,opaqueGpuMs\n");
        var line = new StringBuilder(192);
        var frames = recorder.Frames;
        double t = 0;
        for (var i = 0; i < Math.Min(frames.Length, MaxFrames); i++)
        {
            var f = frames[i];
            t += f.DtMs / 1000.0;
            var known = f.Segment >= 0 && f.Segment < run.Segments.Length; // -1: a frame before the first stamp
            var segment = known ? run.Segments[f.Segment] : default;
            _ = line.Clear().Append(CultureInfo.InvariantCulture,
                $"{i},{f.Segment},{(known ? segment.Name : "")},{segment.Lap},{(known ? ArmName(run, segment.Arm) : "")},{t:F4},");
            _ = line.Append(CultureInfo.InvariantCulture,
                $"{f.DtMs:F3},{f.GcMs:F3},{f.AllocKb},{f.MainAllocKb},{f.Gen0},{f.Gen1},{f.Gen2},{f.JitMs:F3},{f.RunQueueMs:F3},");
            _ = line.Append(CultureInfo.InvariantCulture,
                $"{f.TessQ},{f.TessNear},{f.UploadQ},{f.Received},{f.TessPasses},{f.TessMs:F3},");
            _ = line.Append(CultureInfo.InvariantCulture, $"{f.X:F2},{f.Z:F2},{f.Yaw:F4},{(int)f.Flags},{f.GpuMs:F3},{f.OpaqueGpuMs:F3}\n");
            writer.Write(line);
        }
    }

    public static void WriteJson(Stream stream, BenchRun run, BenchRecorder recorder)
    {
        if (!NotNull(stream) || !NotNull(run) || !NotNull(recorder)) return;
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        var ranges = Ranges(recorder.Frames, run.Segments.Length);
        var scratch = new float[Math.Max(1, recorder.Count)];
        json.WriteStartObject();
        Header(json, run, recorder);
        json.WriteStartArray("segments");
        for (var s = 0; s < Math.Min(run.Segments.Length, MaxSegments); s++)
            Segment(json, run, recorder, s, ranges[s], scratch);
        json.WriteEndArray();
        var laps = LapMetrics(run, recorder.Frames, ranges, scratch);
        json.WriteStartArray("arms");
        for (var a = 0; a < Math.Min(run.Config.Arms.Count, BenchConfig.MaxArms); a++)
            Arm(json, run, recorder.Frames, a, ranges, laps, scratch);
        json.WriteEndArray();
        Deltas(json, run, laps);
        json.WriteEndObject();
        json.Flush();
    }

    private static void Header(Utf8JsonWriter json, BenchRun run, BenchRecorder recorder)
    {
        if (!NotNull(json) || !Assert(run.Segments.Length == run.Logs.Length)) return;
        json.WriteString("schema", Schema);
        json.WriteBoolean("complete", run.Complete && run.Error is null);
        json.WriteString("error", run.Error); // null when the run had none
        json.WriteString("name", run.Config.Name);
        json.WriteString("startedUtc", run.Started.ToString("O", CultureInfo.InvariantCulture));
        json.WriteString("finishedUtc", run.Finished.ToString("O", CultureInfo.InvariantCulture));
        json.WriteStartObject("info");
        foreach (var (key, value) in run.Info.Bounded(BenchRun.MaxInfo)) json.WriteString(key, value);
        json.WriteEndObject();
        KnobValues(json, "baseline", run.Baseline);
        json.WriteStartObject("recorder");
        json.WriteNumber("frames", recorder.Count);
        json.WriteNumber("dropped", recorder.Dropped);
        json.WriteNumber("capacity", recorder.Capacity);
        json.WriteEndObject();
        json.WriteStartArray("chat");
        foreach (var chat in run.Chat.Bounded(BenchRun.MaxChat))
        {
            json.WriteStartObject();
            Number(json, "t", chat.Seconds);
            json.WriteString("type", chat.Type);
            json.WriteString("text", chat.Text);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WritePropertyName("config");
        run.Config.Raw.WriteTo(json);
    }

    private static void KnobValues(Utf8JsonWriter json, string name, int[] values)
    {
        if (!Assert(name.Length > 0) || !Assert(values.Length <= Knobs.MaxKnobs)) return;
        json.WriteStartObject(name);
        for (var k = 0; k < Math.Min(values.Length, Knobs.MaxKnobs); k++) json.WriteNumber(Knobs.Name(k), values[k]);
        json.WriteEndObject();
    }

    // A segment's frames are one contiguous block: the driver only moves forward
    private static (int From, int To)[] Ranges(ReadOnlySpan<BenchFrame> frames, int segments)
    {
        var ranges = new (int From, int To)[Math.Max(segments, 1)];
        if (!Assert(segments <= MaxSegments) || !Assert(frames.Length <= MaxFrames)) return ranges;
        Array.Fill(ranges, (-1, -1));
        for (var i = 0; i < Math.Min(frames.Length, MaxFrames); i++)
        {
            var s = frames[i].Segment;
            if (s >= 0 && s < ranges.Length) ranges[s] = (ranges[s].From < 0 ? i : ranges[s].From, i + 1);
        }

        return ranges;
    }

    private static int Gather(ReadOnlySpan<BenchFrame> frames, (int From, int To) range, float[] scratch, int length)
    {
        if (range.From < 0 || !Assert(range.To <= frames.Length) || !Assert(length >= 0)) return length;
        for (var i = range.From; i < Math.Min(range.To, MaxFrames); i++)
            if ((frames[i].Flags & BenchRecorder.Excluded) == 0 && Index(length, scratch.Length))
                scratch[length++] = frames[i].DtMs;
        return length;
    }

    private static BenchSums Sum(ReadOnlySpan<BenchFrame> frames, (int From, int To) range)
    {
        var sums = new BenchSums();
        if (!Assert(range.To <= frames.Length) || !Assert(range.From <= range.To)) return sums;
        for (var i = Math.Max(range.From, 0); i < Math.Min(range.To, MaxFrames); i++)
        {
            var f = frames[i];
            sums.Frames++;
            (sums.TessQ, sums.TessQMax) = (sums.TessQ + f.TessQ, Math.Max(sums.TessQMax, f.TessQ));
            (sums.TessNear, sums.TessNearMax) = (sums.TessNear + f.TessNear, Math.Max(sums.TessNearMax, f.TessNear));
            (sums.UploadQ, sums.UploadQMax) = (sums.UploadQ + f.UploadQ, Math.Max(sums.UploadQMax, f.UploadQ));
            (sums.Received, sums.TessPasses, sums.TessMs) =
                (sums.Received + f.Received, sums.TessPasses + f.TessPasses, sums.TessMs + f.TessMs);
            if ((f.Flags & BenchFrameTags.Paused) != 0) sums.Paused++;
            if ((f.Flags & (BenchFrameTags.Discard | BenchFrameTags.NoStamp)) != 0) sums.Discarded++;
            if ((f.Flags & BenchFrameTags.Unfocused) == 0) sums.Focused++;
            if ((f.Flags & BenchRecorder.Excluded) != 0) continue;
            (sums.Ms, sums.GcMs, sums.MaxGcMs) = (sums.Ms + f.DtMs, sums.GcMs + f.GcMs, Math.Max(sums.MaxGcMs, f.GcMs));
            (sums.Gen0, sums.Gen1, sums.Gen2) = (sums.Gen0 + f.Gen0, sums.Gen1 + f.Gen1, sums.Gen2 + f.Gen2);
            (sums.AllocKb, sums.MainAllocKb) = (sums.AllocKb + f.AllocKb, sums.MainAllocKb + f.MainAllocKb);
        }

        return sums;
    }

    private static void Segment(Utf8JsonWriter json, BenchRun run, BenchRecorder recorder, int s,
        (int From, int To) range, float[] scratch)
    {
        if (!Index(s, run.Segments.Length) || !NotNull(json)) return;
        var (segment, log) = (run.Segments[s], run.Logs[s]);
        var sums = Sum(recorder.Frames, range);
        json.WriteStartObject();
        json.WriteNumber("index", s);
        json.WriteString("name", segment.Name);
        json.WriteNumber("lap", segment.Lap);
        json.WriteString("arm", ArmName(run, segment.Arm));
        json.WriteBoolean("warmup", segment.Warmup);
        json.WriteBoolean("measured", segment.Measured);
        json.WriteBoolean("ran", log.Ran);
        Number(json, "plannedSeconds", segment.Seconds);
        Number(json, "seconds", log.Seconds);
        json.WriteNumber("frames", sums.Frames);
        json.WriteNumber("pausedFrames", sums.Paused);
        json.WriteNumber("discardedFrames", sums.Discarded);
        Number(json, "focusedFraction", sums.Frames > 0 ? (double)sums.Focused / sums.Frames : double.NaN);
        Frametime(json, "frametime", Summarize(scratch.AsSpan(0, Gather(recorder.Frames, range, scratch, 0))));
        Details(json, sums, log);
        json.WriteStartArray("spikes");
        var spikes = recorder.Spikes(s);
        for (var rank = 0; rank < Math.Min(spikes.Length, BenchRecorder.MaxSpikes); rank++)
            if (spikes[rank].Frame >= 0)
                Spike(json, recorder.Frames, spikes[rank], recorder.Marks(s, rank)); // -1: an empty slot
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void Details(Utf8JsonWriter json, BenchSums sums, in BenchSegmentLog log)
    {
        if (!NotNull(json) || !Assert(sums.Focused <= sums.Frames)) return;
        var seconds = sums.Ms / 1000;
        json.WriteStartObject("gc");
        Number(json, "pauseMs", sums.GcMs);
        Number(json, "maxPauseMs", sums.MaxGcMs);
        Number(json, "pausePercent", sums.Ms > 0 ? 100 * sums.GcMs / sums.Ms : double.NaN);
        json.WriteNumber("gen0", sums.Gen0);
        json.WriteNumber("gen1", sums.Gen1);
        json.WriteNumber("gen2", sums.Gen2);
        Number(json, "allocMBps", seconds > 0 ? sums.AllocKb / 1024.0 / seconds : double.NaN);
        Number(json, "mainAllocMBps", seconds > 0 ? sums.MainAllocKb / 1024.0 / seconds : double.NaN);
        json.WriteEndObject();
        var n = sums.Frames > 0 ? sums.Frames : double.NaN;
        json.WriteStartObject("chunks");
        Number(json, "tessQAvg", sums.TessQ / n);
        json.WriteNumber("tessQMax", sums.TessQMax);
        Number(json, "tessNearAvg", sums.TessNear / n); // marks near the player still waiting: lower is sooner
        json.WriteNumber("tessNearMax", sums.TessNearMax);
        Number(json, "uploadQAvg", sums.UploadQ / n);
        json.WriteNumber("uploadQMax", sums.UploadQMax);
        json.WriteNumber("received", sums.Received);
        json.WriteNumber("tessPasses", sums.TessPasses);
        Number(json, "tessMs", sums.TessMs);
        Number(json, "tessMsPerPass", sums.TessPasses > 0 ? sums.TessMs / sums.TessPasses : double.NaN);
        json.WriteEndObject();
        json.WriteStartObject("cpu");
        Number(json, "processPercent", log.CpuPercent);
        Number(json, "systemPercent", log.SystemCpuPercent);
        json.WriteNumber("workingSetMb", log.WorkingSetMb);
        json.WriteNumber("managedMb", log.ManagedMb);
        json.WriteEndObject();
    }

    // Sorts ms in place
    public static BenchSummary Summarize(Span<float> ms)
    {
        if (!Assert(ms.Length <= MaxFrames) || ms.IsEmpty)
            return new BenchSummary(0, 0, float.NaN, float.NaN, float.NaN, float.NaN, 0);
        double total = 0;
        var over = 0;
        for (var i = 0; i < Math.Min(ms.Length, MaxFrames); i++)
            (total, over) = (total + ms[i], ms[i] > OverMs ? over + 1 : over);
        ms.Sort();
        if (!Finite(total) || !Assert(ms[0] <= ms[^1])) total = double.NaN;
        return new BenchSummary(ms.Length, total, FrameStats.Percentile(ms, 990), ms[^1], FrameStats.LowFps(ms, 100),
            FrameStats.LowFps(ms, 1000), over);
    }

    private static void Frametime(Utf8JsonWriter json, string name, BenchSummary summary)
    {
        if (!Assert(name.Length > 0) || !Assert(summary.N >= 0)) return;
        json.WriteStartObject(name);
        json.WriteNumber("n", summary.N);
        Number(json, "avgMs", summary.AvgMs);
        Number(json, "p99Ms", summary.P99Ms);
        Number(json, "maxMs", summary.MaxMs);
        Number(json, "low1Fps", summary.Low1Fps);
        Number(json, "low01Fps", summary.Low01Fps);
        Number(json, "over25PerMin", summary.Over25PerMin);
        json.WriteEndObject();
    }

    private static void Spike(Utf8JsonWriter json, ReadOnlySpan<BenchFrame> frames, BenchSpike spike,
        ReadOnlySpan<BenchSpikeMark> marks)
    {
        if (!Index(spike.Frame, frames.Length) || !Assert(marks.Length <= BenchRecorder.MarksPerSpike)) return;
        var f = frames[spike.Frame];
        json.WriteStartObject();
        json.WriteNumber("frame", spike.Frame);
        Number(json, "ms", spike.Ms);
        Number(json, "gcPauseMs", f.GcMs);
        json.WriteStartArray("gcCounts");
        json.WriteNumberValue(f.Gen0);
        json.WriteNumberValue(f.Gen1);
        json.WriteNumberValue(f.Gen2);
        json.WriteEndArray();
        LastGc(json, spike.Gc);
        json.WriteNumber("allocKB", f.AllocKb);
        Number(json, "jitMs", f.JitMs);
        // the main thread runnable but waiting for a core: another process's load
        Number(json, "runQueueMs", f.RunQueueMs);
        json.WriteStartArray("marks");
        for (var m = 0; m < Math.Min(marks.Length, BenchRecorder.MarksPerSpike); m++)
        {
            if (marks[m].Empty) continue;
            json.WriteStartObject();
            if (marks[m].Range is { } range) json.WriteString("range", range);
            if (marks[m].Name is { } mark) json.WriteString("mark", mark);
            Number(json, "ms", marks[m].Ms);
            json.WriteNumber("calls", marks[m].Calls);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void LastGc(Utf8JsonWriter json, BenchGc gc)
    {
        if (!NotNull(json) || !Finite(gc.PauseMs)) return;
        if (!gc.Valid)
        {
            json.WriteNull("gc");
            return;
        }

        json.WriteStartObject("gc");
        json.WriteNumber("index", gc.Index);
        json.WriteNumber("generation", gc.Generation);
        Number(json, "pauseMs", gc.PauseMs);
        json.WriteBoolean("compacted", gc.Compacted);
        json.WriteBoolean("concurrent", gc.Concurrent);
        json.WriteNumber("promotedKB", gc.PromotedKb);
        json.WriteEndObject();
    }

    // Per measured lap: its measured segments pooled, then the metrics the deltas compare
    private static double[][] LapMetrics(BenchRun run, ReadOnlySpan<BenchFrame> frames, (int From, int To)[] ranges,
        float[] scratch)
    {
        var laps = new double[run.Config.Laps][];
        if (!Assert(laps.Length <= BenchConfig.MaxLaps) || !Assert(ranges.Length >= run.Segments.Length)) return laps;
        for (var lap = 0; lap < Math.Min(laps.Length, BenchConfig.MaxLaps); lap++)
        {
            var (n, passes, ms) = (0, 0L, 0.0);
            for (var s = 0; s < Math.Min(run.Segments.Length, MaxSegments); s++)
            {
                if (run.Segments[s] is not { Measured: true, Warmup: false } segment || segment.Lap != lap) continue;
                n = Gather(frames, ranges[s], scratch, n);
                var sums = Sum(frames, ranges[s]);
                (passes, ms) = (passes + sums.TessPasses, ms + sums.TessMs);
            }

            var summary = Summarize(scratch.AsSpan(0, n));
            var perPass = passes > 0 ? ms / passes : double.NaN;
            laps[lap] =
                [summary.AvgMs, summary.P99Ms, summary.Low1Fps, summary.Low01Fps, summary.Over25PerMin, perPass];
        }

        return laps;
    }

    private static void Arm(Utf8JsonWriter json, BenchRun run, ReadOnlySpan<BenchFrame> frames, int arm,
        (int From, int To)[] ranges, double[][] laps, float[] scratch)
    {
        if (!Index(arm, run.Config.Arms.Count) || !Assert(ranges.Length >= run.Segments.Length)) return;
        json.WriteStartObject();
        json.WriteString("name", run.Config.Arms[arm].Name);
        json.WriteBoolean("engine", run.Config.Arms[arm].Engine);
        if (arm < run.ArmValues.Length)
            KnobValues(json, "values", run.ArmValues[arm]); // none when the run never booted
        json.WriteStartObject("pooled");
        for (var k = 0; k <= PooledKinds.Length; k++) // the last pass pools every measured kind together
        {
            var n = 0;
            for (var s = 0; s < Math.Min(run.Segments.Length, MaxSegments); s++)
                if (run.Segments[s] is { Measured: true, Warmup: false } segment && segment.Arm == arm &&
                    (k == PooledKinds.Length || segment.Kind == PooledKinds[k]))
                    n = Gather(frames, ranges[s], scratch, n);
            Frametime(json, k < PooledKinds.Length ? BenchSegment.KindName(PooledKinds[k]) : "all",
                Summarize(scratch.AsSpan(0, n)));
        }

        json.WriteEndObject();
        json.WriteStartArray("laps");
        for (var lap = 0; lap < Math.Min(laps.Length, BenchConfig.MaxLaps); lap++)
        {
            if (BenchScenario.ArmOf(lap, run.Config.Arms.Count) != arm) continue;
            json.WriteStartObject();
            json.WriteNumber("lap", lap);
            for (var m = 0; m < Math.Min(laps[lap].Length, Metrics.Length); m++) Number(json, Metrics[m], laps[lap][m]);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    // One lap per arm per block; se is the paired mean's standard error, so mean / se is the paired t over blocks.
    private static void Deltas(Utf8JsonWriter json, BenchRun run, double[][] laps)
    {
        var arms = run.Config.Arms.Count;
        if (!NotNull(json) || !Assert(arms is > 0 and <= BenchConfig.MaxArms)) return;
        var blocks = laps.Length / arms;
        Span<double> deltas = stackalloc double[BenchConfig.MaxLaps];
        json.WriteStartArray("deltas");
        for (var arm = 1; arm < Math.Min(arms, BenchConfig.MaxArms); arm++)
        {
            json.WriteStartObject();
            json.WriteString("arm", run.Config.Arms[arm].Name);
            json.WriteString("vs", run.Config.Arms[0].Name);
            json.WriteNumber("blocks", blocks);
            json.WriteStartObject("metrics");
            for (var m = 0; m < Metrics.Length; m++)
            {
                for (var b = 0; b < Math.Min(blocks, BenchConfig.MaxLaps); b++) deltas[b] = Delta(run, laps, b, arm, m);
                var (mean, sd, n) = Spread(deltas[..Math.Min(blocks, BenchConfig.MaxLaps)]);
                json.WriteStartObject(Metrics[m]);
                Number(json, "mean", mean);
                Number(json, "sd", sd);
                Number(json, "se", sd / Math.Sqrt(n));
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static double Delta(BenchRun run, double[][] laps, int block, int arm, int metric)
    {
        var arms = run.Config.Arms.Count;
        if (!Index(metric, Metrics.Length) || !Index(arm, arms) || !Assert(arms <= BenchConfig.MaxArms))
            return double.NaN;
        double mine = double.NaN, theirs = double.NaN;
        for (var step = 0; step < Math.Min(Math.Min(arms, laps.Length - block * arms), BenchConfig.MaxArms); step++)
        {
            var lap = block * arms + step;
            if (BenchScenario.ArmOf(lap, arms) == arm) mine = laps[lap][metric];
            if (BenchScenario.ArmOf(lap, arms) == 0) theirs = laps[lap][metric];
        }

        return mine - theirs;
    }

    // Mean and sample sd over finite values, and their count; sd is NaN below two
    public static (double Mean, double Sd, int N) Spread(ReadOnlySpan<double> values)
    {
        if (!Assert(values.Length <= MaxFrames)) return (double.NaN, double.NaN, 0);
        var (sum, n) = (0.0, 0);
        for (var i = 0; i < Math.Min(values.Length, MaxFrames); i++)
            if (double.IsFinite(values[i])) (sum, n) = (sum + values[i], n + 1);
        if (n == 0 || !Finite(sum)) return (double.NaN, double.NaN, n);
        var (mean, squares) = (sum / n, 0.0);
        for (var i = 0; i < Math.Min(values.Length, MaxFrames); i++)
            if (double.IsFinite(values[i])) squares += (values[i] - mean) * (values[i] - mean);
        return (mean, n > 1 ? Math.Sqrt(squares / (n - 1)) : double.NaN, n);
    }

    private static string ArmName(BenchRun run, int arm) =>
        NotNull(run) && Index(arm, run.Config.Arms.Count) ? run.Config.Arms[arm].Name : "";

    // JSON has no NaN: a number that does not exist is written as null
    private static void Number(Utf8JsonWriter json, string name, double value)
    {
        if (!Assert(name.Length > 0) || !NotNull(json)) return;
        if (double.IsFinite(value)) json.WriteNumber(name, Math.Round(value, 4));
        else json.WriteNull(name);
    }
}
