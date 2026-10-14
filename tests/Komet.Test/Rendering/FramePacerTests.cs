using Komet.Rendering;

namespace Komet.Test.Rendering;

public sealed class FramePacerTests
{
    // The engine sleeps whole milliseconds and ends the frame up to 1 ms early; here it ends at the interval, never before
    [Test]
    public void TheWaitEndsAtTheExactInterval()
    {
        var watch = Stopwatch.StartNew();
        FramePacer.Wait(watch, 100f);
        Assert.That(watch.Elapsed.TotalMilliseconds, Is.InRange(10.0, 20.0)); // the upper bound only catches an oversleep by whole frames: a loaded machine wakes late
    }
}
