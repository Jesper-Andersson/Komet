using System.Text;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Hud;

namespace Komet.Test.Hud;

// The HUD's report rows: the bench's means per row, the debug protocol's HUD section, the rows' growth over their own reads
[NonParallelizable]
public sealed class HudReportTests
{
    private const string Build = "v1.2.3", Newer = "v1.3.0";
    private static readonly string Installed = new('a', 64), Other = new('b', 64);

    [TearDown]
    public void Detach()
    {
        Contracts.Attach(null);
    }

    [TestCase(double.PositiveInfinity, "F1", "")]
    [TestCase(double.NegativeInfinity, "N0", "")]
    [TestCase(double.NaN, "F2", "")]
    [TestCase(1234.4, "N0", "1,234")]
    [TestCase(9.94, "F2", "9.94")]
    [TestCase(0.0, "F1", "0.0")] // a measured zero is a number
    public void NumbersPrintCultureInvariantAndNothingWhenNotFinite(double value, string format, string expected)
    {
        Assert.That(HudText.Format(value, format), Is.EqualTo(expected));
    }

    [Test]
    public void HeadersAndTitlesAreQuotedAsTheyAreNow()
    {
        var name = "passes";
        var header = new HudLine { Kind = HudLineKind.Header, Label = () => name };
        var title = new HudLine { Kind = HudLineKind.Title, Label = () => "Komet", Badge = () => "Release 1.2.3" };
        name = "changed";
        Assert.Multiple(() =>
        {
            Assert.That(header.MeanText(), Is.EqualTo("[changed]"), "the bench report reads the label as it is");
            Assert.That(title.MeanText(), Is.EqualTo("Komet – Release 1.2.3"));
        });
    }

    // FrameStats of a window without frames: no rate at all rather than 0 fps, and the share of a mod over it is no number either
    [Test]
    public void AnEmptyWindowClaimsNoRate()
    {
        var frames = new FrameStats();
        var fps = new HudLine { Label = () => "FPS", Value = () => frames.Fps };
        var average = new HudLine { Label = () => "Frametime", Value = () => frames.AverageMs, Unit = "ms" };
        Assert.Multiple(() =>
        {
            Assert.That(frames.Fps, Is.NaN);
            Assert.That(frames.AverageMs, Is.NaN);
            Assert.That(Shown(fps), Is.Empty, "the row is left out without a number");
            Assert.That(Shown(average), Is.Empty);
        });

        frames.Record(0.02f, 0, true);
        frames.Record(0.02f, 0, true);
        Assert.Multiple(() =>
        {
            Assert.That(Shown(fps), Is.EqualTo("FPS: 50"));
            Assert.That(Shown(average), Is.EqualTo("Frametime: 20.00 ms"));
        });
    }

