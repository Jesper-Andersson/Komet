using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

public sealed class DiagnosticsTests
{
    [TearDown]
    public void SwitchModTimesOff()
    {
        ModTimes.Enabled = false;
    }

    internal static bool PatchedBy(MethodBase method, Harmony harmony) =>
        Harmony.GetPatchInfo(method) is { } info && info.Owners.Contains(harmony.Id);

    [Test]
    public void TheFrameClockPatchesTheRenderFrameHandler()
    {
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        Assert.That(frame, Is.Not.Null,
            "ClientPlatformWindows.window_RenderFrame is gone, the HUD records the engine's dt alone");
        using var harmony = new TestHarmony("komet-test-frameclock");
        FrameClock.Install(harmony);
        var patches = Harmony.GetPatchInfo(frame);
        Assert.Multiple(() =>
        {
            Assert.That(patches?.Prefixes.Select(patch => patch.owner), Does.Contain(harmony.Id));
            Assert.That(patches?.Postfixes.Select(patch => patch.owner), Does.Contain(harmony.Id));
        });
    }

    private static List<RenderHandler>[] Stage(params Action<float>[] actions)
    {
        var stages = Enumerable.Range(0, Enum.GetValues<EnumRenderStage>().Length)
            .Select(_ => new List<RenderHandler>()).ToArray();
        stages[(int)EnumRenderStage.Opaque].AddRange(actions.Select(action =>
            new RenderHandler { Renderer = new DummyRenderer { action = action }, ProfilingName = "dummy" }));
        return stages;
    }

    // Every firepit registers a renderer of its own; past the bound the engine's loop runs them all, unprofiled
    [Test]
    public void AStageWithMoreRenderersThanTheBoundGoesBackToTheEngine()
    {
        var ran = new List<int>();
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));

        Action<float> Renderer(int index)
        {
            return _ => ran.Add(index);
        }

        ModTimes.Enabled = true;
        Assert.Multiple(() =>
        {
            var many = Stage([.. Enumerable.Range(0, ModTimes.MaxRenderers + 76).Select(Renderer)]);
            Assert.That(ModTimes.RenderStage(game, many, EnumRenderStage.Opaque, 0.016f), Is.True,
                "the engine's own loop runs");
            Assert.That(ran, Is.Empty, "and nothing ran twice");
        });
        Assert.Multiple(() =>
        {
            Assert.That(ModTimes.RenderStage(game, Stage([.. Enumerable.Range(0, 10).Select(Renderer)]),
                EnumRenderStage.Opaque, 0.016f), Is.False);
            Assert.That(ran, Is.EqualTo(Enumerable.Range(0, 10)), "within the bound every renderer runs, in order");
        });
    }

    // Here every renderer is Komet.Test's
    [Test]
    public void TheModTimesRankEachModsDearestEntries()
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        ModTimes.Enabled = true;
        _ = ModTimes.RenderStage(game, Stage(Cheap, Dear, Middle), EnumRenderStage.Opaque, 0.016f);
        var times = new ModTimes();
        times.Update(1, 1f);
        Assert.Multiple(() =>
        {
            Assert.That(times.ModName(0), Is.EqualTo(typeof(DiagnosticsTests).Assembly.GetName().Name));
            Assert.That(times.DetailName(0, 0), Does.StartWith("DiagnosticsTests.Dear "));
            Assert.That(times.DetailName(0, 1), Does.StartWith("DiagnosticsTests.Middle "));
            Assert.That(times.DetailMs(0, 0), Is.GreaterThan(times.DetailMs(0, 1)));
            Assert.That(times.ModMs(0), Is.GreaterThanOrEqualTo(times.DetailMs(0, 0) + times.DetailMs(0, 1)));
        });
    }

    private static void Cheap(float dt)
    {
        // no work: the last of the three
    }

    private static void Dear(float dt)
    {
        Busy.Spin(12);
    }

    private static void Middle(float dt)
    {
        Busy.Spin(1);
    }

    private static void Install(Harmony harmony)
    {
        ModTimes.Install(harmony, DispatchProxy.Create<IModLoader, OneModLoader>(), new QuietLogger());
    }

    // Off by default the engine runs untouched; after Ready the first switch-on patches it. The next session's install starts over:
    // switched on by the saved settings in StartClientSide, it waits for that session's Ready. Ends patched-and-removed, so later
    // switch-ons in this process stay inert.
    [Test]
    public void TheModTimesPatchTheEngineOnlyOnceSwitchedOnAndReady()
    {
        var stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        using var harmony = new TestHarmony("komet-test-modtimes");
        ModTimes.Enabled = false;
        Install(harmony);
        ModTimes.Ready();
        var before = PatchedBy(stage, harmony);
        ModTimes.Enabled = true;
        var enabled = PatchedBy(stage, harmony);
        harmony.UnpatchAll(harmony.Id);
        Install(harmony);
        var early = PatchedBy(stage, harmony);
        ModTimes.Ready();
        Assert.Multiple(() =>
        {
            Assert.That(before, Is.False, "not patched by default");
            Assert.That(enabled, Is.True);
            Assert.That(early, Is.False, "switched on before the level is finalized, it waits");
            Assert.That(PatchedBy(stage, harmony), Is.True, "the new session's Ready");
        });
    }

    // A mod that starts after Komet puts a transpiler on the loop RenderStage would skip; Ready sees it, leaves the renderers to that
    // loop and still times the listeners
    [Test]
    public void ALaterModsTranspilerOnTheRenderLoopLeavesOnlyTheRenderersOut()
    {
        var stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        var entity = AccessTools.Method(typeof(GameTickListener), nameof(GameTickListener.OnTriggered));
        var block = AccessTools.Method(typeof(GameTickListenerBlock), nameof(GameTickListenerBlock.OnTriggered));
        using var harmony = new TestHarmony("komet-test-modtimes");
        using var other = new TestHarmony("komet-test-othermod");
        ModTimes.Enabled = false;
        Install(harmony);
        ModTimes.Enabled = true;
        _ = other.Patch(stage, transpiler: Foreign.Transpiler);
        ModTimes.Ready();
        Assert.Multiple(() =>
        {
            Assert.That(PatchedBy(stage, harmony), Is.False, "the other mod's loop runs");
            Assert.That(PatchedBy(entity, harmony), Is.True, "the entity listeners are timed");
            Assert.That(PatchedBy(block, harmony), Is.True, "and the block listeners");
        });
    }

}

