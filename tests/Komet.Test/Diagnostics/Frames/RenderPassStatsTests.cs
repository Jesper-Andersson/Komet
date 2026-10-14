using Komet.Diagnostics;
using static Komet.Testing.Profiles;

namespace Komet.Test.Diagnostics;

// The per-pass worst is the worst value each pass ever reached, possibly in a different frame each. Asking "what was
// the 79 ms spike" needs the one worst frame kept whole, which is what these tests pin down.
public sealed class RenderPassStatsTests
{
    private static List<(string Key, double Ms)> Shown(RenderPassStats stats)
    {
        return
        [
            .. Enumerable.Range(0, RenderPassStats.Count).Select(place => (stats.Key(place), stats.AverageMs(place)))
        ];
    }

    // A mark carries the time since the previous one, so the ms a pass is meant to get belongs to the mark after its stage begins
    private static ProfileEntryRange Split(double opaqueMs, double guiMs)
    {
        return Frame(10, ("beginrenderstage-Opaque", 0.1), ("rend3D-ret-op", opaqueMs), ("beginrenderstage-Ortho", 0.1),
            ("komet-hud", guiMs));
    }

    // The order is redecided every interval: a pass that was the dearest one window is not owed the top row in the next
    [Test]
    public void OrderFollowsTheLatestWindow()
    {
        var stats = new RenderPassStats();
        for (var i = 0; i < 10; i++) stats.AddFrame(Split(1, 6), true);
        stats.UpdateOrder();
        Assert.That(stats.Key(0), Is.EqualTo("gui"), "the window the GUI pass owned");
        stats.Reset();
        for (var i = 0; i < 10; i++) stats.AddFrame(Split(6, 1), true);
        stats.UpdateOrder();
        var shown = Shown(stats);
        Assert.Multiple(() =>
        {
            Assert.That(stats.Key(0), Is.EqualTo("opaque"), string.Join(", ", shown));
            Assert.That(shown.Select(row => row.Ms), Is.Ordered.Descending, string.Join(", ", shown));
        });
    }

    // The worst steady frame with its own marks, dearest first, and the dearest one's ms
    private static IEnumerable<TestCaseData> WorstFrames()
    {
        static TestCaseData Case(string name, double worstMs, string[] marks, double firstMs,
            params (ProfileEntryRange Frame, bool Steady)[] frames) =>
            new TestCaseData(frames, worstMs, marks, firstMs).SetName(name);

        // The point of the whole feature: the spike's own marks, not the largest mark of any frame
        yield return Case("WorstFrameKeepsItsOwnMarksNotTheLargestEverSeen", 79,
            ["packethandler", "chunkupload", ""], 60, (Frame(20, ("chunkupload", 18)), true),
            (Frame(79, ("packethandler", 60), ("chunkupload", 2)), true));
        yield return Case("MarksAreRankedByCostWithinTheWorstFrame", 79, ["largest", "middle", "small"], 50,
            (Frame(79, ("small", 1), ("largest", 50), ("middle", 20)), true));
        // A load stall or the pause gap would otherwise own the worst frame for the rest of the session
        yield return Case("UnsteadyFramesNeverBecomeTheWorstFrame", 12, ["beginrenderstage-Opaque", "", ""], 4,
            (Frame(12, ("beginrenderstage-Opaque", 4)), true), (Frame(1000, ("chunkupload", 900)), false));
        // Nothing to blame, which is itself the finding
        yield return Case("AFrameWithoutMarksIsStillCountedAsWorst", 79, ["", "", ""], double.NaN,
            (Frame(5, ("beginrenderstage-Opaque", 2)), true), (Frame(79), true));
    }

    [TestCaseSource(nameof(WorstFrames))]
    public void TheWorstFrameIsKeptWhole((ProfileEntryRange Frame, bool Steady)[] frames, double worstMs,
        string[] marks, double firstMs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var stats = new RenderPassStats();
        foreach (var (frame, steady) in frames) stats.AddFrame(frame, steady);
        Assert.Multiple(() =>
        {
            Assert.That(stats.WorstFrameMs, Is.EqualTo(worstMs).Within(0.5));
            Assert.That(Enumerable.Range(0, RenderPassStats.DetailCount).Select(stats.WorstMarkName),
                Is.EqualTo(marks));
            Assert.That(stats.WorstMarkMs(0), double.IsNaN(firstMs) ? Is.NaN : Is.EqualTo(firstMs).Within(0.5));
        });
    }