    [Test]
    public void ANumberThatIsNotFiniteIsLeftOut()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Shown(new HudLine
                    { Label = () => "mod", Value = () => 9.94, Percent = () => double.PositiveInfinity, Unit = "ms" }),
                Is.EqualTo("mod: 9.94 ms"), "no bare ' %'");
            Assert.That(Shown(new HudLine { Label = () => "CPU", Percent = () => double.NaN }), Is.Empty);
            Assert.That(Shown(new HudLine
                { Label = () => "VRAM", Value = () => double.NaN, Percent = () => double.NaN, Unit = "MB" }), Is.Empty);
            Assert.That(Shown(new HudLine { Label = () => "update check failed", Sub = true }),
                Is.EqualTo("  update check failed"), "a line of text has no number to miss");
        });
    }

    [TestCase(null, null, "  label")]
    [TestCase("3.50", null, "  label: 3.50 ms")]
    [TestCase("3.50", "12.5", "  label: 12.5 % 3.50 ms")]
    [TestCase(null, "12.5", "  label: 12.5 %")]
    [TestCase("", "12.5", "  label: 12.5 %")]
    [TestCase("", "", "")]
    [TestCase(null, "", "")]
    [TestCase("3.50", "", "  label: 3.50 ms")]
    public void RowsFormatAsTheReportPrintsThem(string? value, string? percent, string expected)
    {
        Assert.That(HudLine.RowText("label", true, value, percent, "ms"), Is.EqualTo(expected));
    }

    // A bench averages the windows that had a number; a statistic that never had one in the whole bench is left out without
    // tripping an assertion
    [Test]
    public void ABenchMeanSkipsWindowsWithoutANumber()
    {
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        var current = 0.0;
        var line = new HudLine { Label = () => "pause", Value = () => current, Unit = "ms" };
        var share = new HudLine { Label = () => "mod", Value = () => 2, Percent = () => current, Unit = "ms" };
        var never = new HudLine { Label = () => "never", Value = () => double.NaN, Unit = "ms" };
        // a share known in fewer windows than its value (no frame time to divide by): each mean is over its own windows
        var partly = new HudLine
        {
            Label = () => "partly", Value = () => current, Percent = () => current > 5 ? current : double.NaN,
            Unit = "MB"
        };
        var shareless = new HudLine
        { Label = () => "time", Value = () => current, Percent = () => double.NaN, Unit = "ms" };
        foreach (var sample in (double[])[double.NaN, 4, double.PositiveInfinity, 6])
        {
            current = sample;
            foreach (var row in (HudLine[])[line, share, never, partly, shareless]) row.Accumulate();
        }

        Assert.Multiple(() =>
        {
            Assert.That(line.MeanText(), Is.EqualTo("pause: 5.00 ms"));
            Assert.That(share.MeanText(), Is.EqualTo("mod: 5.0 % 2.00 ms"));
            Assert.That(never.MeanText(), Is.Empty);
            Assert.That(partly.MeanText(), Is.EqualTo("partly: 6.0 % 5 MB"),
                "the time of a window without a share counts");
            Assert.That(shareless.MeanText(), Is.EqualTo("time: 5.00 ms"),
                "a row whose share never came keeps its time");
            Assert.That(logger.Lines, Is.Empty, "no assertion failed");
        });
    }

    // The means of a section's rows: a header in brackets, a row without any number left out, a blank line before a later section
    [Test]
    public void ASectionPrintsTheMeansOfItsRows()
    {
        var panel = new HudPanel().Section("x").Value("y", () => 3.5, "ms").Line(() => "unmeasured", () => double.NaN, "ms");
        panel.Accumulate();
        var text = new StringBuilder("[title]");
        panel.AppendMeans(text);
        Assert.That(text.ToString(), Does.Match(@"^\[title\]\n\n\[.*\]\n.*: 3\.50 ms$"));
    }

    // Counting starts (F7, the counters switch, the first showing, a bench with the HUD hidden) and the rows take a new baseline: the first
    // refresh shows the span since instead of nothing, and the prime is no bench sample, so the bench's first interval is its own sample
    [Test]
    public void APrimedRowCoversTheSpanSinceCountingStarted()
    {
        var (line, add) = PerFrame();
        Counting.Hud = false;
        Counting.Hud = true;
        line.Prime();
        add(40, 10);
        var shown = Shown(line);
        Counting.Hud = false;
        line.ResetBench(); // StartBench's order: the reset, then counting starts
        Counting.Hud = true;
        line.Prime();
        foreach (var (added, count) in ((double, double)[])[(60, 20), (10, 10)])
        {
            add(added, count);
            line.Accumulate();
        }

        Counting.Hud = false;
        Assert.Multiple(() =>
        {
            Assert.That(shown, Is.EqualTo("per frame: 4.00 ms"));
            Assert.That(line.MeanText(), Is.EqualTo("per frame: 2.00 ms"), "3 and 1 per frame");
        });
    }

    // A row divides the growth of a feature's total by the growth of the frames or seconds since its own last read. Neither the first
    // read nor one across a pause of the HUD's counting (the HUD-only totals stood still while its frames went on, or the HUD was hidden
    // while a scripted bench kept the tessellation totals going), nor one after the totals restarted with a new world is a rate.
    [Test]
    public void GrowthIsTheSpanSinceTheRowLastRead()
    {
        var total = 10.0;
        var growth = new Growth(() => total);
        var first = growth.Next();
        total = 25;
        var grown = growth.Next();
        Counting.Hud = false; // a pause
        Counting.Hud = true;
        total = 40;
        var paused = growth.Next();
        total = 45;
        var again = growth.Next();
        total = 3;
        var restarted = growth.Next();
        Counting.Bench = true;
        Counting.Hud = false; // hidden, the bench counts on
        total = 30;
        Counting.Hud = true;
        var hidden = growth.Next();
        (Counting.Hud, Counting.Bench) = (false, false);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.NaN);
            Assert.That(grown, Is.EqualTo(15));
            Assert.That(paused, Is.NaN);
            Assert.That(again, Is.EqualTo(5));
            Assert.That(restarted, Is.NaN);
            Assert.That(hidden, Is.NaN);
        });
    }

    // A failure is reported once per call site, and a call site is its file too: Install at line 25 of two features are two sites
    [Test]
    public void AFailureIsReportedOncePerFileMemberAndLine()
    {
        var logger = new CapturingLogger();
        Contracts.Attach(logger);
        foreach (var file in (string[])["/src/SunOcclusion.cs", "/src/WindowSizeCache.cs", "/src/SunOcclusion.cs"])
            _ = Contracts.Assert(false, "harmony is null", "Install", 25, file);
        Assert.That(logger.Lines, Has.Count.EqualTo(2).And.Some.Contains("WindowSizeCache.cs, Install (line 25)"));
    }

    [TestCase(true, Build, "a", "a", "Verified", "aaaaaaa")]
    [TestCase(true, Build, "a", "b", "Mismatch", "aaaaaaa")]
    [TestCase(true, Build, "a", "", "Unverified", Build)]
    [TestCase(true, Build, "", "a", "Unverified", Build)]
    [TestCase(true, Newer, "a", "a", "Outdated", Newer)]
    [TestCase(true, Newer, "a", "b", "Mismatch", "aaaaaaa")]
    [TestCase(false, Newer, "a", "", "Outdated", Newer)]
    [TestCase(false, "", "", "", "NoRelease", Build)]
    [TestCase(false, "v1.2.2", "a", "", "NoRelease", Build)]
    [TestCase(true, "v1.2.2", "a", "a", "Verified", "aaaaaaa")]
    public void GitHubsAnswerMapsToOneState(bool listed, string newest, string installed, string published,
        string state, string detail)
    {
        var report = UpdateCheck.Judge(Build, listed, newest, Hash(installed), listed ? Hash(published) : "", "");
        Assert.Multiple(() =>
        {
            Assert.That(report.State, Is.EqualTo(Enum.Parse<UpdateState>(state)));
            Assert.That(report.Detail, Is.EqualTo(detail));
            Assert.That(report.Tag, Is.EqualTo(listed ? Build : ""));
        });
    }

    // Only a later build is an update: a pre-release is ahead of the release before it and behind its own; previews have no order
    [TestCase("v2.0.1-pre", "v2.0.0", false)]
    [TestCase("v2.0.1-pre", "v2.0.1", true)]
    [TestCase("v2.0.1-pre", "v2.0.1-rc", true)]
    [TestCase("v2.0.1", "v2.0.1-pre", false)]
    [TestCase("v2.0.10", "v2.0.9", false)]
    [TestCase("v2.0.9", "v2.0.10", true)]
    [TestCase("v2.0.0", "v2.0.0", false)]
    [TestCase("v2.0.0", "", false)]
    [TestCase("preview-abc1234", "preview-def5678", true)]
    public void TheNewestTagIsAnUpdateOnlyWhenItIsLater(string tag, string newest, bool behind)
    {
        Assert.That(UpdateCheck.Behind(tag, newest), Is.EqualTo(behind));
    }

    [TestCase("Checking", "v1", "a", "a", "verify-checking")]
    [TestCase("Failed", "v1", "a", "a", "verify-failed")]
    [TestCase("Verified", "v1", "a", "a", "verify-match")]
    [TestCase("Outdated", "v1", "a", "b", "verify-mismatch")]
    [TestCase("Unverified", "", "a", "", "verify-norelease")]
    [TestCase("Unverified", "v1", "", "b", "verify-nofile")]
    [TestCase("Unverified", "v1", "a", "", "verify-nochecksum")]
    public void TheChecksumWindowNamesWhatTheComparisonFound(string state, string tag, string installed,
        string published, string key)
    {
        var report = new UpdateReport(Enum.Parse<UpdateState>(state), "", tag, "", Hash(installed), Hash(published));
        Assert.That(report.Verdict(), Is.EqualTo(key));
    }

    // add grows the total and the frames
    private static (HudLine Line, Action<double, double> Add) PerFrame()
    {
        double total = 0, frames = 0;
        Growth grown = new(() => total), of = new(() => frames);
        return (new HudLine { Label = () => "per frame", Value = () => grown.Next() / of.Next(), Unit = "ms" },
            (added, count) => (total, frames) = (total + added, frames + count));
    }

    // One interval's reading as the report prints it
    private static string Shown(HudLine line)
    {
        line.ResetBench();
        line.Accumulate();
        return line.MeanText();
    }

    private static string Hash(string digit) => digit switch { "" => "", "a" => Installed, _ => Other };
}
