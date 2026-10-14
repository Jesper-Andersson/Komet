using Komet.Core;
using Komet.Shapes;
using Vintagestory.GameContent;

namespace Komet.Test.Shapes;

// The budget rides on one read of Entity.ShapeFresh in EntityShapeRenderer.BeforeRender: if the transpiler lands anywhere else, or
// on a changed method, entities are either never deferred or never tesselated. So: found exactly once on the shipped IL, left
// alone on anything else, and the policy that decides which entity waits.
public sealed class EntityTessBudgetTests
{
    private static readonly string[] LoopMarks = ["gametick", "esr-pre", "esr-tesseleateshape", "end"];
    private static readonly FieldInfo ShapeFresh = AccessTools.Field(typeof(Entity), "shapeFresh");

    private static readonly MethodInfo FreshGetter =
        AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.ShapeFresh));

    private static readonly MethodInfo Tesselate = AccessTools.DeclaredMethod(typeof(EntityShapeRenderer),
        nameof(EntityShapeRenderer.TesselateShape), Type.EmptyTypes);

    [SetUp]
    public void Reset()
    {
        (EntityTessBudget.Millis, Counting.Hud) = (EntityTessBudget.DefaultMillis, true);
        EntityTessBudget.Reset();
    }

    [TearDown]
    public void Restore()
    {
        (EntityTessBudget.Millis, Counting.Hud) = (EntityTessBudget.DefaultMillis, false);
        EntityTessBudget.Reset();
    }

    private static List<CodeInstruction> Shipped()
    {
        var before = EntityTessBudget.Before();
        Assert.That(before, Is.Not.Null, "EntityShapeRenderer.BeforeRender(float) is gone");
        return PatchProcessor.GetOriginalInstructions(before!);
    }

    [Test]
    public void TheShippedBeforeRenderIsSubstitutedInPlace()
    {
        var code = Shipped();
        var count = code.Count;
        var body = EntityTessBudget.Substitute(code);
        var call = body.FindIndex(c => c.Calls(Tesselate));
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Substituted, Is.True);
            Assert.That(body, Has.Count.EqualTo(count + 2), "Begin and End around the call, nothing else added");
            Assert.That(body.Count(c => c.Calls(FreshGetter)), Is.Zero);
            Assert.That(
                body.Count(c => c.Calls(AccessTools.Method(typeof(EntityTessBudget), nameof(EntityTessBudget.Fresh)))),
                Is.EqualTo(1));
            Assert.That(
                body[call - 1].Calls(AccessTools.Method(typeof(EntityTessBudget), nameof(EntityTessBudget.Begin))),
                Is.True);
            Assert.That(
                body[call + 1].Calls(AccessTools.Method(typeof(EntityTessBudget), nameof(EntityTessBudget.End))),
                Is.True);
            Assert.That(body[call - 2].opcode, Is.EqualTo(OpCodes.Ldarg_0),
                "the renderer the call consumes is loaded before Begin");
        });
    }

    private static IEnumerable<TestCaseData> Mismatches()
    {
        yield return new TestCaseData(
                (Action<List<CodeInstruction>>)(code => code.RemoveAll(c => c.operand is "esr-tesseleateshape")))
            .SetName("WithoutTheMarkTheIlIsLeftAlone");
        yield return new TestCaseData((Action<List<CodeInstruction>>)(code =>
                code.Insert(0, code.First(c => c.operand is "esr-tesseleateshape").Clone())))
            .SetName("WithTwoMarksTheIlIsLeftAlone");
        yield return new TestCaseData((Action<List<CodeInstruction>>)(code => code.InsertRange(0,
            [
                new CodeInstruction(OpCodes.Ldnull), new CodeInstruction(OpCodes.Callvirt, FreshGetter),
                new CodeInstruction(OpCodes.Pop)
            ])))
            .SetName("WithTwoReadsTheIlIsLeftAlone");
        yield return new TestCaseData((Action<List<CodeInstruction>>)(code => code.RemoveAll(c => c.Calls(Tesselate))))
            .SetName("WithoutTheTesselationTheIlIsLeftAlone");
    }

    [TestCaseSource(nameof(Mismatches))]
    public void AnotherShapeOfTheMethodIsLeftAlone(Action<List<CodeInstruction>> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var code = Shipped();
        change(code);
        var before = code.Select(c => (c.opcode, c.operand)).ToList();
        var body = EntityTessBudget.Substitute(code);
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Substituted, Is.False);
            Assert.That(body.Select(c => (c.opcode, c.operand)), Is.EqualTo(before));
        });
    }

    // Harmony compiles the replacement when it patches, so an invalid substitution would throw here rather than in a frame; one that
    // did not happen would be a warning in the log
    [Test]
    public void InstallPatchesTheLoopAndTheRenderer()
    {
        using var harmony = new TestHarmony("komet-test-entitytessbudget");
        var logger = new CapturingLogger();
        EntityTessBudget.Install(harmony, logger);
        var loop = Harmony.GetPatchInfo(AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender",
            [typeof(float)]));
        var renderer = Harmony.GetPatchInfo(EntityTessBudget.Before());
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Substituted, Is.True);
            Assert.That(logger.Lines, Is.Empty);
            Assert.That(loop?.Prefixes.Select(p => p.PatchMethod.Name),
                Is.EquivalentTo([nameof(EntityTessBudget.Frame)]));
            Assert.That(renderer?.Transpilers.Select(p => p.PatchMethod.Name),
                Is.EquivalentTo([nameof(EntityTessBudget.Substitute)]));
        });
    }

    private static Entity Made(long id, bool fresh = false)
    {
        var entity = (Entity)RuntimeHelpers.GetUninitializedObject(typeof(EntityAgent));
        entity.EntityId = id;
        entity.Code = new AssetLocation("game", $"deer-{id}");
        ShapeFresh.SetValue(entity, fresh);
        return entity;
    }

    private static void Spend(int millis)
    {
        EntityTessBudget.Begin();
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds <= millis) Thread.SpinWait(64);
        EntityTessBudget.End();
    }

    [Test]
    public void AFreshShapeIsFreshWhateverTheBudget()
    {
        EntityTessBudget.Start(null);
        Spend(EntityTessBudget.Millis);
        Assert.That(EntityTessBudget.Fresh(Made(1, true)), Is.True);
    }

    [Test]
    public void TheFirstTesselationsOfAFrameRunUntilTheBudgetIsSpent()
    {
        EntityTessBudget.Millis = 1;
        EntityTessBudget.Start(null);
        var (first, second, third) = (Made(1), Made(2), Made(3));
        Assert.That(EntityTessBudget.Fresh(first), Is.False, "the first one runs");
        Assert.That(EntityTessBudget.Fresh(second), Is.False, "nothing spent yet");
        Spend(2);
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Fresh(third), Is.True, "over budget: put off");
            Assert.That(EntityTessBudget.Deferred, Is.EqualTo(1));
            Assert.That(EntityTessBudget.Tesselations, Is.EqualTo(1));
            Assert.That(EntityTessBudget.SlowestCode, Is.EqualTo("deer-2"), "the one BeforeRender let through last");
            Assert.That(EntityTessBudget.SlowestMs, Is.GreaterThanOrEqualTo(2));
        });
        EntityTessBudget.Start(null);
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Fresh(third), Is.False, "its turn in the next frame");
            Assert.That(EntityTessBudget.MostWaited, Is.EqualTo(1));
            Assert.That(EntityTessBudget.WorstFrameMs, Is.GreaterThanOrEqualTo(2));
        });
    }

    [Test]
    public void TheLocalPlayerIsNeverPutOff()
    {
        EntityTessBudget.Millis = 1;
        var self = Made(7);
        EntityTessBudget.Start(self);
        Spend(2);
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Fresh(self), Is.False);
            Assert.That(EntityTessBudget.Fresh(Made(8)), Is.True);
        });
    }

    [Test]
    public void NoEntityWaitsLongerThanMaxWaitFrames()
    {
        EntityTessBudget.Millis = 1;
        var waiting = Made(5);
        var frames = 0;
        for (; frames < 10 * EntityTessBudget.MaxWaitFrames; frames++)
        {
            EntityTessBudget.Start(null);
            Spend(1); // something else took the whole budget, every frame
            if (!EntityTessBudget.Fresh(waiting)) break;
        }

        Assert.Multiple(() =>
        {
            Assert.That(frames, Is.EqualTo(EntityTessBudget.MaxWaitFrames));
            Assert.That(EntityTessBudget.MostWaited, Is.EqualTo(EntityTessBudget.MaxWaitFrames));
        });
    }

    // The wait is counted in frames the entity asked and was put off: one that left the view for a while has not waited meanwhile,
    // and the longest wait the HUD shows is a wait someone could have seen
    [Test]
    public void AnEntityOutOfViewDoesNotWait()
    {
        EntityTessBudget.Millis = 1;
        var waiting = Made(5);
        EntityTessBudget.Start(null);
        Spend(1);
        Assert.That(EntityTessBudget.Fresh(waiting), Is.True, "put off once");
        for (var frame = 0; frame < 10 * EntityTessBudget.MaxWaitFrames; frame++)
            EntityTessBudget.Start(null); // out of view
        Spend(1);
        Assert.That(EntityTessBudget.Fresh(waiting), Is.True, "back in view, still over budget: put off again");
        EntityTessBudget.Start(null);
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Fresh(waiting), Is.False);
            Assert.That(EntityTessBudget.MostWaited, Is.EqualTo(2));
        });
    }

    // No static keeps an entity – and through it a world left for the menu: the one being timed is known by its code
    [Test]
    public void NoEntityIsHeld()
    {
        var current = AccessTools.Field(typeof(EntityTessBudget), "_code");
        EntityTessBudget.Millis = 1;
        EntityTessBudget.Start(Made(7));
        Assert.That(EntityTessBudget.Fresh(Made(1)), Is.False);
        Spend(2);
        Assert.Multiple(() =>
        {
            Assert.That(current.GetValue(null), Is.Null, "timed and let go");
            Assert.That(EntityTessBudget.Fresh(Made(2)), Is.True);
            Assert.That(current.GetValue(null), Is.Null, "put off: nothing to time");
            Assert.That(EntityTessBudget.Fresh(Made(3)), Is.True);
        });
        _ = EntityTessBudget.Fresh(Made(7));
        EntityTessBudget.Start(null);
        Assert.That(current.GetValue(null), Is.Null, "a tesselation that never ended is dropped with the frame");
        Assert.That(AccessTools.Field(typeof(EntityTessBudget), "_self").FieldType, Is.EqualTo(typeof(long)),
            "the player by id");
        Assert.That(current.FieldType, Is.EqualTo(typeof(AssetLocation)), "the timed entity by code");
    }

    [Test]
    public void AtZeroEverythingRunsAsInTheEngine()
    {
        EntityTessBudget.Millis = EntityTessBudget.Engine;
        EntityTessBudget.Start(null);
        Spend(EntityTessBudget.DefaultMillis + 1);
        Assert.Multiple(() =>
        {
            Assert.That(EntityTessBudget.Fresh(Made(1)), Is.False);
            Assert.That(EntityTessBudget.Fresh(Made(1, true)), Is.True);
            Assert.That(EntityTessBudget.Deferred, Is.Zero);
        });
    }

    // The time the Before-stage renderers ahead of the entity loop take lands in its own mark, not in the first entity's
    [Test]
    public void TheEntityLoopOpensWithItsOwnMark()
    {
        var saved = ScreenManager.FrameProfiler;
        var profiler = new FrameProfilerUtil(_ => { }) { Enabled = true };
        ScreenManager.FrameProfiler = profiler;
        try
        {
            profiler.Begin();
            profiler.Mark("gametick");
            EntityTessBudget.Frame(null!);
            profiler.Mark("esr-tesseleateshape");
            profiler.End();
            Assert.That(profiler.PrevRootEntry.Marks.Keys, Is.EqualTo(LoopMarks));
        }
        finally
        {
            ScreenManager.FrameProfiler = saved;
        }
    }

    [Test]
    public void ANullEntityThrowsAsTheEngineDid()
    {
        EntityTessBudget.Start(null);
        Assert.That(() => EntityTessBudget.Fresh(null!), Throws.TypeOf<NullReferenceException>());
    }
}
