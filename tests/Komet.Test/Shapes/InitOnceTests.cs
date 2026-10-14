using Komet.Core;
using Komet.Shapes;

namespace Komet.Test.Shapes;

// EntityPlayer.OnTesselation runs Shape.InitForAnimations twice on the local player's shape, the second time with no disable
// list. InitOnce skips the second, which is only sound if the second leaves the shape exactly as the first did – checked here
// against the engine on the composed player and on a shape with a missing element – and if the window around it skips
// nothing else, checked against the real patch.
public sealed class InitOnceTests
{
    private static readonly string[] OtherJoints = ["head", "neck"], OtherDisable = ["hair"];
    private Harmony? _harmony;
    private long _skipped; // InitOnce.Skipped when the test patched

    private long Skipped => InitOnce.Skipped - _skipped;

    [TearDown]
    public void Restore()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        (InitOnce.Enabled, Counting.Hud, ShapeInitMemo.Enabled) = (true, false, true);
    }

    // The window and the InitForAnimations patch it rides on, which is ShapeInitMemo's; the memo itself stays out of the way
    private void Patch(ILogger? logger = null, ulong fingerprint = InitOnce.Fingerprint,
        ulong memo = ShapeInitMemo.Fingerprint)
    {
        _harmony = new Harmony("komet-test-initonce");
        InitOnce.Install(_harmony, logger, fingerprint);
        ShapeInitMemo.Install(_harmony, null, memo);
        (Counting.Hud, ShapeInitMemo.Enabled, _skipped) = (true, false, InitOnce.Skipped);
    }

    // What EntityPlayer.OnTesselation does: the first LoadAnimator with the entity's disable list, the second with none
    private static void AssertSecondInitChangesNothing(Shape shape, string[]? disable)
    {
        Init(shape, disable, ["head"]);
        var once = AnimationShapes.State(shape);
        var tables = shape.Animations.SelectMany(a => a.KeyFrames).Select(AnimationShapes.Table.GetValue).ToList();
        Init(shape, null, ["head"]);
        var twice = AnimationShapes.State(shape);
        Assert.Multiple(() =>
        {
            Assert.That(twice, Is.EqualTo(once));
            Assert.That(
                shape.Animations.SelectMany(a => a.KeyFrames).Select(AnimationShapes.Table.GetValue)
                    .Where((t, i) => !ReferenceEquals(t, tables[i])),
                Is.Not.Empty, "the unpatched second init must have run, or the comparison proves nothing");
        });
    }

    [TestCase("naked")]
    [TestCase("clothed")]
    public void TheComposedPlayersSecondInitLeavesItsShapeAsTheFirstDid(string outfit)
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.PlayerUninitialised(outfit);
        AssertSecondInitChangesNothing(shape, AnimationShapes.PlayerDisable);
    }

    // A keyframe for an element the shape lacks gets a placeholder ForElement from the first init, whose disable list does
    // not name it; the second init, with none, must neither replace nor drop it
    [Test]
    public void APlaceholderFromTheFirstInitSurvivesTheSecond()
    {
        var shape = AnimationShapes.Synthetic();
        AssertSecondInitChangesNothing(shape, ["cape"]);
        var missing = shape.Animations[0].KeyFrames[0].Elements["gone"];
        Assert.That(missing.ForElement, Is.Not.Null, "no placeholder was made");
        Assert.That(missing.ForElement.Name, Is.Null, "the placeholder is a bare ShapeElement");
    }

    private static void Init(Shape shape, string[]? disable, string[] joints, string name = "player")
    {
        shape.InitForAnimations(AnimationShapes.Log, name, disable, joints);
    }

    private static EntityPlayer Player() => (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));

    // The two inits exactly as EntityPlayer.OnTesselation makes them, the window driven the way its patches drive it. The first
    // LoadAnimator builds its animator right after its init, and AnimatorBase's constructor lower-cases every animation code.
    private static (object? Before, object? After) Tesselate(Shape shape, EntityPlayer player,
        string[]? secondJoints = null, string[]? secondDisable = null, Shape? secondShape = null, bool based = true)
    {
        InitOnce.Open(player);
        try
        {
            Init(shape, ["cape"], ["head"]);
            _ = new ClientAnimator(() => 1.0, shape.Animations, shape.Elements, shape.JointsById);
            if (based) InitOnce.Based(player);
            var target = secondShape ?? shape;
            var before = AnimationShapes.Table.GetValue(target.Animations[0].KeyFrames[0]);
            Init(target, secondDisable, secondJoints ?? ["head"]);
            return (before, AnimationShapes.Table.GetValue(target.Animations[0].KeyFrames[0]));
        }
        finally
        {
            InitOnce.Close();
        }
    }

    // The duplicate, alone, is skipped: the same shape, joints and disable list (or none) once base has returned
    private static IEnumerable<TestCaseData> SecondInits()
    {
        static TestCaseData Case(string name, bool skipped, string[]? joints = null, string[]? disable = null,
            bool otherShape = false, bool based = true) =>
            new TestCaseData(joints, disable, otherShape, based, skipped).SetName(name);

        yield return Case("InTheWindowTheSecondInitOfTheSameShapeIsSkipped", true);
        yield return Case("TheSameDisableListIsSkippedToo", true, disable: ["cape"]);
        yield return Case("OtherJointsRunTheSecondInit", false, OtherJoints);
        yield return Case("AnotherDisableListRunsTheSecondInit", false, disable: OtherDisable);
        yield return Case("AnotherShapeRunsTheSecondInit", false, otherShape: true);
        yield return Case("AnInitBeforeBaseReturnedRunsAgain", false, based: false);
    }

    [TestCaseSource(nameof(SecondInits))]
    public void OnlyTheDuplicateIsSkipped(string[]? joints, string[]? disable, bool otherShape, bool based,
        bool skipped)
    {
        Patch();
        var other = AnimationShapes.Synthetic();
        Init(other, null, ["head"], "other");
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player(), joints, disable,
            otherShape ? other : null, based);
        Assert.Multiple(() =>
        {
            Assert.That(InitOnce.Blocked, Is.False);
            Assert.That(after, skipped ? Is.SameAs(before) : Is.Not.SameAs(before), "whether the second init ran");
            Assert.That(Skipped, Is.EqualTo(skipped ? 1 : 0));
        });
    }

    [Test]
    public void OnlyTheFirstInitAfterBaseIsSkipped()
    {
        Patch();
        var shape = AnimationShapes.Synthetic();
        var player = Player();
        InitOnce.Open(player);
        try
        {
            Init(shape, null, ["head"]);
            InitOnce.Based(player);
            Init(shape, null, ["head"]);
            var before = AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]);
            Init(shape, null, ["head"]);
            Assert.That(AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]), Is.Not.SameAs(before));
        }
        finally
        {
            InitOnce.Close();
        }

        Assert.That(Skipped, Is.EqualTo(1));
    }

    [Test]
    public void OutsideTheWindowNothingIsSkipped()
    {
        Patch();
        var shape = AnimationShapes.Synthetic();
        Init(shape, null, ["head"]);
        var before = AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]);
        Init(shape, null, ["head"]);
        Assert.That(AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]), Is.Not.SameAs(before));
    }

    [Test]
    public void ANestedTesselationSkipsNothing()
    {
        Patch();
        var (shape, player) = (AnimationShapes.Synthetic(), Player());
        InitOnce.Open(player);
        try
        {
            Init(shape, null, ["head"]);
            InitOnce.Based(player);
            var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
            Assert.That(after, Is.Not.SameAs(before), "a window inside a window is not the sequence InitOnce knows");
            var outer = AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]);
            Init(shape, null, ["head"]);
            Assert.That(AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]), Is.Not.SameAs(outer));
        }
        finally
        {
            InitOnce.Close();
        }
    }

    // The whole window against the engine: the same tesselation with and without InitOnce must leave the same shape, including
    // the AnimationsByCrc32 entry the second init makes for a code the first animator lower-cased in between
    [TestCase("wave", 1)]
    [TestCase("Wave", 0)]
    public void TheWindowLeavesTheShapeAsTheEngineDoes(string code, int skipped)
    {
        var engine = AnimationShapes.Synthetic();
        engine.Animations[0].Code = code;
        _ = Tesselate(engine, Player());
        Patch();
        var patched = AnimationShapes.Synthetic();
        patched.Animations[0].Code = code;
        _ = Tesselate(patched, Player());
        Assert.Multiple(() =>
        {
            Assert.That(AnimationShapes.State(patched), Is.EqualTo(AnimationShapes.State(engine)));
            Assert.That(Skipped, Is.EqualTo(skipped));
            Assert.That(patched.AnimationsByCrc32.ContainsKey(AnimationMetaData.GetCrc32("wave")), Is.True);
        });
    }

    // ResolveAndFindJoints zeroes every JointId before it throws on a missing joint list; Entity.OnTesselation catches that and
    // logs it. The init before it completed, but it is no longer the state the second init would repeat.
    [Test]
    public void AnInitThatThrewInsideBaseIsNotSkippedOver()
    {
        Patch();
        var (shape, player) = (AnimationShapes.Synthetic(), Player());
        var arm = shape.Elements[0].Children![1];
        InitOnce.Open(player);
        try
        {
            Init(shape, ["cape"], ["head"]);
            Assert.That(arm.JointId, Is.Not.Zero);
            Assert.That(() => Init(shape, ["cape"], null!), Throws.TypeOf<NullReferenceException>());
            Assert.That(arm.JointId, Is.Zero, "the failed init did not get as far as the test needs");
            InitOnce.Based(player);
            Init(shape, null, ["head"]);
            Assert.That(arm.JointId, Is.Not.Zero, "the second init was skipped over the shape the failed one left");
        }
        finally
        {
            InitOnce.Close();
        }

        Assert.That(Skipped, Is.Zero);
    }

    [Test]
    public void ASubclassOfEntityPlayerRunsBothInits()
    {
        Patch();
        var player = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ModdedPlayer));
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), player);
        Assert.Multiple(() =>
        {
            Assert.That(after, Is.Not.SameAs(before));
            Assert.That(Skipped, Is.Zero);
        });
    }

    // The real patched method: an uninitialised EntityPlayer throws from inside Entity.OnTesselation's catch, which has no Api to
    // log to. The finalizer must let that exception out unchanged and still close the window, or the next one would nest.
    [Test]
    public void AnExceptionLeavesThroughTheFinalizerAndClosesTheWindow()
    {
        Patch();
        var shape = AnimationShapes.Synthetic();
        var player = Player();
        Assert.That(() => player.OnTesselation(ref shape, "player"), Throws.TypeOf<NullReferenceException>());
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
        Assert.Multiple(() =>
        {
            Assert.That(after, Is.SameAs(before), "the window was left open, so this one nested and skipped nothing");
            Assert.That(Skipped, Is.EqualTo(1));
        });
    }

    [Test]
    public void DisabledRunsBothInits()
    {
        Patch();
        InitOnce.Enabled = false;
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
        Assert.That(after, Is.Not.SameAs(before));
    }

    // A mod's patch on anything between the two inits could change the shape there, so both run, also when that patch comes after
    // Komet installed, which the recheck on LevelFinalize sees; logged once, however often it is asked
    [TestCase(false)]
    [TestCase(true)]
    public void AForeignPatchBetweenTheInitsRunsBoth(bool late)
    {
        var ctor = AccessTools.Constructor(typeof(PlayerHeadController),
            [typeof(IAnimationManager), typeof(EntityPlayer), typeof(Shape)]);
        using var foreign = new TestHarmony("komet-test-initonce-foreign");
        var logger = new CapturingLogger();
        if (!late)
            _ = foreign.Patch(ctor, postfix: Foreign.Postfix);
        Patch(logger);
        if (late)
            _ = foreign.Patch(ctor, postfix: Foreign.Postfix);
        InitOnce.Recheck();
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
        Assert.Multiple(() =>
        {
            Assert.That(InitOnce.Blocked, Is.True);
            Assert.That(after, Is.Not.SameAs(before));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("another mod"));
        });
    }

    // A patch on a method the skipped init calls would miss that call: another mod's postfix on ResolveAndFindJoints, or on the
    // SetJointId that one calls
    [TestCase(nameof(Shape.ResolveAndFindJoints))]
    [TestCase(nameof(ShapeElement.SetJointId))]
    public void AForeignPatchInsideTheInitRunsBoth(string name)
    {
        var inside = name == nameof(ShapeElement.SetJointId)
            ? AccessTools.DeclaredMethod(typeof(ShapeElement), name, [typeof(int)])
            : AccessTools.DeclaredMethod(typeof(Shape), name,
                [typeof(ILogger), typeof(string), typeof(Dictionary<string, ShapeElement>), typeof(string[])]);
        using var foreign = new TestHarmony("komet-test-initonce-foreign-inside");
        _ = foreign.Patch(inside, postfix: Foreign.Postfix);
        Patch();
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
        Assert.Multiple(() =>
        {
            Assert.That(InitOnce.Blocked, Is.True);
            Assert.That(after, Is.Not.SameAs(before));
        });
    }

    // The memo's patch carries the skip also when the memo stands down for a changed engine of its own
    [Test]
    public void AChangedEngineForTheMemoStillSkipsTheDuplicate()
    {
        Patch(memo: ShapeInitMemo.Fingerprint ^ 1);
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
        Assert.Multiple(() =>
        {
            Assert.That((ShapeInitMemo.Blocked, InitOnce.Blocked), Is.EqualTo((true, false)));
            Assert.That(after, Is.SameAs(before), "the second init ran");
            Assert.That(Skipped, Is.EqualTo(1));
        });
    }

    // Close is a finalizer, so the window closes however EntityPlayer.OnTesselation leaves, and a void one rethrows untouched
    [Test]
    public void InstallPatchesTheTesselationAndLeavesNoWindowOpen()
    {
        Patch();
        var patches = Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(EntityPlayer),
            nameof(EntityPlayer.OnTesselation), [typeof(Shape).MakeByRefType(), typeof(string)]));
        Assert.Multiple(() =>
        {
            Assert.That(patches?.Prefixes.Select(p => p.PatchMethod.Name), Is.EquivalentTo([nameof(InitOnce.Open)]));
            Assert.That(patches?.Finalizers.Select(p => p.PatchMethod.Name), Is.EquivalentTo([nameof(InitOnce.Close)]));
            Assert.That(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(Entity), nameof(Entity.OnTesselation),
                [typeof(Shape).MakeByRefType(), typeof(string)]))?.Postfixes, Has.Count.EqualTo(1));
        });
        var shape = AnimationShapes.Synthetic();
        Init(shape, null, ["head"]);
        var before = AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]);
        Init(shape, null, ["head"]);
        Assert.That(AnimationShapes.Table.GetValue(shape.Animations[0].KeyFrames[0]), Is.Not.SameAs(before),
            "a window was left open");
    }

    [Test]
    public void AChangedEngineRunsBothInitsWithOneWarning()
    {
        var logger = new CapturingLogger();
        Patch(logger, InitOnce.Fingerprint ^ 1);
        var (before, after) = Tesselate(AnimationShapes.Synthetic(), Player());
        Assert.Multiple(() =>
        {
            Assert.That(InitOnce.Blocked, Is.True);
            Assert.That(after, Is.Not.SameAs(before));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("1.22.7"));
        });
    }

    // A mod's EntityPlayer subclass may override the protected OnTesselation and change the shape after base's init
    private sealed class ModdedPlayer : EntityPlayer
    {
        public int Tesselations { get; private set; }

        protected override void OnTesselation(ref Shape entityShape, string shapePathForLogging, ref bool shapeIsCloned)
        {
            base.OnTesselation(ref entityShape, shapePathForLogging, ref shapeIsCloned);
            Tesselations++; // where such a mod could change the shape after the first init
        }
    }
}
