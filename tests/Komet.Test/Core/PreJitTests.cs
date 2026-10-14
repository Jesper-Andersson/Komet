using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime;
using Komet.Core;

namespace Komet.Test.Core;

internal static class CctorProbe
{
    public static bool Ran;
}

internal static class WithCctor
{
    private static readonly int Seed;

    // Explicit on purpose: without beforefieldinit the runtime runs it at first access, and never earlier
    static WithCctor()
    {
        CctorProbe.Ran = true;
        Seed = 7;
    }

    public static int Work(int x)
    {
        var sum = Seed;
        for (var i = 0; i < x; i++) sum += (i * 3) ^ x;
        return sum;
    }
}

internal static class LazyProbe
{
    public static bool Ran;
}

// No explicit static constructor, so the compiler marks the type beforefieldinit: the runtime may run the initialiser any time before the
// first field access, and a JIT that initialised classes while compiling a reader would run it on the walker, ahead of the game
internal static class BeforeFieldInit
{
    public static readonly int Seed = Mark();

    private static int Mark()
    {
        LazyProbe.Ran = true;
        return 5;
    }
}

internal static class ReadsBeforeFieldInit
{
    public static int Read() => BeforeFieldInit.Seed + 1;
}

internal static class GenericProbe
{
    public static int Plain() => 1;

    public static int Generic<T>() => typeof(T).Name.Length;

    public static int Patched() => 2;
}

internal static class GenericHost<T>
{
    public static int Plain() => typeof(T).Name.Length;

    public static class Nested
    {
        public static int Inner() => 3;
    }
}

internal abstract class AbstractProbe
{
    public abstract int Missing();
}

// The walk runs against the game's real assemblies: a game update that adds a method shape PrepareMethod cannot take shows up as a
// failure count here instead of an exception on a thread in the game. They resolve from the installation as in the game
// (GameAssemblies); otherwise every method touching cairo, OpenTK.Graphics or SQLite would count as failed.
[NonParallelizable]
public sealed class PreJitTests
{
    [SetUp]
    public void Arm()
    {
        PreJit.Reset();
        PreJit.Enabled = true;
    }

    [TearDown]
    public void Disarm() => PreJit.Reset();

    // First in the fixture: the walks below compile the API too, and a compiled method is not compiled again
    [Test]
    [Order(1)]
    [Category("Slow")]
    public void WalksTheGameApi()
    {
        var api = typeof(Shape).Assembly;
        var compiled = JitInfo.GetCompiledMethodCount(true);
        PreJit.Walk(api, PreJit.PatchedSet());
        var tried = PreJit.Prepared + PreJit.Failed;
        Assert.Multiple(() =>
        {
            Assert.That(PreJit.Prepared, Is.GreaterThan(7000));
            Assert.That(PreJit.Failed, Is.LessThan(tried / 100), "more than 1 % of the API did not compile");
            Assert.That(JitInfo.GetCompiledMethodCount(true) - compiled, Is.GreaterThan(1000),
                "PrepareMethod compiled nothing");
        });
    }

    [Test]
    public void LeavesStaticConstructorsUnrun()
    {
        PreJit.Walk(typeof(PreJitTests).Assembly, new HashSet<(Guid, int)>());
        var work = typeof(WithCctor).GetMethod(nameof(WithCctor.Work))!;
        Assert.Multiple(() =>
        {
            Assert.That(PreJit.Prepared, Is.GreaterThan(10));
            Assert.That(PreJit.Prepare(work.Module, work.MetadataToken), Is.True);
            Assert.That(CctorProbe.Ran, Is.False, "PrepareMethod ran a static constructor");
        });
        Assert.That(WithCctor.Work(3), Is.GreaterThan(0));
        Assert.That(CctorProbe.Ran, Is.True, "the probe itself is broken");
    }

    // The explicit static constructor above is never run at JIT time by construction; this is the case where a JIT may
    [Test]
    public void LeavesBeforeFieldInitInitialisersUnrun()
    {
        var read = typeof(ReadsBeforeFieldInit).GetMethod(nameof(ReadsBeforeFieldInit.Read))!;
        var mark = AccessTools.Method(typeof(BeforeFieldInit), "Mark");
        Assert.Multiple(() =>
        {
            Assert.That(typeof(BeforeFieldInit).Attributes.HasFlag(TypeAttributes.BeforeFieldInit), Is.True,
                "the probe needs beforefieldinit");
            Assert.That(PreJit.Prepare(read.Module, read.MetadataToken), Is.True);
            Assert.That(PreJit.Prepare(mark.Module, mark.MetadataToken), Is.True);
            Assert.That(LazyProbe.Ran, Is.False, "compiling a reader of a beforefieldinit static ran its initialiser");
        });
        Assert.That(ReadsBeforeFieldInit.Read(), Is.EqualTo(6));
        Assert.That(LazyProbe.Ran, Is.True, "the probe itself is broken");
    }

