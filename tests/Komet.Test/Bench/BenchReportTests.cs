using System.Text;
using System.Text.Json;
using Komet.Bench;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Tessellation;

namespace Komet.Test.Bench;

// The result is read by a script, not a person: it has to parse, carry its schema, write missing numbers as null, and pair the arms
// inside their mirrored blocks so the delta of two arms is the difference the switch made
public sealed class BenchReportTests
{
    private const int FramesPerSegment = 100;

    private static readonly int[] LapsOfA = [0, 3];

    // Every segment gets FramesPerSegment frames of ms(segment), one 30 ms spike in the middle with a profile whose dearest mark
    // was called 9 times, and, with tessMs, one tessellation pass per frame taking that long
    private static (BenchRun Run, BenchRecorder Recorder) Run(string arms, System.Func<BenchSegment, float> ms,
        int skip = -1, System.Func<BenchSegment, double>? tessMs = null)
    {
        TessAccounting.Clear();
        var config = BenchConfig.Parse(BenchConfigTests.Json($"\"laps\": 4, \"warmupLaps\": 0, {arms}"));
        var segments = BenchScenario.Expand(config);
        var run = new BenchRun(config, segments) { Baseline = Knobs.Snapshot() };
        run.ArmValues = BenchDriver.ArmValues(run.Baseline, config.Arms, out _);
        var recorder = new BenchRecorder(segments.Length * FramesPerSegment, segments.Length, 3);
        var spike = new ProfileEntryRange { Code = "all", ElapsedTicks = Stopwatch.Frequency / 40, Marks = [] };
        spike.Marks["esr-tesseleateshape"] = new ProfileEntry((int)(Stopwatch.Frequency / 50), 9);
        recorder.Start();
        for (var s = 0; s < segments.Length; s++)
        {
            run.Logs[s] = new BenchSegmentLog(true, segments[s].Seconds, 20, 30, 6000, 5000);
            if (s == skip) continue;
            var tags = segments[s].Measured ? BenchFrameTags.None : BenchFrameTags.Unmeasured;
            for (var i = 0; i < FramesPerSegment; i++)
            {
                if (tessMs != null)
                    TessAccounting.Record(TessBucket.Full, (long)(tessMs(segments[s]) * Stopwatch.Frequency / 1000),
                        false);
                recorder.Stamp(s, i == 0 ? tags | BenchFrameTags.Discard : tags, s, i, 0.5f);
                var middle = i == FramesPerSegment / 2;
                recorder.Frame(
                    new FrameRecord(i, middle ? 30 : ms(segments[s]), middle ? 4 : 0, 0, 0, 0, middle ? spike : null),
                    new FrameCounters(middle ? 1 : 0, 0, 0, 2048, 1024));
            }
        }

        recorder.Stop();
        TessAccounting.Clear();
        (run.Complete, run.Finished) = (true, DateTime.UtcNow);
        return (run, recorder);
    }

