using Komet.Rendering;

namespace Komet.Test.Rendering;

// The engine's two error checks a frame ask the driver every EveryCalls-th time, and every time while switched off or in GL debug mode
public sealed class GlErrorPollTests
{
    [TearDown]
    public void Restore() => GlErrorPoll.Enabled = true;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-glerrorpoll");
        GlErrorPoll.Install(harmony);
        Assert.That(GlErrorPoll.Rewritten, Is.True,
            "RenderAfterFinalComposition or RenderAfterBlit no longer make exactly one CheckGlErrorAlways call");
    }

    [Test]
    public void OnlyEveryNthCallAsks()
    {
        using var harmony = new TestHarmony("komet-test-glerrorpoll");
        GlErrorPoll.Install(harmony); // counts from zero
        var asked = Enumerable.Range(0, 10 * GlErrorPoll.EveryCalls).Select(_ => GlErrorPoll.Ask(false)).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(asked.Count(a => a), Is.EqualTo(10));
            Assert.That(asked[GlErrorPoll.EveryCalls - 1], Is.True, "the EveryCalls-th call asks");
        });
    }

    [TestCase(false, false, TestName = "SwitchedOffEveryCallAsks")]
    [TestCase(true, true, TestName = "InDebugModeEveryCallAsks")]
    public void EveryCallAsks(bool enabled, bool debugMode)
    {
        GlErrorPoll.Enabled = enabled;
        Assert.That(Enumerable.Range(0, 3 * GlErrorPoll.EveryCalls).All(_ => GlErrorPoll.Ask(debugMode)), Is.True);
    }
}