// IModLoader with one mod; only Mods is read by the walk
public class OneModLoader : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != "get_Mods") throw new NotSupportedException(targetMethod?.Name);
        var mod = new TestMod();
        typeof(Mod).GetProperty(nameof(Mod.Info))!.SetValue(mod,
            new ModInfo { Name = "Komet", Version = "1.2.3", ModID = "komet" });
        return new Mod[] { mod };
    }

    private sealed class TestMod : Mod;
}

// The lows are taken over the frames actually recorded, so an empty or short history must report nothing rather than a number
public sealed class FrameStatsTests
{
    private const int History = 2000;

    private static FrameStats Filled(int frames, float ms = 10f)
    {
        var stats = new FrameStats();
        for (var i = 0; i < Math.Min(frames, 4 * History); i++) stats.Record(ms / 1000f, 0, true);
        stats.SampleWindow();
        return stats;
    }

    [Test]
    public void FramesThatAreNotSteadyStayOutOfTheHistory()
    {
        var stats = new FrameStats();
        for (var i = 0; i < 500; i++) stats.Record(1f, 0, false); // a load stall
        for (var i = 0; i < 200; i++) stats.Record(0.01f, 0, true);
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(200));
            Assert.That(stats.WorstMs, Is.EqualTo(10f).Within(0.01f));
            Assert.That(stats.Low1Fps, Is.EqualTo(100f).Within(0.5f));
            Assert.That(stats.Frames, Is.EqualTo(700)); // the live fps still counts every frame
        });
    }

    [TestCase(0)]
    [TestCase(99)]
    [TestCase(100)]
    [TestCase(999)]
    [TestCase(1000)]
    public void LowsAppearOnlyOnceTheirWindowHasFrames(int frames)
    {
        var stats = Filled(frames);
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(frames));
            Assert.That(stats.WorstMs, frames > 0 ? Is.Not.NaN : Is.NaN);
            Assert.That(stats.Low1Fps, frames >= 100 ? Is.Not.NaN : Is.NaN);
            Assert.That(stats.Low01Fps, frames >= 1000 ? Is.Not.NaN : Is.NaN);
        });
    }

    // The worst frame and the pause it was recorded with, out of the same window the lows come from
    [Test]
    public void TheWorstFrameIsTheOneTheLowsAreTakenFrom()
    {
        var stats = new FrameStats();
        for (var i = 0; i < 1998; i++) stats.Record(0.01f, 0.5f, true);
        stats.Record(0.2f, 12f, true); // one 200 ms hitch
        stats.Record(0.01f, 40f, true); // a big pause in a short frame is not the worst frame's
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(History));
            Assert.That(stats.WorstMs, Is.EqualTo(200f).Within(0.01f));
            Assert.That(stats.WorstGcMs, Is.EqualTo(12f).Within(0.001f));
            // the worst two frames: 200 ms and 10 ms
            Assert.That(stats.Low01Fps, Is.EqualTo(1000f / 105f).Within(0.5f));
            // the worst twenty: one 200 ms and nineteen 10 ms
            Assert.That(stats.Low1Fps, Is.EqualTo(1000f / 19.5f).Within(0.5f));
        });
    }

    [Test]
    public void TheRingKeepsOnlyTheLastHistoryFrames()
    {
        var stats = new FrameStats();
        stats.Record(0.5f, 0, true); // falls out again
        for (var i = 0; i < History; i++) stats.Record(0.01f, 0, true);
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.EqualTo(History));
            Assert.That(stats.WorstMs, Is.EqualTo(10f).Within(0.01f));
        });
    }

    [Test]
    public void ResetHistoryStartsTheWindowOver()
    {
        var stats = Filled(1500, 50f);
        stats.ResetHistory();
        stats.SampleWindow();
        Assert.Multiple(() =>
        {
            Assert.That(stats.Recorded, Is.Zero);
            Assert.That(stats.WorstMs, Is.NaN);
            Assert.That(stats.Low01Fps, Is.NaN);
            Assert.That(stats.HistoryMs(History - 1), Is.Zero);
        });
    }

    // The engine's int counter wraps after hours of drawing and Alt+F3 resets it; neither may stop the lows or report a negative rate
    [TestCase(int.MaxValue - 500, int.MinValue + 999, 15)] // 1500 draws across the wrap
    [TestCase(5_000_000, 300, 3)] // reset, then 300 draws
    public void TheDrawCallCounterWrappingOrResetKeepsTheLows(int before, int after, int perFrame)
    {
        var saved = RuntimeStats.drawCallsCount;
        try
        {
            RuntimeStats.drawCallsCount = before;
            var stats = Filled(100);
            stats.Reset();
            stats.ResetHistory();
            for (var i = 0; i < 100; i++) stats.Record(0.02f, 0, true);
            RuntimeStats.drawCallsCount = after;
            stats.SampleWindow();
            Assert.Multiple(() =>
            {
                Assert.That(stats.DrawCallsPerFrame, Is.EqualTo(perFrame));
                Assert.That(stats.Low1Fps, Is.EqualTo(50f).Within(0.5f), "the window after the counter's jump");
            });
        }
        finally
        {
            RuntimeStats.drawCallsCount = saved;
        }
    }

    // IconBudget and HangWatch count frames with the HUD off too: Completed stood still there, the creative inventory stayed empty
    [Test]
    public void FramesCountWithoutTheHud() => Clock.With(false, null, () =>
    {
        var (frames, completed) = (FrameClock.Frames, FrameClock.Completed);
        FrameClock.Begin();
        FrameClock.Begin();
        Assert.That((FrameClock.Frames, FrameClock.Completed), Is.EqualTo((frames + 2, completed)));
    });

    // Read in the Ortho stage, the dt that ended at this frame's start pairs with the GC pause since the previous Ortho, so a collection
    // early in a frame lands on the one before. FrameClock cuts both at frame starts.
    [Test]
    public void EachFrameCarriesItsOwnGcPause()
    {
        Clock.With(true, null, () =>
        {
            var records = new List<FrameRecord>();
            TimeSpan own = default;
            FrameClock.Begin();
            for (var frame = 0; frame < 4; frame++)
            {
                var completed = FrameClock.Completed;
                if (frame == 2) own = Busy.Collect(); // inside the third frame, early, well before any Ortho stage
                Busy.Spin(frame == 2 ? 30 : 5);
                FrameClock.End();
                FrameClock.Begin();
                Assert.That(FrameClock.Completed, Is.EqualTo(completed + 1));
                records.Add(FrameClock.Last);
            }

            var history = new FrameStats();
            foreach (var record in records) history.Record((float)(record.DtMs / 1000), (float)record.GcMs, true);
            history.SampleWindow();
            Assert.Multiple(() =>
            {
                Assert.That(records[2].GcMs, Is.GreaterThanOrEqualTo(own.TotalMilliseconds - 1e-6),
                    "the collection belongs to its own frame");
                Assert.That(records[1].GcMs, Is.LessThan(own.TotalMilliseconds), "not to the frame before it");
                Assert.That(history.WorstMs, Is.EqualTo(records[2].DtMs).Within(0.01),
                    "the long frame is the one with the collection");
                Assert.That(history.WorstGcMs, Is.EqualTo(records[2].GcMs).Within(0.001));
                Assert.That(records.Select(record => record.OutsideMs), Is.All.GreaterThanOrEqualTo(0),
                    "End() ran for every frame");
                Assert.That(records.Select(record => record.Root), Is.All.Null, "no profiler, no profile");
            });
        });
    }

    // An exception inside the frame skips the postfix: the frame still counts, only the split between frames is unknown
    [Test]
    public void AFrameWithoutItsPostfixKeepsItsDt()
    {
        Clock.With(true, ScreenManager.FrameProfiler, () =>
        {
            FrameClock.Begin();
            Busy.Spin(2);
            FrameClock.Begin(); // no End() in between
            Assert.Multiple(() =>
            {
                Assert.That(FrameClock.Last.DtMs, Is.GreaterThan(1));
                Assert.That(FrameClock.Last.OutsideMs, Is.NaN);
            });
        });
    }

    [Test]
    public void TheClockStandsStillWhileNobodyLooks()
    {
        Clock.With(false, ScreenManager.FrameProfiler, () =>
        {
            var completed = FrameClock.Completed;
            for (var i = 0; i < 3; i++)
            {
                FrameClock.Begin();
                FrameClock.End();
            }

            Assert.That(FrameClock.Completed, Is.EqualTo(completed));
        });
    }
}