    // Reset runs every interval; a spike seen once must survive it or the panel could never show one
    [Test]
    public void IntervalResetKeepsTheWorstFrame()
    {
        var stats = new RenderPassStats();
        stats.AddFrame(Frame(79, ("packethandler", 60)), true);
        stats.Reset();
        Assert.Multiple(() =>
        {
            Assert.That(stats.WorstFrameMs, Is.EqualTo(79).Within(0.5));
            Assert.That(stats.WorstMarkName(0), Is.EqualTo("packethandler"));
            Assert.That(stats.AverageTotalMs, Is.Zero, "the interval aggregate is cleared");
        });
    }

    private static FrameRecord Record(double dtMs, ProfileEntryRange? root = null, double outsideMs = 0,
        double gcMs = 0, double jitMs = 0, double runQueueMs = 0)
    {
        return new FrameRecord(0, dtMs, gcMs, outsideMs, jitMs, runQueueMs, root);
    }

    private static ProfileEntryRange Range(string code, double ms, params (string Code, double Ms)[] marks)
    {
        var range = Frame(ms, marks);
        range.Code = code;
        return range;
    }

    // Frames with bursts of block-entity inits carry hundreds of distinct marks: the first MaxMarks are read, the rest of the time is
    // Other, and the frame still counts.
    [Test]
    public void AFrameWithMoreMarksThanTheBoundStillCounts()
    {
        var marks = Enumerable.Range(0, RenderPassStats.MaxMarks + 100).Select(i => ($"initbe-{i}", 0.1)).ToArray();
        var stats = new RenderPassStats();
        stats.AddFrame(Frame(60, marks), true);
        stats.UpdateOrder();
        var shown = Shown(stats);
        Assert.Multiple(() =>
        {
            Assert.That(stats.WorstFrameMs, Is.EqualTo(60).Within(0.5), "the frame is not dropped");
            Assert.That(stats.AverageTotalMs, Is.EqualTo(60).Within(0.5));
            Assert.That(shown.Sum(row => row.Ms), Is.EqualTo(60).Within(0.5), "every millisecond lands in some pass");
            Assert.That(shown.Single(row => row.Key == "other").Ms,
                Is.GreaterThanOrEqualTo(60 - RenderPassStats.MaxMarks * 0.1 - 0.5), "the unread marks' time is Other");
        });
    }

    // The time between one frame's End() and the next Begin() is input and window events: its own pass, part of the frame
    [Test]
    public void TimeBetweenFramesIsItsOwnPassAndPartOfTheFrame()
    {
        var stats = new RenderPassStats();
        stats.AddFrame(Record(100, Frame(58, ("beginrenderstage-Opaque", 0.1), ("rend3D-ret-op", 50)), 42), true);
        stats.UpdateOrder();
        var shown = Shown(stats);
        Assert.Multiple(() =>
        {
            Assert.That(stats.WorstFrameMs, Is.EqualTo(100).Within(0.5),
                "the frame is dt long, not only its profiled part");
            Assert.That(shown.Single(row => row.Key == "outside").Ms, Is.EqualTo(42).Within(0.01));
            Assert.That(stats.WorstMarkName(0), Is.EqualTo("rend3D-ret-op"));
            Assert.That(stats.WorstMarkName(1), Is.EqualTo(SpikeLedger.Outside), "the gap competes with the marks");
            Assert.That(stats.FrameTop[1].Ms, Is.EqualTo(42).Within(0.01));
        });
    }