    private static JsonElement Json(BenchRun run, BenchRecorder recorder)
    {
        using var stream = new MemoryStream();
        BenchReport.WriteJson(stream, run, recorder);
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static float Arms(BenchSegment segment)
    {
        if (!segment.Measured) return 20f;
        return segment.Arm == 0 ? 10f : 12f;
    }

    [Test]
    public void TheResultParsesAndStatesWhatItIs()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms);
        var json = Json(run, recorder);
        var still = json.GetProperty("segments").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "still");
        var spike = still.GetProperty("spikes")[0];
        Assert.Multiple(() =>
        {
            Assert.That(json.GetProperty("schema").GetString(), Is.EqualTo("komet-bench/2"));
            Assert.That(json.GetProperty("complete").GetBoolean(), Is.True);
            Assert.That(json.GetProperty("error").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(json.GetProperty("segments").GetArrayLength(), Is.EqualTo(run.Segments.Length));
            Assert.That(json.GetProperty("recorder").GetProperty("frames").GetInt32(),
                Is.EqualTo(run.Segments.Length * FramesPerSegment));
            Assert.That(json.GetProperty("config").GetProperty("laps").GetInt32(), Is.EqualTo(4),
                "the config is echoed");
            Assert.That(json.GetProperty("baseline").EnumerateObject().Count(), Is.EqualTo(Knobs.Count));
            Assert.That(still.GetProperty("frametime").GetProperty("n").GetInt32(), Is.EqualTo(FramesPerSegment - 1),
                "the discarded frame is out");
            Assert.That(still.GetProperty("discardedFrames").GetInt32(), Is.EqualTo(1));
            Assert.That(still.GetProperty("frametime").GetProperty("maxMs").GetDouble(), Is.EqualTo(30));
            Assert.That(still.GetProperty("gc").GetProperty("gen0").GetInt32(), Is.EqualTo(1));
            Assert.That(still.GetProperty("gc").GetProperty("maxPauseMs").GetDouble(), Is.EqualTo(4));
            Assert.That(still.GetProperty("cpu").GetProperty("workingSetMb").GetInt64(), Is.EqualTo(6000));
            Assert.That(spike.GetProperty("ms").GetDouble(), Is.EqualTo(30), "the dearest spike first");
            Assert.That(spike.GetProperty("frame").GetInt32() % FramesPerSegment, Is.EqualTo(FramesPerSegment / 2));
            Assert.That(spike.GetProperty("marks")[0].GetProperty("calls").GetInt32(), Is.EqualTo(9));
            Assert.That(spike.GetProperty("gcCounts")[0].GetInt32(), Is.EqualTo(1));
            Assert.That(spike.GetProperty("gc").GetProperty("generation").GetInt32(), Is.InRange(0, 2),
                "it collected: the last collection");
            Assert.That(still.GetProperty("spikes")[1].GetProperty("gc").ValueKind, Is.EqualTo(JsonValueKind.Null),
                "this one did not");
        });
    }

    [Test]
    public void ArmsPoolTheirOwnMeasuredLapsOnly()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms);
        var arms = Json(run, recorder).GetProperty("arms");
        Assert.Multiple(() =>
        {
            Assert.That(arms.GetArrayLength(), Is.EqualTo(2));
            Assert.That(arms[0].GetProperty("pooled").GetProperty("all").GetProperty("avgMs").GetDouble(),
                Is.EqualTo(10 + 20 / 99.0).Within(1e-3));
            Assert.That(arms[1].GetProperty("pooled").GetProperty("all").GetProperty("avgMs").GetDouble(),
                Is.EqualTo(12 + 18 / 99.0).Within(1e-3));
            Assert.That(arms[1].GetProperty("pooled").GetProperty("still").GetProperty("n").GetInt32(),
                Is.EqualTo(2 * (FramesPerSegment - 1)), "B ran laps 1 and 2 of A B B A");
            Assert.That(arms[0].GetProperty("laps").EnumerateArray().Select(l => l.GetProperty("lap").GetInt32()),
                Is.EqualTo(LapsOfA));
            Assert.That(arms[1].GetProperty("values").GetProperty("FrustumSweep").GetInt32(), Is.Zero);
            Assert.That(arms[1].GetProperty("engine").GetBoolean(), Is.False);
        });
    }

    [Test]
    public void TheDeltaIsTheSwitchNotTheNoise()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms);
        var delta = Json(run, recorder).GetProperty("deltas")[0];
        var avg = delta.GetProperty("metrics").GetProperty("avgMs");
        var (aa, aaRecorder) = Run("\"arms\": [ { \"name\": \"A1\" }, { \"name\": \"A2\" } ]", _ => 10f);
        var same = Json(aa, aaRecorder).GetProperty("deltas")[0].GetProperty("metrics").GetProperty("avgMs");
        Assert.Multiple(() =>
        {
            Assert.That((delta.GetProperty("arm").GetString(), delta.GetProperty("vs").GetString()),
                Is.EqualTo(("B", "A")));
            Assert.That(delta.GetProperty("blocks").GetInt32(), Is.EqualTo(2));
            Assert.That(avg.GetProperty("mean").GetDouble(), Is.EqualTo(2 - 2 / 99.0).Within(1e-3));
            Assert.That((avg.GetProperty("sd").GetDouble(), avg.GetProperty("se").GetDouble()),
                Is.EqualTo((0.0, 0.0)).Within(1e-9));
            Assert.That(same.GetProperty("mean").GetDouble(), Is.Zero.Within(1e-9), "A/A: the arms agree");
        });
    }

    // A tessellation fast path in arm B: the lap metric compares the mean time per pass like a frame time, and every segment's
    // chunks object has the passes, their time and the mean; without passes the mean is null
    [Test]
    public void TheTimePerPassIsComparedLikeAFrameTime()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms, tessMs: segment => segment.Arm == 0 ? 2.0 : 0.5);
        var json = Json(run, recorder);
        var delta = json.GetProperty("deltas")[0].GetProperty("metrics").GetProperty("tessMsPerPass");
        var chunks = json.GetProperty("segments").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "still" && s.GetProperty("arm").GetString() == "A")
            .GetProperty("chunks");
        var (none, noneRecorder) = Run(BenchConfigTests.TwoArms, Arms);
        var empty = Json(none, noneRecorder);
        Assert.Multiple(() =>
        {
            Assert.That(delta.GetProperty("mean").GetDouble(), Is.EqualTo(-1.5).Within(1e-3));
            Assert.That(chunks.GetProperty("tessPasses").GetInt64(), Is.EqualTo(FramesPerSegment));
            Assert.That(chunks.GetProperty("tessMs").GetDouble(), Is.EqualTo(2.0 * FramesPerSegment).Within(1e-2));
            Assert.That(chunks.GetProperty("tessMsPerPass").GetDouble(), Is.EqualTo(2.0).Within(1e-3));
            Assert.That(empty.GetProperty("segments")[0].GetProperty("chunks").GetProperty("tessMsPerPass").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
            Assert.That(
                empty.GetProperty("deltas")[0].GetProperty("metrics").GetProperty("tessMsPerPass").GetProperty("mean")
                    .ValueKind,
                Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public void AMissingNumberIsNull()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms, 4);
        var segment = Json(run, recorder).GetProperty("segments")[4];
        Assert.Multiple(() =>
        {
            Assert.That(segment.GetProperty("frames").GetInt32(), Is.Zero);
            Assert.That(segment.GetProperty("frametime").GetProperty("avgMs").ValueKind,
                Is.EqualTo(JsonValueKind.Null));
            Assert.That(segment.GetProperty("focusedFraction").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(segment.GetProperty("spikes").GetArrayLength(), Is.Zero);
        });
    }

    [Test]
    public void TheCsvHasOneRowPerFrame()
    {
        var (run, recorder) = Run(BenchConfigTests.TwoArms, Arms);
        using var stream = new MemoryStream();
        BenchReport.WriteCsv(stream, recorder, run);
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var columns = lines[0].Split(',').Length;
        Assert.Multiple(() =>
        {
            Assert.That(lines, Has.Length.EqualTo(recorder.Count + 1));
            Assert.That(lines.All(line => line.Split(',').Length == columns));
            Assert.That(lines[1], Does.StartWith("0,0,setup,-1,A,"));
            Assert.That(lines.Count(line => line.Contains(",turn-far,", StringComparison.Ordinal)),
                Is.EqualTo(4 * FramesPerSegment), "the far turn is named as in result.json");
        });
    }

    [Test]
    public void ResultFilesAppearWhole()
    {
        var directory = Path.Combine(Path.GetTempPath(), "komet-bench-test-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var json = BenchConfigTests.Json("\"laps\": 1, \"warmupLaps\": 0").Replace(
                Path.Combine(Path.GetTempPath(), "komet-bench-test"), directory, StringComparison.Ordinal);
            var config = BenchConfig.Parse(json);
            var run = new BenchRun(config, BenchScenario.Expand(config));
            var recorder = new BenchRecorder(10, run.Segments.Length, 1);
            recorder.Start();
            recorder.Stamp(0, BenchFrameTags.None, 0, 0, 0);
            recorder.Frame(new FrameRecord(0, 10, 0, 0, 0, 0, null), default);
            recorder.Stop();
            BenchReport.Write(run, recorder);
            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(config.Output) && File.Exists(config.Frames));
                Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
                Assert.That(() => JsonDocument.Parse(File.ReadAllText(config.Output)).Dispose(), Throws.Nothing);
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    // Without KOMET_BENCH nothing is read or registered: the call returns before it touches the api
    [Test]
    public void WithoutTheVariableTheBenchmarkIsInert()
    {
        Assert.That(Environment.GetEnvironmentVariable(Benchmark.Variable), Is.Null.Or.Empty,
            "the test environment must not set it");
        Assert.That(() => Benchmark.Install(null!), Throws.Nothing);
    }

    // The lows are FrameStats', the HUD's own: 1000·k / Σ(worst k ms) with k = n/100, and n/1000 for the 0.1 % low
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(999)]
    [TestCase(1000)]
    public void LowsNeedTheirWindow(int n)
    {
        var summary = BenchReport.Summarize(Enumerable.Repeat(10f, n).ToArray());
        Assert.Multiple(() =>
        {
            Assert.That(summary.Low1Fps, n >= 100 ? Is.EqualTo(100f).Within(1e-3) : Is.NaN);
            Assert.That(summary.Low01Fps, n >= 1000 ? Is.EqualTo(100f).Within(1e-3) : Is.NaN);
        });
    }

    [Test]
    public void TheSummaryCountsWhatItSays()
    {
        float[] ms = [10, 25, 25.5f, 40, 120, 8];
        var summary = BenchReport.Summarize(ms);
        var empty = BenchReport.Summarize([]);
        Assert.Multiple(() =>
        {
            Assert.That((summary.N, summary.Over25, summary.MaxMs), Is.EqualTo((6, 3, 120f)), "strictly above 25 ms");
            Assert.That(summary.Over25PerMin, Is.EqualTo(3 * 60_000.0 / 228.5).Within(1e-9));
            Assert.That(summary.AvgMs, Is.EqualTo(228.5 / 6).Within(1e-4));
            Assert.That(summary.P99Ms, Is.EqualTo(120f));
            Assert.That((empty.N, empty.AvgMs, empty.P99Ms, empty.Low1Fps),
                Is.EqualTo((0, double.NaN, float.NaN, float.NaN)));
        });
    }

    // Nearest rank: the smallest frame with at least p of all frames at or below it
    [TestCase(1, 990, 1f)]
    [TestCase(99, 990, 99f)]
    [TestCase(100, 990, 99f)]
    [TestCase(1000, 990, 990f)]
    public void PercentilesAreNearestRank(int n, int perMille, float expected)
    {
        var sorted = Enumerable.Range(1, n).Select(i => (float)i).ToArray();
        Assert.That(FrameStats.Percentile(sorted, perMille), Is.EqualTo(expected));
    }

    [Test]
    public void SpreadIgnoresMissingLaps()
    {
        Assert.Multiple(() =>
        {
            Assert.That(BenchReport.Spread([1, double.NaN, 3]), Is.EqualTo((2.0, Math.Sqrt(2), 2)));
            Assert.That(BenchReport.Spread([5.0]).Sd, Is.NaN, "one lap has no spread");
        });
    }
}