// FrameClock's switches and the engine's profiler as a test sets them, put back afterwards: no sink, a fresh frame start
internal static class Clock
{
    public static void With(bool stats, FrameProfilerUtil? profiler, Action body)
    {
        var saved = (FrameClock.Stats, ScreenManager.FrameProfiler);
        (FrameClock.Stats, ScreenManager.FrameProfiler) = (stats, profiler!);
        try
        {
            body();
        }
        finally
        {
            (FrameClock.Stats, ScreenManager.FrameProfiler, FrameClock.Sink) = (saved.Stats, saved.FrameProfiler, null);
            FrameClock.Begin();
        }
    }
}

public sealed class DrmMemoryTests
{
    // amdgpu's fdinfo: the client id, its VRAM and GTT; a file without a client id is no client
    [Test]
    public void ParsesAClient()
    {
        var client = DrmMemory.Parse(["pos:\t0", "drm-driver:\tamdgpu", "drm-client-id:\t42", "drm-memory-vram:\t6291456 KiB",
            "drm-memory-gtt:\t\t20480 KiB"]);
        Assert.That(client, Is.EqualTo(new DrmMemory.Client(42, "amdgpu", 6291456, 20480)));
        Assert.That(DrmMemory.Parse(["pos:\t0", "flags:\t02"]), Is.Null);
        Assert.That(DrmMemory.Text([client!.Value, new DrmMemory.Client(7, "amdgpu", 1024 * 1024, 0)], 6100),
            Is.EqualTo("Vulkan 6144 MB (GTT 20 MB), OpenGL 1024 MB (GTT 0 MB)"));
    }
}
