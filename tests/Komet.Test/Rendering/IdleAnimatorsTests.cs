using Komet.Rendering;

namespace Komet.Test.Rendering;

// A render call the loops leave out is one the engine's OnRenderFrame would have returned from without doing anything: golden against
// the engine's own AnimationUtil.OnRenderFrame in every state that decides it, and AnimatableRenderer's ShouldRender
public sealed class IdleAnimatorsTests
{
    [TearDown]
    public void Restore() => IdleAnimators.Enabled = true;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-idleanimators");
        IdleAnimators.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(IdleAnimators.Matched, Is.True, "the OnRenderFrame bodies are not the ones verified");
            Assert.That(IdleAnimators.Rewritten, Is.True,
                "TriggerRenderStage no longer calls OnRenderFrame in two loops");
            Assert.That(IdleAnimators.Unmarked, Is.True,
                "the extended-debug loop no longer checks and marks after each renderer");
            Assert.That(IdleAnimators.Parks, Is.True, "the two loops are no longer the counted loops Skip opens");
            Assert.That(IdleAnimators.Blocked, Is.False);
        });
    }

    // Every combination of what the util's OnRenderFrame reads: an idle one does nothing there, any other one does something
    [Test]
    public void AnIdleUtilIsOneTheEngineWouldHaveReturnedFrom([Values] bool animator, [Values] bool renderer,
        [Values] bool active, [Values] bool running, [Values] bool shouldRender, [Values] bool stopped)
    {
        var util = new RecordingUtil();
        var recorder = (RecordingAnimator)RuntimeHelpers.GetUninitializedObject(typeof(RecordingAnimator));
        AccessTools.Field(typeof(AnimatorBase), "activeAnimCount").SetValue(recorder, running ? 1 : 0);
        (util.animator, util.renderer) = (animator ? recorder : null, renderer ? Renderer(shouldRender) : null);
        if (active) util.activeAnimationsByAnimCode["walk"] = new AnimationMetaData { Code = "walk" };
        AccessTools.Field(typeof(AnimationUtil), "stopRenderTriggered").SetValue(util, stopped);
        var idle = IdleAnimators.Idle(util);
        util.OnRenderFrame(0.016f, EnumRenderStage.Opaque); // the engine's body, unpatched
        Assert.That(recorder.Frames + util.Changes > 0, Is.EqualTo(!idle));
    }

    // The extended-debug loop of the patched TriggerRenderStage: an idle renderer is neither checked for a GL error nor marked, a
    // working one is called and marked; switched off, every renderer is called and marked as by the engine
    [TestCase(true, new[] { "ShadowFar-busy", "end" })]
    [TestCase(false, new[] { "ShadowFar-animatable", "ShadowFar-busy", "end" })]
    public void TheDebugLoopMarksOnlyRenderersThatRan(bool enabled, string[] marks)
    {
        using var harmony = new TestHarmony("komet-test-idleanimators-marks");
        IdleAnimators.Install(harmony, new QuietLogger());
        IdleAnimators.Enabled = enabled;
        var (platform, saved) = (ScreenManager.Platform, ScreenManager.FrameProfiler);
        var profiler = new FrameProfilerUtil(_ => { }) { Enabled = true };
        var calls = 0;
        var busy = Answers.Of<IRenderer>(new() { [nameof(IRenderer.OnRenderFrame)] = _ => calls++ });
        (ScreenManager.Platform, ScreenManager.FrameProfiler) = (Platform(), profiler);
        try
        {
            profiler.Begin();
            Events(Renderer(false), busy).TriggerRenderStage(EnumRenderStage.ShadowFar, 0.016f);
            profiler.End();
            Assert.Multiple(() =>
            {
                Assert.That(profiler.PrevRootEntry.Marks.Keys, Is.EquivalentTo(marks));
                Assert.That(calls, Is.EqualTo(1));
            });
        }
        finally
        {
            (ScreenManager.Platform, ScreenManager.FrameProfiler) = (platform, saved);
        }
    }

    // GlErrorChecking off: CheckGlError returns at once
    private static ClientPlatformWindows Platform() =>
        (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));

    private static ClientEventManager Events(params IRenderer[] renderers)
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.extendedDebugInfo = true;
        var events = (ClientEventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
        var stages = new List<RenderHandler>[Enum.GetValues<EnumRenderStage>().Length];
        for (var i = 0; i < stages.Length; i++) stages[i] = [];
        for (var i = 0; i < renderers.Length; i++)
            stages[(int)EnumRenderStage.ShadowFar].Add(new RenderHandler
            {
                Renderer = renderers[i],
                ProfilingName = "ShadowFar-" + (renderers[i] is AnimatableRenderer ? "animatable" : "busy")
            });
        AccessTools.Field(typeof(ClientEventManager), "game").SetValue(events, game);
        AccessTools.Field(typeof(ClientEventManager), "renderersByStage").SetValue(events, stages);
        return events;
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void AnAnimatableRendererIsIdleWhileItDoesNotRender(bool shouldRender, bool idle)
    {
        Assert.That(IdleAnimators.Idle(Renderer(shouldRender)), Is.EqualTo(idle));
    }

    // A renderer of any other kind, or one implementing IRenderer again, is always called
    [Test]
    public void OtherRenderersAreNeverIdle()
    {
        Assert.Multiple(() =>
        {
            Assert.That(IdleAnimators.Idle(new Reimplemented()), Is.False);
            Assert.That(IdleAnimators.Idle(Answers.Of<IRenderer>([])), Is.False);
            Assert.That(IdleAnimators.Idle(null), Is.False);
        });
    }

    private static AnimatableRenderer Renderer(bool shouldRender)
    {
        var renderer = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        renderer.ShouldRender = shouldRender;
        return renderer;
    }

    private sealed class RecordingUtil() : AnimationUtil(null, new Vec3d())
    {
        public int Changes { get; private set; }

        protected override void OnAnimationsStateChange(bool animsNowActive) => Changes++;
    }

    private sealed class RecordingAnimator() : AnimatorBase(null, [])
    {
        public int Frames { get; private set; }
        public override int MaxJointId => 0;

        public override void OnFrame(Dictionary<string, AnimationMetaData> activeAnimationsByAnimCode, float dt) =>
            Frames++;

        protected override void calculateMatrices(float dt)
        {
            // never stepped here
        }
    }

    // Implements IRenderer.OnRenderFrame itself: its calls are not the engine body's
    private sealed class Reimplemented() : AnimationUtil(null, new Vec3d()), IRenderer
    {
        void IRenderer.OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            // another body
        }
    }
}