    // Leave() moves the root's LastMark past an Enter/Leave range, so its time is in no root mark: client entity behaviours are game
    // tick, rendTransparent the transparent pass, any other range Other. A range competes with the marks as a whole.
    [Test]
    public void ChildRangesAreBookedToTheirPass()
    {
        var frame = Frame(40, ("mrl", 0.1), ("gametick", 1));
        frame.ChildRanges = new Dictionary<string, ProfileEntryRange>
        {
            ["behaviors"] = Range("behaviors", 20, ("taskai", 12)), ["rendTransparent"] = Range("rendTransparent", 3),
            ["somemod"] = Range("somemod", 5)
        };
        var stats = new RenderPassStats();
        stats.AddFrame(frame, true);
        stats.UpdateOrder();
        var shown = Shown(stats);
        var tick = shown.FindIndex(row => row.Key == "gametick");
        Assert.Multiple(() =>
        {
            Assert.That(shown[tick].Ms, Is.EqualTo(21).Within(0.01));
            Assert.That(shown.Single(row => row.Key == "transparent").Ms, Is.EqualTo(3).Within(0.01));
            Assert.That(shown.Single(row => row.Key == "other").Ms, Is.EqualTo(16).Within(0.01),
                "mrl, somemod and the rest");
            Assert.That(stats.DetailName(tick, 0), Is.EqualTo("taskai"), "a range's marks are its pass's detail");
            Assert.That(stats.WorstMarkName(0), Is.EqualTo("behaviors"), "the range whole, not its dearest mark");
            Assert.That(stats.WorstMarkName(1), Is.EqualTo("somemod"));
        });
    }

    // GuiScreenRunningGame.OnMouseDown/OnMouseUp wrap a click in FrameProfiler.Begin("mousedown")/End() between two frames, which
    // replaces PrevRootEntry. Read in the next frame's Ortho stage, the click's profile was taken for the frame's, all of it under
    // "end", i.e. Swap. FrameClock's postfix takes the root right after the frame's own End(). The frame's pass is five times the click,
    // so a preemption in the gap between the frames cannot make the gap the frame's worst.
    [Test]
    public void AClickBetweenFramesDoesNotReplaceTheFramesProfile()
    {
        const int FrameMs = 10, ClickMs = 2;
        var profiler = new FrameProfilerUtil(_ => { }) { Enabled = true };
        Clock.With(true, profiler, () =>
        {
            FrameClock.Begin(); // window_RenderFrame's prefix
            profiler.Begin();
            profiler.Mark("beginrenderstage-Opaque");
            Busy.Spin(FrameMs);
            profiler.Mark("rend3D-ret-op");
            profiler.End();
            FrameClock.End(); // window_RenderFrame's postfix
            var frameRoot = profiler.PrevRootEntry;

            profiler.Begin("mousedown"); // OpenTK's event processing, between the frames
            Busy.Spin(ClickMs);
            profiler.Mark("click");
            profiler.End();
            var clickRoot = profiler.PrevRootEntry;

            FrameClock.Begin(); // the next frame completes the record
            var record = FrameClock.Last;
            var passes = new RenderPassStats();
            passes.AddFrame(record, true);
            passes.UpdateOrder();
            Assert.Multiple(() =>
            {
                Assert.That(clickRoot, Is.Not.SameAs(frameRoot), "the engine did overwrite it");
                Assert.That(record.Root, Is.SameAs(frameRoot));
                Assert.That(record.Root!.Marks, Does.ContainKey("rend3D-ret-op").And.Not.ContainKey("click"));
                Assert.That(record.OutsideMs, Is.GreaterThanOrEqualTo(ClickMs),
                    "the click's time is between the frames");
                Assert.That(passes.WorstMarkName(0), Is.EqualTo("rend3D-ret-op"));
                Assert.That(Shown(passes).Single(row => row.Key == "opaque").Ms, Is.GreaterThanOrEqualTo(FrameMs));
            });
        });
    }

    // A frame whose profiler was off leaves the previous root in PrevRootEntry; that one belongs to an older frame
    [Test]
    public void AStaleRootIsNoProfile()
    {
        var profiler = new FrameProfilerUtil(_ => { }) { Enabled = true };
        Clock.With(true, profiler, () =>
        {
            FrameClock.Begin();
            profiler.Begin();
            profiler.End();
            FrameClock.End();
            FrameClock.Begin();
            FrameClock.End(); // no Begin()/End() of the profiler this frame
            FrameClock.Begin();
            Assert.That(FrameClock.Last.Root, Is.Null);
        });
    }

