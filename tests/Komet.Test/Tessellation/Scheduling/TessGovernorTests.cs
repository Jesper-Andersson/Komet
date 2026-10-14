using Komet.Tessellation;

namespace Komet.Test.Tessellation;

public sealed class TessGovernorTests
{
    [TestCase(4, 8, 0.1, 500, 4, 5, TestName = "calm, a backlog, all busy: one more")]
    [TestCase(8, 8, 0.1, 500, 8, 8, TestName = "never past the ceiling")]
    [TestCase(4, 8, 0.1, 500, 2, 4, TestName = "threads idle: no more")]
    [TestCase(4, 8, 0.1, 10, 4, 4, TestName = "a small backlog: no more")]
    [TestCase(4, 8, 0.5, 500, 4, 4, TestName = "between calm and pressed: holds")]
    [TestCase(4, 8, 1.5, 500, 4, 3, TestName = "the main thread waits: one less")]
    [TestCase(8, 8, 6.0, 500, 8, 4, TestName = "it waits long: half")]
    [TestCase(1, 8, 9.0, 500, 1, 1, TestName = "at least one while there is work")]
    [TestCase(3, 8, 0.1, 0, 0, 2, TestName = "no backlog: eases back")]
    [TestCase(6, 2, 0.1, 500, 6, 2, TestName = "a lowered ceiling takes effect at once")]
    [TestCase(3, 0, 0.1, 500, 3, 0, TestName = "a ceiling of none: none")]
    public void TheLimitFollowsTheMainThread(int limit, int ceiling, double waitMs, int backlog, int busy, int next) =>
        Assert.That(TessGovernor.Decide(limit, ceiling, waitMs, (backlog, busy)), Is.EqualTo(next));

    [Test]
    public void UnmeasuredTheCeilingStands()
    {
        TessGovernor.Reset();
        Assert.That(TessGovernor.Limit(6), Is.EqualTo(6));
    }
}