    [Test]
    [Category("Slow")]
    public void StartCompilesTheLoadedEngineOnceOnItsOwnThread()
    {
        // VintagestoryLib with the API; VSEssentials only if another fixture loaded it, the other mods are absent
        _ = typeof(ClientMain).Assembly;
        var logger = new CapturingLogger();
        var niceBefore = Nice();
        PreJit.Enabled = false;
        PreJit.Start(logger);
        Assert.Multiple(() =>
        {
            Assert.That(Walker(), Is.Null.Or.Property(nameof(Thread.IsAlive)).False,
                "switched off at join, yet a walk started");
            Assert.That(PreJit.State, Is.EqualTo(PreJitState.Idle));
        });

        PreJit.Enabled = true;
        PreJit.Start(logger);
        Assert.That(Walker()?.Join(TimeSpan.FromSeconds(120)), Is.True);
        var prepared = PreJit.Prepared;
        Assert.Multiple(() =>
        {
            Assert.That(PreJit.State, Is.EqualTo(PreJitState.Done));
            Assert.That(Walker()!.Name, Is.EqualTo(PreJit.ThreadName));
            Assert.That(prepared, Is.GreaterThan(20000), "the API and VintagestoryLib together");
            Assert.That(PreJit.Failed, Is.LessThan(prepared / 50),
                "CI carries only the referenced part of Lib, locally it is ~1");
            Assert.That(PreJit.JitMs, Is.GreaterThan(0).And.LessThanOrEqualTo(PreJit.WallMs));
            Assert.That(Nice(), Is.EqualTo(niceBefore), "the priority of the calling thread changed");
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("pre-compiled"));
        });
        // Without CAP_SYS_NICE setpriority can lower a priority but not raise it: a walker started at a lower one may stay there
        var niced = logger.Lines[0].Contains("at nice", StringComparison.Ordinal);
        if (!OperatingSystem.IsLinux()) Assert.That(niced, Is.False, "setpriority is only called on Linux");
        else if (niceBefore <= PreJit.Niceness) Assert.That(niced, Is.True, "the walker lowers its own priority");

        PreJit.Start(logger);
        Assert.Multiple(() =>
        {
            Assert.That(Walker()!.IsAlive, Is.False, "a second world join walked again");
            Assert.That(PreJit.Prepared, Is.EqualTo(prepared));
            Assert.That(logger.Lines, Has.Count.EqualTo(1));
        });

        static Thread? Walker() => AccessTools.Field(typeof(PreJit), "_thread").GetValue(null) as Thread;
    }

    [Test]
    public void SkipsGenericAbstractAndPatchedMethods()
    {
        using var harmony = new TestHarmony("komet-test-prejit");
        var patched = typeof(GenericProbe).GetMethod(nameof(GenericProbe.Patched))!;
        _ = harmony.Patch(patched, Foreign.Prefix);
        var set = PreJit.PatchedSet();
        using var pe = new PEReader(File.OpenRead(typeof(PreJitTests).Assembly.Location));
        var md = pe.GetMetadataReader();
        var id = typeof(PreJitTests).Module.ModuleVersionId;

        bool Eligible(MethodBase method) =>
            PreJit.Eligible(md, (MethodDefinitionHandle)MetadataTokens.EntityHandle(method.MetadataToken),
                id, set);

        Assert.Multiple(() =>
        {
            Assert.That(set, Does.Contain((id, patched.MetadataToken)));
            Assert.That(Eligible(typeof(GenericProbe).GetMethod(nameof(GenericProbe.Plain))!), Is.True);
            Assert.That(Eligible(typeof(GenericProbe).GetMethod(nameof(GenericProbe.Generic))!), Is.False,
                "generic method");
            Assert.That(Eligible(typeof(GenericHost<>).GetMethod("Plain")!), Is.False, "method of a generic type");
            Assert.That(Eligible(typeof(GenericHost<>.Nested).GetMethod("Inner")!), Is.False,
                "method of a type nested in a generic type");
            Assert.That(Eligible(typeof(AbstractProbe).GetMethod(nameof(AbstractProbe.Missing))!), Is.False, "no IL");
            Assert.That(Eligible(patched), Is.False, "patched by Harmony");
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public void CancelOrSwitchOffBeforeTheWalkPreparesNothing(bool cancel)
    {
        if (cancel) PreJit.Stop();
        else PreJit.Enabled = false;
        PreJit.Walk(typeof(Shape).Assembly, PreJit.PatchedSet());
        PreJit.Enabled = true;
        Assert.That(PreJit.Prepared + PreJit.Failed, Is.Zero);
    }

    // The flag is read every CheckEvery rows: after the cancel lands, at most one more interval is walked
    [Test]
    public void CancelStopsARunningWalk()
    {
        var lib = typeof(ClientMain).Assembly;
        using var pe = new PEReader(File.OpenRead(lib.Location));
        var rows = pe.GetMetadataReader().GetTableRowCount(TableIndex.MethodDef);
        var walker = new Thread(() => PreJit.Walk(lib, PreJit.PatchedSet())) { IsBackground = true };
        walker.Start();
        _ = SpinWait.SpinUntil(() => Done() >= 2 * PreJit.CheckEvery, TimeSpan.FromSeconds(10));
        PreJit.Stop();
        Interlocked.MemoryBarrier();
        var atStop = Done();
        Assert.That(walker.Join(TimeSpan.FromSeconds(30)), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(atStop, Is.GreaterThanOrEqualTo(2 * PreJit.CheckEvery), "the walk never got going");
            Assert.That(Done(), Is.LessThan(rows / 2), "the walk ran on after the cancel");
            Assert.That(Done() - atStop, Is.LessThan(2 * PreJit.CheckEvery),
                "more than one checkpoint interval after the cancel");
        });

        static int Done() => PreJit.Prepared + PreJit.Failed;
    }

    // Field 19 of /proc/thread-self/stat, after the parenthesised command name
    private static int Nice()
    {
        if (!OperatingSystem.IsLinux()) return 0;
        var stat = File.ReadAllText("/proc/thread-self/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(fields[16], CultureInfo.InvariantCulture);
    }
}