    private static SpikeLedger Settled(double ms = 10, int frames = 500)
    {
        var ledger = new SpikeLedger();
        for (var i = 0; i < frames; i++) _ = ledger.Add(Record(ms), [], true, i);
        return ledger;
    }

    [TestCase(20, false)]
    [TestCase(25, false)]
    [TestCase(26, true)]
    public void SpikesStartAt25MsOverAFastMean(double ms, bool spike)
    {
        var ledger = Settled();
        Assert.That(ledger.Add(Record(ms), [], true, 0), Is.EqualTo(spike), $"threshold {ledger.ThresholdMs:F1} ms");
    }

    // A stretch of slow frames is counted while it is new, then the mean has caught up with it
    [Test]
    public void ASlowStretchStopsCountingOnceTheMeanCatchesUp()
    {
        var ledger = Settled();
        var counted = Enumerable.Range(0, 1000).Count(_ => ledger.Add(Record(40), [], true, 0));
        Assert.That(counted, Is.InRange(20, 200), "neither none of it nor all of it");
    }

    // At 20 fps every frame is 50 ms: the mean carries the threshold up, so steady slowness is not a spike, a hitch on top of it is
    [Test]
    public void OverASlowMeanTheThresholdIsAMultipleOfIt()
    {
        var ledger = Settled(50);
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Count, Is.Zero, "the first frame seeds the mean instead of being measured against 0");
            Assert.That(ledger.ThresholdMs, Is.EqualTo(125).Within(0.5));
            Assert.That(ledger.Add(Record(100), [], true, 0), Is.False);
            Assert.That(ledger.Add(Record(130), [], true, 0), Is.True);
        });
    }

    [Test]
    public void UnsteadyFramesAreNoSpikes()
    {
        var ledger = Settled();
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Add(Record(500), [], false, 0), Is.False);
            Assert.That(ledger.Count, Is.Zero);
        });
    }

    // Ranked by the frame time the cause cost in total: many medium spikes can outweigh one big one
    [Test]
    public void CausesAreRankedByTheTimeTheyCost()
    {
        var ledger = Settled();
        _ = ledger.Add(Record(90), [("esr-afteranim", 70.0)], true, 1);
        for (var i = 0; i < 4; i++) _ = ledger.Add(Record(40), [("doneMTT", 25.0), ("rend3D-ret-op", 5.0)], true, 2);
        ledger.Rank();
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Count, Is.EqualTo(5));
            Assert.That(ledger.Ranked(0).Name, Is.EqualTo("doneMTT"));
            Assert.That(ledger.Ranked(0).Count, Is.EqualTo(4));
            Assert.That(ledger.Ranked(0).AverageMs, Is.EqualTo(40).Within(0.01));
            Assert.That(ledger.Ranked(1).Name, Is.EqualTo("esr-afteranim"));
            Assert.That(ledger.Ranked(1).MaxMs, Is.EqualTo(90).Within(0.01));
            Assert.That(ledger.Ranked(2).Name, Is.Empty);
        });
    }

    // The collector, JIT and the scheduler stop the thread inside some mark; each wins when it covers half the excess over the mean
    [TestCase(20, 0, 0, 0, SpikeLedger.Gc)]
    [TestCase(0, 20, 0, 0, SpikeLedger.Jit)]
    [TestCase(0, 0, 20, 0, SpikeLedger.RunQueue)]
    [TestCase(10, 0, 0, 0, "rend3D-ret-op")]
    [TestCase(0, 0, 0, 35, SpikeLedger.Outside)]
    public void TheDominantPartOfTheFrameIsItsCause(double gcMs, double jitMs, double runQueueMs, double outsideMs,
        string cause)
    {
        var ledger = Settled();
        var top = outsideMs > 30
            ? [(SpikeLedger.Outside, outsideMs), ("rend3D-ret-op", 10.0)]
            : new (string? Name, double Ms)[] { ("rend3D-ret-op", 30.0) };
        _ = ledger.Add(Record(50, outsideMs: outsideMs, gcMs: gcMs, jitMs: jitMs, runQueueMs: runQueueMs), top, true,
            7);
        ledger.Rank();
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Ranked(0).Name, Is.EqualTo(cause));
            Assert.That(ledger.Latest(0).Cause, Is.EqualTo(cause));
            Assert.That(ledger.Latest(0).AtSeconds, Is.EqualTo(7));
        });
    }

    [Test]
    public void WithoutAProfileTheGapOrNothingIsTheCause()
    {
        var ledger = Settled();
        _ = ledger.Add(Record(60, outsideMs: 45), [], true, 0);
        _ = ledger.Add(Record(60, outsideMs: 1), [], true, 0);
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Latest(1).Cause, Is.EqualTo(SpikeLedger.Outside));
            Assert.That(ledger.Latest(0).Cause, Is.EqualTo(SpikeLedger.Unprofiled));
        });
    }

    // The ring keeps the latest spikes whole, newest first, with their own dearest marks
    [Test]
    public void TheRingKeepsTheLatestSpikesWithTheirMarks()
    {
        var ledger = Settled();
        for (var i = 0; i < SpikeLedger.Recent + 3; i++)
            _ = ledger.Add(Record(30 + i, gcMs: 0.1 * i), [($"mark{i}", 20.0), ("second", 1.0)], true, i);
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Stored, Is.EqualTo(SpikeLedger.Recent));
            Assert.That(ledger.Latest(0).DtMs, Is.EqualTo(30 + SpikeLedger.Recent + 2).Within(0.01));
            Assert.That(ledger.LatestMark(0, 0).Name, Is.EqualTo($"mark{SpikeLedger.Recent + 2}"));
            Assert.That(ledger.LatestMark(0, 1).Name, Is.EqualTo("second"));
            Assert.That(ledger.LatestMark(0, 2).Name, Is.Null);
            Assert.That(ledger.Latest(SpikeLedger.Recent - 1).DtMs, Is.EqualTo(33).Within(0.01),
                "the three oldest fell out");
            Assert.That(ledger.Latest(SpikeLedger.Recent).DtMs, Is.Zero, "past the ring");
        });
    }

    // The ledger is bounded: past MaxCauses distinct names new causes are booked together
    [Test]
    public void TooManyCausesAreBookedTogether()
    {
        var ledger = Settled();
        for (var i = 0; i < SpikeLedger.MaxCauses + 50; i++)
        {
            _ = ledger.Add(Record(40), [($"initbe-{i}", 30.0)], true, 0);
            for (var j = 0; j < 20; j++)
                _ = ledger.Add(Record(10), [], true, 0); // isolated hitches, not a slow stretch
        }

        ledger.Rank();
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Count, Is.EqualTo(SpikeLedger.MaxCauses + 50));
            Assert.That(ledger.Ranked(0).Name, Is.EqualTo(SpikeLedger.Other));
            Assert.That(ledger.Ranked(0).Count, Is.EqualTo(51));
        });
    }

    [Test]
    public void ResetForgetsEverySpike()
    {
        var ledger = Settled();
        _ = ledger.Add(Record(90), [("x", 80.0)], true, 0);
        ledger.Reset();
        ledger.Rank();
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Count, Is.Zero);
            Assert.That(ledger.Stored, Is.Zero);
            Assert.That(ledger.Ranked(0).Name, Is.Empty);
            Assert.That(ledger.ThresholdMs, Is.EqualTo(SpikeLedger.MinMs));
        });
    }

    [Test]
    public void TheLedgerKeepsHowOftenEachDearestMarkWasCalled()
    {
        var ledger = Settled();
        var spike = ledger.Add(Record(60, Tree(), 1),
            [(Tesselate, 23.7), ("rend3D-ret-op", 5.0), (SpikeLedger.Outside, 1.0)], true, 500);
        Assert.Multiple(() =>
        {
            Assert.That(spike, Is.True);
            Assert.That(ledger.LatestMark(0, 0), Is.EqualTo((Tesselate, 23.7, 9)));
            Assert.That(ledger.LatestMark(0, 1).Calls, Is.EqualTo(1));
            Assert.That(ledger.LatestMark(0, 2).Calls, Is.Zero, "a pseudo cause has no calls");
            Assert.That(SpikeLedger.Calls(Tree(), "rendTransparent"), Is.EqualTo(2), "a range counts its Enter()s");
            Assert.That(SpikeLedger.Calls(null, Tesselate), Is.Zero, "nor a frame without a profile");
        });
    }
}
