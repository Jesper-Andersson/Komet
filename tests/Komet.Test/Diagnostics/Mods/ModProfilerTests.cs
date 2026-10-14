using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

// /komet profile patches a mod's entry points, books every call to its method and takes the patches out again
[NonParallelizable]
public sealed class ModProfilerTests
{
    private sealed class Probe : ModSystem
    {
        private static byte[] _kept = [];

        public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client && Outer() > 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Outer() => Inner() + Inner() + 1;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Inner()
        {
            Thread.SpinWait(2000);
            _kept = new byte[1000];
            return _kept.Length / 1000;
        }
    }

    [Test]
    public void EntryPointsAreOverridesOfTheGamesVirtuals()
    {
        var found = ModProfiler.Candidates(typeof(ModProfilerTests).Assembly);
        Assert.That(found, Does.Contain(typeof(Probe).GetMethod(nameof(Probe.ShouldLoad), [typeof(EnumAppSide)])));
        Assert.That(found, Does.Not.Contain(typeof(Probe).GetMethod(nameof(Probe.Inner))), "not called by the game");
        Assert.That(ModProfiler.Candidates(typeof(ModSystem).Assembly), Is.Empty, "the game is not a mod");
    }

    [Test]
    public void CallsAreBookedWithTheirSelfTimeAndThePatchesGoAgain()
    {
        var (outer, inner) = (typeof(Probe).GetMethod(nameof(Probe.Outer))!, typeof(Probe).GetMethod(nameof(Probe.Inner))!);
        var profile = ModProfiler.Begin("probe", "1.0", [outer, inner], 3600)!;
        for (var i = 0; i < 4 && profile.State == ModProfiler.Phase.Patching; i++) profile.Step();
        Assert.That(profile.State, Is.EqualTo(ModProfiler.Phase.Measuring));
        for (var i = 0; i < 5; i++) _ = Probe.Outer();
        profile.Stop();
        var report = profile.Report();
        Assert.Multiple(() =>
        {
            Assert.That(ModProfiler.Running, Is.False);
            Assert.That(Harmony.GetPatchInfo(outer)?.Owners ?? [], Does.Not.Contain(ModProfiler.HarmonyId));
            Assert.That(report, Does.Match(@"Probe\.Outer\s+5\s"));
            Assert.That(report, Does.Match(@"Probe\.Inner\s+10\s"));
            Assert.That(profile.Top(2)[0].Name, Is.EqualTo("Probe.Inner"), "Outer's own time is small, Inner's is the spin");
        });
    }

    [Test]
    public void BytesGoToTheMethodThatAllocatedThemAndAFramesTimeToItsPeak()
    {
        var (outer, inner) = (typeof(Probe).GetMethod(nameof(Probe.Outer))!, typeof(Probe).GetMethod(nameof(Probe.Inner))!);
        var profile = ModProfiler.Begin("probe", "1.0", [outer, inner], 3600)!;
        for (var i = 0; i < 4 && profile.State == ModProfiler.Phase.Patching; i++) profile.Step();
        for (var i = 0; i < 200; i++) _ = Probe.Outer(); // the first calls JIT the patched bodies, which allocates
        profile.Step(); // the frame ends: its time goes to the peaks and the series
        profile.Stop();
        var rows = profile.Rows(2, "alloc");
        var summary = profile.Summary();
        Assert.Multiple(() =>
        {
            Assert.That(rows[0].Name, Is.EqualTo("Probe.Inner"));
            Assert.That(rows[0].BytesPerCall, Is.GreaterThanOrEqualTo(1000));
            Assert.That(rows[1].BytesPerCall, Is.LessThan(100), "Inner's arrays are not Outer's own");
            Assert.That(rows[0].PeakMs, Is.GreaterThan(0));
            Assert.That(summary.Series, Has.Length.EqualTo(1));
            Assert.That(summary.WorstMs, Is.GreaterThanOrEqualTo(rows[0].PeakMs * 0.999), "the series is float");
        });
    }

    [Test]
    public void CompilerNamesReadAsTheMethodTheyAreIn()
    {
        Action lambda = static () => { };
        Assert.That(ModProfiler.Pretty(lambda.Method), Is.EqualTo("ModProfilerTests.CompilerNamesReadAsTheMethodTheyAreIn (lambda)"));
    }
}
