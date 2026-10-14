using System.Text.Json;
using System.Text.Json.Nodes;
using Komet.Bench;
using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Rendering;
using Komet.Tessellation;
using Vintagestory.Server.Systems;
using Komet.Test.Diagnostics;
using static Komet.Testing.Profiles;

namespace Komet.Test.Bench;

// bench.json fails loudly at load: an unknown key, a knob the table does not know, a value out of range or a relative path is an
// error, never a silently ignored wish that shows up as a wrong benchmark an hour later
public sealed class BenchConfigTests
{
    // Two arms, the second without FrustumSweep: the report and scenario tests' A/B run
    internal const string TwoArms =
        "\"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\", \"set\": { \"FrustumSweep\": false } } ]";

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "komet-bench-test");
    private static readonly string[] Profiles = ["quick", "load", "debug", "smoke", "aa", "full", "tess", "gcreg", "culling", "culling1440", "hud", "vulkan"],
        OnOff = ["on", "off"];

    internal static string Json(string extra = "", string? output = null,
        string world = "{ \"savegameId\": \"75695bba\" }")
    {
        output ??= Path.Combine(Root, "result.json").Replace("\\", "\\\\", StringComparison.Ordinal);
        var mods = Path.Combine(Root, "Mods").Replace("\\", "\\\\", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{ "output": "{{output}}", "modDir": "{{mods}}", "world": {{world}}{{(extra.Length > 0 ? ", " + extra : "")}} }""");
    }

    private static string BenchJson() => Path.Combine(Paths.Repo, "scripts", "bench.json");

    [TearDown]
    public void Restore()
    {
        FrustumSweep.Enabled = true;
        ChunkBudget.CapMillis = ChunkBudget.DefaultCapMillis;
    }

    [Test]
    public void AMinimalConfigTakesTheDefaults()
    {
        var config = BenchConfig.Parse(Json());
        Assert.Multiple(() =>
        {
            Assert.That((config.Laps, config.WarmupLaps), Is.EqualTo((8, 1)), "one warm-up lap per arm");
            Assert.That(config.Arms, Has.Count.EqualTo(1));
            Assert.That((config.Settle, config.LapSettle), Is.EqualTo((150.0, 10.0)));
            Assert.That((config.Still, config.Rotate, config.Leg, config.Turn, config.Speed),
                Is.EqualTo((10.0, 20.0, 40.0, 2.0, 11.0)));
            Assert.That(config.Frames, Is.EqualTo(Path.Combine(Root, "frames.csv")));
            Assert.That(config.Status, Is.EqualTo(Path.Combine(Root, "result.json") + ".status"));
            Assert.That(config.SavegameId, Is.EqualTo("75695bba"));
        });
    }

    [Test]
    public void ArmsSetKnobsByTheirTableName()
    {
        var config = BenchConfig.Parse(Json("""
                                            "laps": 4, "revision": "3cb72fc-dirty", "env": { "DOTNET_gcServer": "1" },
                                            "arms": [ { "name": "on" }, { "name": "off", "engine": true, "set": { "FrustumSweep": false, "UploadCap": 6 } } ]
                                            """));
        var off = config.Arms[1];
        Assert.Multiple(() =>
        {
            Assert.That(config.Arms.Select(arm => arm.Name), Is.EqualTo(OnOff));
            Assert.That((config.Arms[0].Engine, off.Engine), Is.EqualTo((false, true)));
            Assert.That(config.WarmupLaps, Is.EqualTo(2));
            Assert.That(off.Settings, Does.Contain(new BenchSetting("FrustumSweep", 0)));
            Assert.That(off.Settings, Does.Contain(new BenchSetting("UploadCap", 6)));
            Assert.That(config.Raw.GetProperty("revision").GetString(), Is.EqualTo("3cb72fc-dirty"),
                "echoed into the result");
        });
    }

    // An engine arm starts from every knob's engine value, the others from the player's; the arm's own settings win either way
    [Test]
    public void AnEngineArmStartsFromTheEngineValues()
    {
        var config = BenchConfig.Parse(Json("""
                                            "laps": 2, "arms": [ { "name": "komet", "set": { "UploadCap": 6 } }, { "name": "engine", "engine": true, "set": { "FrustumSweep": true } } ]
                                            """));
        var baseline = Knobs.Snapshot();
        var values = BenchDriver.ArmValues(baseline, config.Arms, out var error);
        var expected = Knobs.Snapshot(engine: true);
        expected[Knobs.Find("FrustumSweep")] = 1;
        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(values[1], Is.EqualTo(expected));
            Assert.That(values[0][Knobs.Find("UploadCap")], Is.EqualTo(6));
            Assert.That(values[0].Where((_, k) => k != Knobs.Find("UploadCap")),
                Is.EqualTo(baseline.Where((_, k) => k != Knobs.Find("UploadCap"))));
        });
    }

    [Test]
    public void CommentsAndUnderscoreKeysAreIgnored()
    {
        var config = BenchConfig.Parse(Json("""
                                            // the smoke profile
                                            "_comment": "anything", "laps": 2, "sandbox": { "window": [1280, 720] }
                                            """));
        Assert.That(config.Laps, Is.EqualTo(2));
    }

    [TestCase("\"lapps\": 3", "unknown key lapps")]
    [TestCase("\"settle\": { \"quiet\": 3 }", "unknown key settle.quiet")]
    [TestCase("\"route\": { \"speed\": 100 }", "route.speed must be between")]
    [TestCase("\"laps\": 1.5", "laps must be a whole number")]
    [TestCase("\"laps\": 3, \"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\" } ]", "a multiple of the 2 arms")]
    [TestCase("\"env\": { \"DOTNET_gcServer\": 1 }", "env.DOTNET_gcServer must be a string")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"Nonexistent\": true } } ]",
        "arms[0].set.Nonexistent must be one of the knob names in Komet/Core/Features/Features.cs")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"My Mod:water\": true } } ]", "set.My Mod:water must be")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"mymod:\": true } } ]", "modid:name")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"mymod:water\": 1.5 } } ]", "true, false or a whole number")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"UploadCap\": 99 } } ]", "inside the knob's range")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"UploadCap\": 2, \"UploadCap\": 3 } } ]", "named once")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"engine\": 1 } ]", "arms[0].engine must be true or false")]
    [TestCase("\"arms\": [ { \"name\": \"A\" }, { \"name\": \"A\" } ]", "unique names")]
    [TestCase(
        "\"arms\": [ {\"name\":\"1\"},{\"name\":\"2\"},{\"name\":\"3\"},{\"name\":\"4\"},{\"name\":\"5\"},{\"name\":\"6\"},{\"name\":\"7\"},{\"name\":\"8\"},{\"name\":\"9\"} ]",
        "a list of 1 to")]
    [TestCase("", "output must be an absolute path", "result.json")]
    [TestCase("", "output must be an absolute path", "/tmp/a\\u0000b.json")]
    [TestCase("", "world.savegameId", null, "{ }")]
    public void ABadValueNamesItsKey(string extra, string expected, string? output = null,
        string world = "{ \"savegameId\": \"75695bba\" }")
    {
        var error = Assert.Throws<InvalidDataException>(() => BenchConfig.Parse(Json(extra, output, world)))!.Message;
        Assert.That(error, Does.Contain(expected));
    }

    // Every profile scripts/bench.sh can pick, flattened the way the script does it, parses: each knob an arm names resolves through
    // Knobs.Find, so removing or renaming a feature fails here instead of at the start of a benchmark run
    [Test]
    public void EveryProfileInBenchJsonParsesAndNamesOnlyKnownKnobs()
    {
        var file = JsonNode.Parse(File.ReadAllText(BenchJson()),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        var profiles = file["profiles"]!.AsObject();
        Assert.That(profiles.Select(profile => profile.Key), Is.EquivalentTo(Profiles));
        foreach (var (name, profile) in profiles)
        {
            var flat = JsonNode.Parse(Json())!.AsObject();
            foreach (var (key, value) in profile!.AsObject()) flat[key] = value!.DeepClone();
            flat["name"] = name;
            var config = BenchConfig.Parse(flat.ToJsonString());
            var knobs = profile["arms"]?.AsArray()
                .SelectMany(arm => arm!["set"]?.AsObject().Select(entry => entry.Key) ?? []) ?? [];
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(Knobs.Find), Is.All.GreaterThanOrEqualTo(0), name);
                Assert.That(() => BenchScenario.Expand(config), Throws.Nothing, name);
            });
        }
    }

    // Another mod's knob is named modid:name and only checked for its shape when the file is read: it registers later, and the arms
    // resolve once the world is up, where one no mod registered ends the run with its name
    [Test]
    public void AnotherModsKnobResolvesAtTheBoot()
    {
        var config = BenchConfig.Parse(Json("""
                                            "laps": 2, "arms": [ { "name": "on" }, { "name": "dry", "set": { "UploadCap": 6, "mymod:water": false } } ]
                                            """));
        var values = BenchDriver.ArmValues(Knobs.Snapshot(), config.Arms, out var error);
        Assert.Multiple(() =>
        {
            Assert.That(config.Arms[1].Settings, Does.Contain(new BenchSetting("mymod:water", 0)));
            Assert.That(error, Is.EqualTo("bench.json: arms[1].set.mymod:water names no registered knob"));
            Assert.That(values[1][Knobs.Find("UploadCap")], Is.EqualTo(6), "the known ones still resolve");
        });
    }

    // ShaderUseCache.Enabled = true empties its cache even when it already was true: an arm switch writes only differing values
    [Test]
    public void ApplyWritesOnlyWhatDiffers()
    {
        var baseline = Knobs.Snapshot();
        Assert.That(Knobs.Apply(baseline), Is.Zero);
        var changed = (int[])baseline.Clone();
        changed[Knobs.Find("FrustumSweep")] = 0;
        changed[Knobs.Find("UploadCap")] = 5;
        Assert.Multiple(() =>
        {
            Assert.That(Knobs.Apply(changed), Is.EqualTo(2));
            Assert.That((FrustumSweep.Enabled, ChunkBudget.CapMillis), Is.EqualTo((false, 5)));
            Assert.That(Knobs.Apply(baseline), Is.EqualTo(2));
            Assert.That(FrustumSweep.Enabled, Is.True);
        });
    }
}

// FrameClock hands the recorder frame N as N+1 starts, with N's own dt, GC pause and collections, and the recorder books it to the
// stamp the driver left during N
public sealed class BenchRecorderTests
{
    private const int AllocationWindows = 5;
    private static readonly int[] TessPasses = [2, 0, 1], LaterFirst = [1, 0];
    private static readonly float[] TessMs = [3, 0, 4];

    private static BenchRecorder Started(int capacity = 1000, int segments = 4, int spikes = 3)
    {
        var recorder = new BenchRecorder(capacity, segments, spikes);
        recorder.Start();
        return recorder;
    }

    private static void Feed(BenchRecorder recorder, int segment, float ms, BenchFrameTags tags = BenchFrameTags.None,
        ProfileEntryRange? root = null)
    {
        recorder.Stamp(segment, tags, 0, 0, 0);
        recorder.Frame(new FrameRecord(0, ms, 0, 0, 0, 0, root), default);
    }

    [Test]
    public void AFrameIsBookedToTheStampLeftDuringIt()
    {
        var recorder = Started();
        recorder.Stamp(2, BenchFrameTags.Warmup, 10, 20, 1.5f);
        recorder.Frame(new FrameRecord(7, 16, 1.5, 0.2, 0.25, double.NaN, null),
            new FrameCounters(1, 0, 0, 4096, 2048));
        recorder.Frame(new FrameRecord(8, 20, 0, 0, 0, 0, null), default); // the driver did not run in between
        var frames = recorder.Frames.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(frames, Has.Length.EqualTo(2));
            Assert.That((frames[0].Segment, frames[0].Flags, frames[0].DtMs, frames[0].GcMs),
                Is.EqualTo(((short)2, BenchFrameTags.Warmup, 16f, 1.5f)));
            Assert.That((frames[0].Gen0, frames[0].AllocKb, frames[0].MainAllocKb, frames[0].JitMs),
                Is.EqualTo(((byte)1, 4, 2, 0.25f)));
            Assert.That(frames[0].RunQueueMs, Is.NaN, "no schedstat, no run-queue time");
            Assert.That((frames[0].X, frames[0].Z, frames[0].Yaw), Is.EqualTo((10f, 20f, 1.5f)));
            Assert.That(frames[1].Flags & BenchFrameTags.NoStamp, Is.EqualTo(BenchFrameTags.NoStamp),
                "an unstamped frame says so");
            Assert.That(frames[1].Segment, Is.EqualTo(2));
        });
    }

    // The frame FrameClock sees the sink join in has no counters from its start, so it is skipped; the collection lands on its frame.
    // Stats is on (the HUD is looking) so frames close before the sink joins mid-frame, as in BenchDriver.Boot; without it _start is 0
    // and the join frame is never closed at all
    [Test]
    public void FrameClockHandsEveryLaterFrameToTheSinkWithItsOwnCollection()
    {
        var recorder = Started();
        Clock.With(true, null, () =>
        {
            FrameClock.Begin();
            FrameClock.Sink = recorder;
            FrameClock.End();
            FrameClock.Begin();
            for (var frame = 0; frame < 3; frame++)
            {
                recorder.Stamp(0, BenchFrameTags.None, 0, 0, 0);
                if (frame == 1) _ = Busy.Collect();
                Busy.Spin(2);
                FrameClock.End();
                FrameClock.Begin();
            }
        });

        var frames = recorder.Frames.ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(frames, Has.Length.EqualTo(3));
            Assert.That(frames[0].Flags & BenchFrameTags.NoStamp, Is.EqualTo(BenchFrameTags.None),
                "the join frame is not booked");
            Assert.That(frames.Select(f => f.DtMs), Is.All.GreaterThanOrEqualTo(2f));
            Assert.That(frames[1].Gen0, Is.GreaterThanOrEqualTo(1));
            Assert.That(frames[1].GcMs, Is.GreaterThan(0f));
            Assert.That(frames[1].MainAllocKb, Is.GreaterThan(0),
                "Busy.Collect allocated on this thread until gen0 filled");
        });
    }

    // Only a spike frame whose counters show a collection asks the runtime for the last one
    [Test]
    public void ASpikeThatCollectedDescribesTheLastCollection()
    {
        var recorder = Started(spikes: 2);
        Feed(recorder, 0, 20);
        GC.Collect(2, GCCollectionMode.Forced, true);
        recorder.Stamp(0, BenchFrameTags.None, 0, 0, 0);
        recorder.Frame(new FrameRecord(1, 50, 3, 0, 0, 0, null), new FrameCounters(1, 1, 1, 0, 0));
        var spikes = recorder.Spikes(0).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(spikes.Select(s => s.Frame), Is.EqualTo(LaterFirst));
            Assert.That(spikes[0].Gc,
                Has.Property(nameof(BenchGc.Valid)).True.And.Property(nameof(BenchGc.Generation)).EqualTo(2));
            Assert.That(spikes[0].Gc.Index, Is.GreaterThan(0));
            Assert.That(spikes[1].Gc.Valid, Is.False, "a frame that collected nothing");
        });
    }

    // The slots stay sorted, dearest first; a tie keeps the earlier frame. Excluded frames never become spikes.
    [Test]
    public void SpikesKeepTheLargestCountedFramesOfTheirSegment()
    {
        var recorder = Started(spikes: 3);
        foreach (var (ms, tags) in new (float, BenchFrameTags)[]
                 {
                     (5, 0), (9, 0), (7, 0), (9, 0), (3, 0), (12, 0), (500, BenchFrameTags.Paused),
                     (400, BenchFrameTags.Discard)
                 })
            Feed(recorder, 1, ms, tags);
        Assert.Multiple(() =>
        {
            Assert.That(recorder.Spikes(1).ToArray().Select(s => (s.Frame, s.Ms)),
                Is.EqualTo([(5, 12f), (1, 9f), (3, 9f)]));
            Assert.That(recorder.Spikes(0).ToArray().All(s => s.Frame < 0), "other segments stay empty");
        });
    }

    // A dearer frame moves the earlier spike down a rank, and its marks move with it
    [Test]
    public void ASpikeCarriesItsDearestMarksAndTheirCalls()
    {
        var recorder = Started(spikes: 2);
        Feed(recorder, 3, 40, root: Tree());
        Feed(recorder, 3, 60);
        var marks = recorder.Marks(3, 1).ToArray().Where(m => !m.Empty).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(recorder.Spikes(3).ToArray().Select(s => s.Frame), Is.EqualTo(LaterFirst));
            Assert.That(recorder.Marks(3, 0).ToArray(), Is.All.Property(nameof(BenchSpikeMark.Empty)).True,
                "the 60 ms frame had no profile");
            Assert.That(marks.Select(m => m.Ms), Is.Ordered.Descending);
            Assert.That(marks[0], Is.EqualTo(new BenchSpikeMark(null, Tesselate, marks[0].Ms, 9)));
            Assert.That(marks.Single(m => m is { Range: "rendTransparent", Name: null }).Calls, Is.EqualTo(2),
                "a range as a whole");
            Assert.That(marks.Single(m => m.Name == "rendtransp-blocks").Calls, Is.EqualTo(4));
        });
    }

    // TessAccounting counts on the tessellation thread; a frame gets the passes that finished since the previous one, and a world
    // change (the counters start over) does not turn into a negative frame
    [Test]
    public void TessellationPassesAreBookedToTheFrameTheyFinishedIn()
    {
        TessAccounting.Clear();
        try
        {
            var recorder = Started();
            TessAccounting.Record(TessBucket.Full, Ticks(2), false);
            TessAccounting.Record(TessBucket.Edge, Ticks(1), false);
            TessAccounting.Record(TessBucket.Skipped, Ticks(5), false); // an Empty chunk: no pass
            Feed(recorder, 0, 16);
            Feed(recorder, 0, 16);
            TessAccounting.Clear();
            TessAccounting.Record(TessBucket.PriorityFull, Ticks(4), false);
            Feed(recorder, 0, 16);
            var frames = recorder.Frames.ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(frames.Select(f => f.TessPasses), Is.EqualTo(TessPasses));
                Assert.That(frames.Select(f => f.TessMs), Is.EqualTo(TessMs).Within(1e-3));
            });
        }
        finally
        {
            TessAccounting.Clear();
        }
    }

    [Test]
    public void FramesPastTheCapacityAreCountedNotStored()
    {
        var recorder = Started(5);
        for (var i = 0; i < 8; i++) Feed(recorder, 0, 10);
        Assert.That((recorder.Count, recorder.Dropped), Is.EqualTo((5, 3)));
    }

    // The sink runs inside window_RenderFrame's prefix every frame: nothing allocates, spikes and their marks included, as long as
    // the spike frames collected nothing. Every window pushes new spikes, and one of them has to come out at zero: once a test
    // has patched anything, Harmony hooks the JIT, and a compile on this thread (the window's own on-stack replacement, a
    // callee's tier-up) then allocates once. A frame that allocates fails every window.
    [Test]
    public void RecordingAllocatesNothingPerFrame()
    {
        var recorder = Started(30_000, 2, 10);
        ProfileEntryRange[] trees = [Tree(), Tree()]; // FrameProfilerUtil.Begin builds a new root every frame
        for (var i = 0; i < 1000; i++) Feed(recorder, i < 500 ? 0 : 1, 10, root: trees[i % 2]);
        var allocated = new long[AllocationWindows];
        for (var window = 0; window < allocated.Length; window++)
            allocated[window] = Window(recorder, trees, 20 + 10 * window);
        Assert.That(allocated.Min(), Is.Zero, $"bytes per window: {string.Join(", ", allocated)}");
    }

    private static long Window(BenchRecorder recorder, ProfileEntryRange[] trees, int ms)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++) Feed(recorder, 1, ms + i % 7, root: trees[i % 2]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

// The run is a flat, bounded, deterministic list; the arms alternate in mirrored blocks; and the kinematic route moves the way the
// engine's own movement code points and never steps further than the server's position check accepts
public sealed class BenchScenarioTests
{
    private static readonly BenchKind[] Prelude = [BenchKind.Setup, BenchKind.Climb, BenchKind.Settle];

    private static readonly BenchKind[] Lap =
    [
        BenchKind.LapSettle, BenchKind.Still, BenchKind.Rotate, BenchKind.Out, BenchKind.Turn, BenchKind.Back,
        BenchKind.Turn
    ];

    private static readonly BenchKind[] Unmeasured =
        [BenchKind.Setup, BenchKind.Climb, BenchKind.Settle, BenchKind.LapSettle];

    private static readonly BenchKind[] LapWithoutRotate =
        [BenchKind.LapSettle, BenchKind.Still, BenchKind.Out, BenchKind.Turn, BenchKind.Back, BenchKind.Turn];

    private static readonly BenchKind[] LapWithoutRoute = [BenchKind.LapSettle, BenchKind.Rotate];
    private static readonly int[] Abba = [0, 1, 1, 0, 0, 1, 1, 0];

    private static BenchSegment[] Expand(string extra)
    {
        return BenchScenario.Expand(BenchConfig.Parse(BenchConfigTests.Json(extra)));
    }

    private static int[] LapArms(BenchSegment[] segments, bool warmup)
    {
        return [.. segments.Where(s => s.Kind == BenchKind.LapSettle && s.Warmup == warmup).Select(s => s.Arm)];
    }

    [Test]
    public void SetupClimbAndSettleComeBeforeTheLaps()
    {
        var segments = Expand(BenchConfigTests.TwoArms);
        Assert.Multiple(() =>
        {
            Assert.That(segments.Take(3).Select(s => s.Kind), Is.EqualTo(Prelude));
            Assert.That(segments, Has.Length.EqualTo(3 + (2 + 8) * 7),
                "two warm-up and eight measured laps of seven segments");
            Assert.That(segments.Skip(3).Take(7).Select(s => s.Kind), Is.EqualTo(Lap));
            Assert.That(segments.Where(s => s.Far).Select(s => (s.Kind, s.Name)).ToArray(),
                Has.Length.EqualTo(10).And.All.EqualTo((BenchKind.Turn, "turn-far")));
            Assert.That(segments[2].Seconds, Is.EqualTo(150), "the settle is a fixed time");
            Assert.That(segments.Where(s => !s.Measured).Select(s => s.Kind).Distinct(), Is.EquivalentTo(Unmeasured));
        });
    }

    // A B B A A B B A for two arms, A B C C B A for three; every arm warms up once
    [TestCase(BenchConfigTests.TwoArms, new[] { 0, 1, 1, 0, 0, 1, 1, 0 }, new[] { 0, 1 })]
    [TestCase("\"laps\": 6, \"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\" }, { \"name\": \"C\" } ]",
        new[] { 0, 1, 2, 2, 1, 0 }, new[] { 0, 1, 2 })]
    public void MeasuredLapsRunInMirroredBlocks(string arms, int[] measured, int[] warmups)
    {
        var segments = Expand(arms);
        Assert.Multiple(() =>
        {
            Assert.That(LapArms(segments, false), Is.EqualTo(measured));
            Assert.That(LapArms(segments, true), Is.EqualTo(warmups));
            Assert.That(
                segments.Where(s => s.Lap >= 0).GroupBy(s => (s.Lap, s.Warmup))
                    .All(lap => lap.Select(s => s.Arm).Distinct().Count() == 1),
                "a lap never switches arms");
        });
    }

    // The driver switches a lap's arm when it enters the lap's LapSettle, so every lap needs one even without settle time: a lap
    // settle of 0 once dropped the segment, and with it every switch, benchmarking the baseline in every arm
    [Test]
    public void EveryLapStartsWhereItsArmIsSwitchedEvenWithoutSettleTime()
    {
        var segments = Expand("\"settle\": { \"lapSeconds\": 0 }, " + BenchConfigTests.TwoArms);
        var laps = segments.Where(s => s.Lap >= 0).GroupBy(s => (s.Lap, s.Warmup)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(laps, Has.Length.EqualTo(2 + 8));
            Assert.That(laps.All(lap => lap.First().Kind == BenchKind.LapSettle),
                "each lap opens with its switch point");
            Assert.That(LapArms(segments, false), Is.EqualTo(Abba));
            Assert.That(segments.Where(s => s.Kind == BenchKind.LapSettle).All(s => s.Seconds == 0),
                "no settle time: the switch frame only");
        });
    }

    // A lap part of length 0 is left out; leg 0 drops the whole route (both legs and both turns), the lap settle stays
    [Test]
    public void ZeroLengthLapPartsAreLeftOut()
    {
        var noRotate = Expand("\"lap\": { \"rotate\": 0 }, " + BenchConfigTests.TwoArms);
        var noRoute = Expand("\"lap\": { \"still\": 0, \"leg\": 0 }, " + BenchConfigTests.TwoArms);
        Assert.Multiple(() =>
        {
            Assert.That(noRotate, Has.Length.EqualTo(3 + (2 + 8) * 6));
            Assert.That(noRotate.Skip(3).Take(6).Select(s => s.Kind), Is.EqualTo(LapWithoutRotate));
            Assert.That(noRoute, Has.Length.EqualTo(3 + (2 + 8) * 2));
            Assert.That(noRoute.Skip(3).Take(2).Select(s => s.Kind), Is.EqualTo(LapWithoutRoute));
            Assert.That(noRoute.Where(s => s.Lap >= 0).Select(s => s.Kind).Distinct(),
                Is.EquivalentTo(LapWithoutRoute));
        });
    }

    [Test]
    public void TheSegmentCapIsEnforced()
    {
        Assert.That(() => Expand("\"laps\": 40, \"warmupLaps\": 0"),
            Throws.TypeOf<InvalidDataException>().With.Message
                .Contains(BenchScenario.MaxSegments.ToString(CultureInfo.InvariantCulture)));
    }

    // EntityControls.CalcMovementVectors is what the engine walks for Forward at a level pitch of π; the out leg has to point the same way
    [TestCase(0.0)]
    [TestCase(1.0)]
    [TestCase(3.1416)]
    [TestCase(5.5)]
    public void TheOutLegFollowsTheEnginesForwardVector(double yaw)
    {
        var controls = new EntityControls { Forward = true };
        var pos = new EntityPos(0, 0, 0) { Yaw = (float)yaw, Pitch = MathF.PI };
        controls.CalcMovementVectors(pos, 1f);
        var fly = controls.FlyVector;
        var segment = new BenchSegment(BenchKind.Out, 0, 0, false, false, 40);
        var pose = BenchScenario.Pose(segment, 20, yaw, 440);
        var length = Math.Sqrt(fly.X * fly.X + fly.Z * fly.Z);
        var distance = Math.Sqrt(pose.X * pose.X + pose.Z * pose.Z);
        Assert.Multiple(() =>
        {
            Assert.That(fly.Y / length, Is.Zero.Within(1e-6), "pitch π is level, to float precision");
            Assert.That(pose.X / distance, Is.EqualTo(fly.X / length).Within(1e-4));
            Assert.That(pose.Z / distance, Is.EqualTo(fly.Z / length).Within(1e-4));
            Assert.That(distance, Is.EqualTo(220).Within(1e-9), "half the leg after half its time");
            Assert.That(pose.Yaw, Is.EqualTo(yaw), "the camera looks where it flies");
        });
    }

    [Test]
    public void ALapEndsWhereItStarted()
    {
        const double heading = 0.7, leg = 440;

        static BenchSegment Of(BenchKind kind, bool far = false)
        {
            return new BenchSegment(kind, 0, 0, false, far, kind == BenchKind.Turn ? 2 : 40);
        }

        var outEnd = BenchScenario.Pose(Of(BenchKind.Out), 40, heading, leg);
        var farTurn = BenchScenario.Pose(Of(BenchKind.Turn, true), 2, heading, leg);
        var backEnd = BenchScenario.Pose(Of(BenchKind.Back), 40, heading, leg);
        var homeTurn = BenchScenario.Pose(Of(BenchKind.Turn), 2, heading, leg);
        var rotated = BenchScenario.Pose(Of(BenchKind.Rotate), 40, heading, leg);
        Assert.Multiple(() =>
        {
            Assert.That((farTurn.X, farTurn.Z), Is.EqualTo((outEnd.X, outEnd.Z)),
                "the far turn stands where the out leg ended");
            Assert.That(farTurn.Yaw, Is.EqualTo(heading + Math.PI).Within(1e-12), "and faces home");
            Assert.That(Math.Abs(backEnd.X) + Math.Abs(backEnd.Z), Is.Zero.Within(1e-9), "the back leg ends at home");
            Assert.That(Math.Cos(homeTurn.Yaw), Is.EqualTo(Math.Cos(heading)).Within(1e-12), "facing out again");
            Assert.That(Math.Sin(rotated.Yaw), Is.EqualTo(Math.Sin(heading)).Within(1e-12), "a full turn");
            Assert.That(BenchScenario.Pose(Of(BenchKind.Out), 400, heading, leg), Is.EqualTo(outEnd),
                "an overshooting frame stops at the end");
        });
    }

    // One frame advances the scenario by at most BenchDriver.MaxStep; at the highest speeds the config allows, the step one position
    // packet carries still passes EntityPosExtensions.SetFromPacket, which rejects more than 128 blocks per axis
    [TestCase(BenchConfig.MaxSpeed)]
    [TestCase(BenchScenario.ClimbSpeed)]
    public void TheWorstFrameStaysUnderTheServersStepLimit(double speed)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepted(speed * BenchDriver.MaxStep), Is.True);
            Assert.That(Accepted(128.5), Is.False, "the check the test relies on is the engine's");
        });
    }

    private static bool Accepted(double step)
    {
        var pos = new EntityPos(1000, 150, 1000);
        var packet = new Packet_EntityPosition
        {
            X = CollectibleNet.SerializeDoublePrecise(1000 + step), Y = CollectibleNet.SerializeDoublePrecise(150),
            Z = CollectibleNet.SerializeDoublePrecise(1000 + step), MotionX = CollectibleNet.SerializeDoublePrecise(0),
            MotionY = CollectibleNet.SerializeDoublePrecise(0), MotionZ = CollectibleNet.SerializeDoublePrecise(0)
        };
        return pos.SetFromPacket(packet, new EntityAgent());
    }
}
