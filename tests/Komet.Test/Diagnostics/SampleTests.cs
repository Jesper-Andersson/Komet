using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

// What the HUD samples once a second off the main thread: /proc read into a buffer of its own, a log parsed again only once it grew
public sealed class SampleTests
{
    private const int Samples = 20;

    [Test]
    public void TheSystemSampleReadsWhatTheProcessAndProcSay()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("/proc");
        var stats = new ResourceStats();
        stats.Sample();
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Samples; i++) stats.Sample();
        var perSample = (GC.GetAllocatedBytesForCurrentThread() - start) / Samples;
        var line = File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
        var totalKb = long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);
        using var process = Process.GetCurrentProcess();
        Assert.Multiple(() =>
        {
            Assert.That(perSample, Is.LessThan(2048), "was ~44 KB: Process.Refresh, a StreamReader and a Split per line");
            Assert.That(stats.TotalRamMb, Is.EqualTo(totalKb / 1024));
            Assert.That(stats.AvailableRamMb, Is.InRange(1, stats.TotalRamMb));
            Assert.That(stats.WorkingSetMb, Is.EqualTo(process.WorkingSet64 / (1024 * 1024)).Within(16));
            Assert.That(stats.SystemCpuPercent, Is.InRange(0.0, 100.0));
            Assert.That(stats.CpuPercent, Is.GreaterThanOrEqualTo(0.0));
        });
    }

    [Test]
    public void ALogIsParsedAgainOnlyOnceItChanged()
    {
        var path = Path.Combine(Path.GetTempPath(), $"komet-logstats-{Environment.ProcessId}.log");
        try
        {
            File.WriteAllText(path, "cut\n1.1.2026 10:00:00 [Notification] first\n1.1.2026 10:00:01 [Warning] second\n");
            var log = new LogStats("client-main.log");
            AccessTools.Field(typeof(LogStats), "_path").SetValue(log, path);
            log.Sample();
            var shown = (log.Entry(0), log.Entry(1));
            var start = GC.GetAllocatedBytesForCurrentThread();
            log.Sample();
            var unchanged = GC.GetAllocatedBytesForCurrentThread() - start;
            File.AppendAllText(path, "1.1.2026 10:00:02 [Error] third\n");
            log.Sample();
            Assert.Multiple(() =>
            {
                Assert.That(shown, Is.EqualTo(((LogEntry?)new LogEntry("10:00:01", "", LogLevel.Warning, "second"),
                    (LogEntry?)new LogEntry("10:00:00", "", LogLevel.Info, "first"))));
                Assert.That(unchanged, Is.LessThan(1024), "the same tail is not decoded and split again");
                Assert.That(log.Entry(0), Is.EqualTo((LogEntry?)new LogEntry("10:00:02", "", LogLevel.Error, "third")));
            });
        }
        finally
        {
            File.Delete(path);
        }
    }
}
