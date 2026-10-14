using System.Text;
using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

// The one schedstat parser behind FrameClock's run-queue column (field 1) and RenderCost's thread rows (field 0)
public sealed class SchedStatTests
{
    [TestCase("123456789 4242 17\n", SchedStat.Run, 123456789L)]
    [TestCase("123456789 4242 17\n", SchedStat.Wait, 4242L)]
    [TestCase("123456789 4242 17\n", 2, 17L)]
    [TestCase("0 0 0\n", SchedStat.Wait, 0L)]
    [TestCase("123456789 4242", SchedStat.Wait, -1L)] // cut short: no byte ends the field
    [TestCase("123456789", SchedStat.Run, -1L)]
    [TestCase("", SchedStat.Run, -1L)] // an ended thread
    [TestCase("", SchedStat.Wait, -1L)]
    [TestCase(" 12 3\n", SchedStat.Run, -1L)]
    [TestCase("12  3\n", SchedStat.Wait, -1L)]
    [TestCase("12 3 4\n", 3, -1L)]
    public void TheFieldIsTheDigitsBetweenItsSeparators(string text, int field, long expected)
    {
        Assert.That(SchedStat.Field(Encoding.ASCII.GetBytes(text), field), Is.EqualTo(expected));
    }

    [Test]
    public void TheCallingThreadsOwnFileReads()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("/proc is Linux's");
        using var file = File.OpenHandle("/proc/thread-self/schedstat");
        var (run, wait) = (SchedStat.Read(file, SchedStat.Run), SchedStat.Read(file, SchedStat.Wait));
        Assert.Multiple(() =>
        {
            Assert.That(run, Is.GreaterThan(0), "this thread has run");
            Assert.That(wait, Is.GreaterThanOrEqualTo(0));
            Assert.That(file.IsClosed, Is.False);
        });
    }
}
