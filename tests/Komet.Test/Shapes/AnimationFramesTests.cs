using Komet.Core;
using Komet.Shapes;

namespace Komet.Test.Shapes;

// A cache hit hands an animator a compiled pose tree that ClientAnimator.calculateMatrices walks by position, so a descriptor that
// fails to separate two shapes silently animates one entity with another's poses.
public sealed class AnimationFramesTests
{
    private static readonly FieldInfo JointsDone =
        typeof(Animation).GetField("jointsDone", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private const string Frames = "Komet.Shapes.AnimationFrames, Komet";

    private static int _foreignCalls;

    private static readonly float[] BothHands = [10f, 10f, 30f, 30f];
    private Harmony? _harmony;
    private int _mainThread;

    [SetUp]
    public void Remember()
    {
        _mainThread = RuntimeEnv.MainThreadId;
    }

    [TearDown]
    public void Restore()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        RuntimeEnv.MainThreadId = _mainThread;
        (AnimationFrames.Enabled, Counting.Hud) = (true, false);
        AnimationFrames.ResetPeaks();
        AnimationFrames.Clear();
    }

    private void Patch(ILogger? logger = null, ulong fingerprint = AnimationFrames.Fingerprint)
    {
        _harmony = new Harmony("komet-test-animationframes");
        AnimationFrames.Install(_harmony, logger, fingerprint);
    }

    // The key as the cache compares it, null for a shape it does not describe. Two shapes describe the same only when
    // GenerateAllFrames compiles them the same: the animator indexes a pose tree by position, so a descriptor that misses a
    // difference is a wrong pose.
    internal static byte[]? Descriptor(Animation animation, ShapeElement[] roots, bool recursive = true)
    {
        lock (Gate(null))
            return Describe(null, animation, roots, recursive) ? Buffer(null).AsSpan(0, At(null)).ToArray() : null;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "Gate")]
    private static extern ref Lock Gate([UnsafeAccessorType(Frames)] object? frames);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "Describe")]
    private static extern bool Describe([UnsafeAccessorType(Frames)] object? frames, Animation animation,
        ShapeElement[] roots, bool recursive);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_buffer")]
    private static extern ref byte[] Buffer([UnsafeAccessorType(Frames)] object? frames);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_at")]
    private static extern ref int At([UnsafeAccessorType(Frames)] object? frames);

    private static ShapeElement Bare(string name, params ShapeElement[] children)
    {
        return new ShapeElement { Name = name, Children = children.Length == 0 ? null : children };
    }

    private static AnimationKeyFrameElement Turned(double rotationX) =>
        new AnimationKeyFrameElement { RotationX = rotationX };

    private static Animation Walk(params (string Element, double Rotation)[] elements)
    {
        var byName = new Dictionary<string, AnimationKeyFrameElement>();
        foreach (var (element, rotation) in elements) byName[element] = Turned(rotation);
        return new Animation
        {
            Code = "walk",
            QuantityFrames = 30,
            KeyFrames = [new AnimationKeyFrame { Frame = 0, Elements = byName }]
        };
    }

    // Two descriptors and whether they must be equal: the same for what compiles the same, different for what does not
    private static IEnumerable<TestCaseData> Descriptions()
    {
        static TestCaseData Case(string name, bool same, Func<byte[]?> one, Func<byte[]?> two) =>
            new TestCaseData(one, two, same).SetName(name);

        var walk = Walk(("upperarm", 30));
        var two = Walk(("upperarm", 30), ("lowerarm", 10));
        static ShapeElement[] Arms() => [Bare("upperarm"), Bare("lowerarm")];
        yield return Case("TheSameShapeAndAnimationDescribeTheSame", true,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(Walk(("upperarm", 30)), [Bare("upperarm")]));
        // Keyframe elements are a Dictionary, whose iteration order is not part of its content
        yield return Case("KeyframeElementOrderDoesNotChangeTheDescriptor", true,
            () => Descriptor(two, Arms()),
            () => Descriptor(Walk(("lowerarm", 10), ("upperarm", 30)), Arms()));
        // AnimationKeyFrame.Resolve maps keyframe elements onto shape elements by name, so a rename changes the compile
        yield return Case("ARenamedElementDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(walk, [Bare("lowerarm")]));
        // This is the gear case: Shape.StepParentShape reparents a gear element as a child of a named element
        yield return Case("StepParentedGearDescribesDifferentlyFromTheBareShape", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))]));
        // Without the depth written alongside each name, a child and a sibling would describe identically
        yield return Case("AChildDescribesDifferentlyFromASibling", false,
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))]),
            () => Descriptor(walk, [Bare("upperarm"), Bare("sleeve")]));
        yield return Case("TwoGearPiecesOnDifferentParentsDescribeDifferently", false,
            () => Descriptor(two, [Bare("upperarm", Bare("plate")), Bare("lowerarm")]),
            () => Descriptor(two, [Bare("upperarm"), Bare("lowerarm", Bare("plate"))]));
        // StepParentShape merges the gear's keyframes into the parent animation, changing content as well as count
        yield return Case("AChangedKeyframeValueDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () => Descriptor(Walk(("upperarm", 31)), [Bare("upperarm")]));
        yield return Case("AnAddedKeyframeDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () =>
            {
                var added = Walk(("upperarm", 30));
                var key = new AnimationKeyFrame
                { Frame = 15, Elements = new() { ["upperarm"] = Turned(60) } };
                key.Resolve(new Dictionary<string, ShapeElement>()); // stamps its Frame on the element, as every init does
                added.KeyFrames = [added.KeyFrames[0], key];
                return Descriptor(added, [Bare("upperarm")]);
            });
        // GenerateFrameForElement interpolates against QuantityFrames, so the same keyframes over a different span differ
        yield return Case("ADifferentQuantityFramesDescribesDifferently", false,
            () => Descriptor(walk, [Bare("upperarm")]),
            () =>
            {
                var slow = Walk(("upperarm", 30));
                slow.QuantityFrames = 60;
                return Descriptor(slow, [Bare("upperarm")]);
            });
        // An unset double? and a set one are different inputs to getTwoKeyFramesElementForFlag, which tests IsSet
        yield return Case("AnUnsetValueDescribesDifferentlyFromASetOne", false,
            () => Descriptor(Walk(("upperarm", 0)), [Bare("upperarm")]),
            () =>
            {
                var unset = Walk();
                unset.KeyFrames[0].Elements["upperarm"] = new AnimationKeyFrameElement();
                return Descriptor(unset, [Bare("upperarm")]);
            });
        yield return Case("NonRecursiveDescribesDifferentlyFromRecursive", false,
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))]),
            () => Descriptor(walk, [Bare("upperarm", Bare("sleeve"))], false));
        // Element geometry never reaches the compiled pose: the model matrix GenerateFrame threads through is not stored,
        // and the two AnimationFrame methods that once consumed it are [Obsolete] no-ops. So it stays out of the key.
        yield return Case("ElementGeometryDoesNotChangeTheDescriptor", true,
            () => Descriptor(walk, [Bare("upperarm")]),
            () =>
            {
                var moved = Bare("upperarm");
                moved.From = [1, 2, 3];
                moved.RotationX = 45;
                return Descriptor(walk, [moved]);
            });
    }

    [TestCaseSource(nameof(Descriptions))]
    public void TheDescriptorSeparatesWhatCompilesDifferently(Func<byte[]?> one, Func<byte[]?> two, bool same)
    {
        ArgumentNullException.ThrowIfNull(one);
        ArgumentNullException.ThrowIfNull(two);
        var (a, b) = (one(), two());
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.Not.Null, "the descriptor must be computable for a well formed shape");
            Assert.That(b, Is.Not.Null, "the descriptor must be computable for a well formed shape");
            Assert.That(a, same ? Is.EqualTo(b) : Is.Not.EqualTo(b));
        });
    }

    private static ShapeElement Element(string name, params ShapeElement[] children)
    {
        return new ShapeElement
        {
            Name = name, From = [1, 2, 3], To = [4, 5, 6], RotationOrigin = [0.5, 0, 0.25], RotationY = 15,
            Children = children.Length == 0 ? null : children
        };
    }

    // Every group set whole, as a shape's json writes them
    private static AnimationKeyFrameElement Full(double v)
    {
        return new AnimationKeyFrameElement
        {
            OffsetX = v / 2, OffsetY = -v, OffsetZ = 1, RotationX = v, RotationY = 2 * v, RotationZ = -v, StretchX = 1,
            StretchY = 1 + v / 100, StretchZ = 1, RotShortestDistanceX = v > 0, RotShortestDistanceZ = true
        };
    }

    private static AnimationKeyFrameElement Rotated(double degrees)
    {
        return new AnimationKeyFrameElement { RotationX = degrees, RotationY = 0, RotationZ = 0 };
    }

    private static AnimationKeyFrame Key(int frame, params (string Name, AnimationKeyFrameElement Element)[] elements)
    {
        return new AnimationKeyFrame { Frame = frame, Elements = elements.ToDictionary(e => e.Name, e => e.Element) };
    }

    // Resolved the way AnimationManager.LoadAnimator resolves a shape
    private static Shape Build(int quantityFrames, ShapeElement[] roots, params AnimationKeyFrame[] keys)
    {
        var shape = new Shape
        {
            Elements = roots,
            Animations =
                [new Animation { Code = "test", Name = "test", QuantityFrames = quantityFrames, KeyFrames = keys }]
        };
        shape.InitForAnimations(AnimationShapes.Log, "test", "head");
        return shape;
    }

    private static ShapeElement[] Arm() =>
        [Element("upper", Element("lower", Element("hand")), Element("sleeve")), Element("head")];

    // The engine's compile, then Komet's on the same Animation, compared to the bit; the result is Komet's
    private static AnimationFrame[][] SameAsEngine(Shape shape, bool recursive = true)
    {
        var animation = shape.Animations[0];
        animation.GenerateAllFrames(shape.Elements, shape.JointsById, recursive);
        var engine = animation.PrevNextKeyFrameByFrame;
        animation.PrevNextKeyFrameByFrame = null;
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById, recursive), Is.True,
            "declined");
        Assert.That(AnimationShapes.Diff(engine, animation.PrevNextKeyFrameByFrame), Is.Null);
        return animation.PrevNextKeyFrameByFrame!;
    }

    private static List<ElementPose> PosesOf(AnimationFrame[][] compiled, string name)
    {
        return
        [
            .. compiled.SelectMany(pair => pair).Distinct().SelectMany(AnimationShapes.Poses)
                .Where(p => p.ForElement.Name == name)
        ];
    }

    [Test]
    public void KeyframesOutOfFrameOrderCompileAsTheEngineDoes()
    {
        var shape = Build(12, Arm(), Key(6, ("upper", Full(60)), ("hand", Full(5))), Key(0, ("upper", Full(0))),
            Key(3, ("lower", Full(-30)), ("upper", Full(30))), Key(9, ("hand", Full(-5))));
        _ = SameAsEngine(shape);
    }

    // One element object in two keyframes: seekRightKeyFrame and seekLeftKeyFrame land on two keyframes that hold the same
    // object, so the engine takes its left == right branch and interpolates nothing
    [Test]
    public void OneElementObjectSharedByTwoKeyframesTakesTheLeftIsRightBranch()
    {
        var shared = Full(40);
        var shape = Build(10, Arm(), Key(0, ("upper", shared)), Key(3, ("lower", Full(10))), Key(6, ("upper", shared)));
        var compiled = SameAsEngine(shape);
        Assert.That(PosesOf(compiled, "upper").Select(p => p.degX), Is.All.EqualTo(40f));
    }

    // What the descriptor cannot hold: that one object sits in two keyframes, and the Frame the last Resolve stamped on it. At 6 the
    // object carries the later frame, at 3 both keyframes stamp the same one. Either shape compiles uncached.
    [TestCase(6)]
    [TestCase(3)]
    public void AKeyframeElementObjectInTwoKeyframesIsNotDescribed(int second)
    {
        var shared = Full(40);
        var shape = Build(10, Arm(), Key(0, ("lower", Full(10))), Key(3, ("upper", shared)),
            Key(second, ("upper", shared)));
        Assert.That(Descriptor(shape.Animations[0], shape.Elements), Is.Null);
    }

    [Test]
    public void AnElementInOneKeyframeKeepsItsValueAtEveryFrame()
    {
        var shape = Build(8, Arm(), Key(0, ("lower", Full(1))), Key(4, ("upper", Full(25)), ("lower", Full(2))));
        var compiled = SameAsEngine(shape);
        Assert.That(PosesOf(compiled, "upper").Select(p => p.degX), Is.All.EqualTo(25f));
    }

    // At frame 8 the next keyframe of "upper" is the first one, at frame 2: the engine's wrap branch, t = Mod(8 - 7, 10) / 5
    [Test]
    public void WrappingAroundTheEndInterpolatesAcrossIt()
    {
        var shape = Build(10, Arm(), Key(2, ("upper", Rotated(20))), Key(7, ("upper", Rotated(70))),
            Key(8, ("lower", Rotated(1))));
        var compiled = SameAsEngine(shape);
        var atEight = compiled[8][0].RootElementTransforms[0];
        Assert.That(atEight.degX, Is.EqualTo(60f));
    }

    // Two keyframes at one frame: right.Frame - left.Frame is 0, and the engine divides by it. NaN and infinity have to match
    // to the bit, which Diff compares.
    [Test]
    public void TwoKeyframesAtTheSameFrameDivideByZeroTheSameWay()
    {
        var shape = Build(6, Arm(), Key(0, ("lower", Full(3))), Key(3, ("upper", Full(10))),
            Key(3, ("upper", Full(50))));
        var compiled = SameAsEngine(shape);
        var degrees = PosesOf(compiled, "upper").Select(p => p.degX).ToList();
        Assert.That(degrees.Any(d => !float.IsFinite(d)), Is.True, "the division by zero was not reached");
    }

    [Test]
    public void NegativeFrameNumbersCompileAsTheEngineDoes()
    {
        var shape = Build(8, Arm(), Key(-2, ("upper", Full(-20))), Key(3, ("upper", Full(30)), ("lower", Full(4))));
        _ = SameAsEngine(shape);
    }

    [Test]
    public void RecursiveFalseCompilesOnlyTheRoots()
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0)), ("hand", Full(9))), Key(4, ("upper", Full(45))));
        var compiled = SameAsEngine(shape, false);
        Assert.That(compiled[0][0].RootElementTransforms.Sum(p => p.ChildElementPoses.Count), Is.Zero);
    }

    // The same element object in two places: GenerateFrame poses it twice, GetKeyFrameElement answers the same for both
    [Test]
    public void TheSameElementTwiceInTheTreeIsPosedTwice()
    {
        var hand = Element("hand");
        var shape = Build(8, [Element("upper", hand, Element("lower")), Element("head")],
            Key(0, ("hand", Full(10)), ("upper", Full(1))), Key(4, ("hand", Full(20))));
        shape.Elements = [shape.Elements[0], shape.Elements[1], hand];
        var compiled = SameAsEngine(shape);
        Assert.That(PosesOf(compiled, "hand"), Has.Count.EqualTo(2 * 2)); // two keyframes, twice each
        Assert.That(Descriptor(shape.Animations[0], shape.Elements), Is.Null,
            "names cannot tell it from two hands");
    }

    // FastSmallDictionary.Add appends without looking for the key, and TryGetValue answers with the first entry it meets
    [Test]
    public void AnElementATableHoldsTwiceIsAnsweredByItsFirstEntry()
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(10)), ("lower", Full(2))), Key(4, ("upper", Full(50))));
        var table = (FastSmallDictionary<ShapeElement, AnimationKeyFrameElement>)AnimationShapes.Table.GetValue(
            shape.Animations[0].KeyFrames[0])!;
        table.Add(shape.Elements[0], Full(-90));
        // never read, so never thrown on
        table.Add(shape.Elements[0].Children![0], new AnimationKeyFrameElement { OffsetY = 1 });
        var compiled = SameAsEngine(shape);
        Assert.That(PosesOf(compiled, "upper").Select(p => p.degX), Does.Not.Contain(-90f));
    }

    [Test]
    public void AnElementSubclassIsLeftToTheEngine()
    {
        static ShapeElement Hand() =>
            new ByName { Name = "hand", From = [1, 2, 3], To = [4, 5, 6], RotationOrigin = [0, 0, 0] };

        var shape = Build(8, [Element("upper", Hand()), Element("lower", Hand())], Key(0, ("hand", Full(10))),
            Key(4, ("hand", Full(30))));
        var animation = shape.Animations[0];
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.False);
        Assert.That(Descriptor(animation, shape.Elements), Is.Null,
            "a plain shape with its names compiles otherwise");
        Patch();
        animation.GenerateAllFrames(shape.Elements, shape.JointsById);
        var hands = PosesOf(animation.PrevNextKeyFrameByFrame, "hand").Select(p => p.degX);
        Assert.That(hands, Is.EquivalentTo(BothHands), "both hands, as only the engine's lookup poses them");
    }

    // IsSet reports a position when any of OffsetX/Y/Z has a value, lerpKeyFrameElement then reads all three
    [Test]
    public void AFlagSetOnOneAxisIsLeftToTheEngineToThrowOn()
    {
        var partial = new AnimationKeyFrameElement { OffsetY = 2 };
        var shape = Build(8, Arm(), Key(0, ("upper", partial)), Key(4, ("upper", Full(3))));
        var animation = shape.Animations[0];
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById),
            Throws.InvalidOperationException);
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.False);
        Patch();
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById),
            Throws.InvalidOperationException);
        Assert.That(animation.PrevNextKeyFrameByFrame, Is.Null);
    }

    [Test]
    public void AKeyframeAtOrPastQuantityFramesGetsTheEnginesMessage()
    {
        var shape = Build(4, Arm(), Key(0, ("upper", Full(0))), Key(4, ("upper", Full(9))));
        var animation = shape.Animations[0];
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.False);
        Patch();
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById),
            Throws.InvalidOperationException.With.Message.Contains(
                "QuantityFrames always must be higher than frame number"));
    }

    [Test]
    public void NoKeyframesAtAllIsTheEnginesError()
    {
        var shape = Build(4, Arm());
        var animation = shape.Animations[0];
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.False);
        Patch();
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById),
            Throws.Exception.With.Message.Contains("has no keyframes"));
    }

    [Test]
    public void JointsAtTheCapAreTheEnginesError()
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0))), Key(4, ("upper", Full(9))));
        var animation = shape.Animations[0];
        var joints = Enumerable.Range(1, GlobalConstants.MaxAnimatedElements)
            .ToDictionary(i => i, _ => new AnimationJoint());
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, joints), Is.False);
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, joints),
            Throws.Exception.With.Message.Contains("joint"));
        _ = joints.Remove(1);
        shape.JointsById = joints;
        _ = SameAsEngine(shape);
    }

    // CacheInverseTransformMatrixRecursive, the engine's first statement, walks the whole tree whatever recursive says
    [TestCase(true)]
    [TestCase(false)]
    public void ANullElementAnywhereIsTheEnginesError(bool recursive)
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0))), Key(4, ("upper", Full(9))));
        var lower = shape.Elements[0].Children![0];
        lower.Children = [lower.Children![0], null!];
        var animation = shape.Animations[0];
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById, recursive), Is.False);
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById, recursive),
            Throws.TypeOf<NullReferenceException>());
        shape.Elements = [shape.Elements[0], null!];
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById, recursive), Is.False);
    }

    // GenerateFrame builds a model matrix for every parent and never stores it, but GetLocalTransformMatrix still reads From
    // and RotationOrigin there and throws on a short one. The inverse is cached, so only that dead walk can throw.
    [TestCase(true)]
    [TestCase(false)]
    public void AParentWithoutGeometryIsTheEnginesError(bool fromMissing)
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0))), Key(4, ("upper", Full(9))));
        var lower = shape.Elements[0].Children![0];
        if (fromMissing) lower.From = null;
        else lower.RotationOrigin = [1, 2];
        var animation = shape.Animations[0];
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.False);
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById),
            Throws.InstanceOf<SystemException>());
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById, false), Is.True,
            "without recursion there is no parent matrix to build");
    }

    // GenerateAllFrames clears jointsDone once per keyframe; the compile never touches it but must not succeed where that throws
    [Test]
    public void AnAnimationWithoutItsJointBookkeepingIsTheEnginesError()
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0))), Key(4, ("upper", Full(9))));
        var animation = shape.Animations[0];
        JointsDone.SetValue(animation, null);
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.False);
        Assert.That(() => animation.GenerateAllFrames(shape.Elements, shape.JointsById),
            Throws.TypeOf<NullReferenceException>());
    }

    [Test]
    public void AShapeThatWasNeverResolvedIsDeclined()
    {
        var shape = new Shape
        {
            Elements = Arm(),
            Animations = [new Animation { Code = "raw", QuantityFrames = 4, KeyFrames = [Key(0, ("upper", Full(1)))] }]
        };
        Assert.That(AnimationFrames.Compile(shape.Animations[0], shape.Elements, shape.JointsById), Is.False);
    }

    // No statics and nothing written but the result: the singleplayer server compiles on its own thread through the same prefix
    [Test]
    [Category("Slow")]
    public void FourThreadsCompilingOneShapeAgreeWithTheEngine()
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.Resolve(AnimationShapes.Load("survival", "entity/lore/shiver/surface"), "shiver");
        var animations = shape.Animations.Where(a => a.KeyFrames is { Length: > 0 }).ToArray();
        var reference = new Dictionary<Animation, AnimationFrame[][]>();
        foreach (var animation in animations)
        {
            animation.GenerateAllFrames(shape.Elements, shape.JointsById);
            reference[animation] = animation.PrevNextKeyFrameByFrame;
        }

        int diffs = 0, declined = 0, compiles = 0;
        var threads = Enumerable.Range(0, 4).Select((_, _) => new Thread(() =>
        {
            for (var repeat = 0; repeat < 10; repeat++)
                foreach (var animation in animations)
                {
                    _ = Interlocked.Increment(ref compiles);
                    if (!AnimationFrames.Compile(animation, shape.Elements, shape.JointsById))
                        _ = Interlocked.Increment(ref declined);
                    else if (AnimationShapes.Diff(reference[animation], animation.PrevNextKeyFrameByFrame) != null)
                        _ = Interlocked.Increment(ref diffs);
                }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Multiple(() =>
        {
            Assert.That(compiles, Is.EqualTo(4 * 10 * animations.Length));
            Assert.That(declined, Is.Zero);
            Assert.That(diffs, Is.Zero);
        });
    }

    // Only the compile stands down: the jointsDone race is the engine's own, so the Clone guard stays
    [Test]
    public void AChangedEngineKeepsOnlyTheCloneGuardWithOneWarning()
    {
        var logger = new CapturingLogger();
        Patch(logger, AnimationFrames.Fingerprint ^ 1);
        var clone = AccessTools.Method(typeof(Animation), nameof(Animation.Clone), []);
        var animation = Fresh().Animations[0];
        Assert.Multiple(() =>
        {
            Assert.That(AnimationFrames.Blocked, Is.True);
            Assert.That(_harmony!.GetPatchedMethods(), Is.EquivalentTo(new MethodBase[] { clone }));
            Assert.That(JointsDone.GetValue(animation.Clone()), Is.Not.SameAs(JointsDone.GetValue(animation)));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("1.22.7"));
        });
    }

    private static Shape Fresh(string code = "test")
    {
        var shape = Build(8, Arm(), Key(0, ("upper", Full(0)), ("hand", Full(3))), Key(4, ("upper", Full(45))));
        shape.Animations[0].Code = code;
        return shape;
    }

    private static AnimationFrame[][] Generated(Shape shape)
    {
        shape.Animations[0].GenerateAllFrames(shape.Elements, shape.JointsById);
        return shape.Animations[0].PrevNextKeyFrameByFrame;
    }

    // A miss compiles and is stored, so the next shape that describes the same hits it, inverse transforms filled as the engine's
    // compile fills them first thing; the result is the engine's to the bit
    [Test]
    public void ACompileIsCachedAndSharedAndTheEngines()
    {
        Patch();
        Counting.Hud = true;
        var (hits, misses) = (AnimationFrames.Hits, AnimationFrames.Misses);
        var first = Generated(Fresh());
        var hit = Fresh();
        foreach (var element in AnimationShapes.Elements(hit.Elements)) element.inverseModelTransform = null;
        var second = Generated(hit);
        Assert.Multiple(() =>
        {
            Assert.That(AnimationFrames.Blocked, Is.False);
            Assert.That(AnimationFrames.Misses - misses, Is.EqualTo(1));
            Assert.That(AnimationFrames.Hits - hits, Is.EqualTo(1));
            Assert.That(second, Is.SameAs(first));
            Assert.That(AnimationShapes.Elements(hit.Elements).Where(e => e.inverseModelTransform is null), Is.Empty);
        });
        _harmony!.UnpatchAll(_harmony.Id);
        Assert.That(AnimationShapes.Diff(Generated(Fresh()), first, true), Is.Null);
    }

    [Test]
    public void DisabledEveryCompileIsTheEngines()
    {
        Patch();
        (AnimationFrames.Enabled, Counting.Hud) = (false, true);
        var counted = AnimationFrames.Hits + AnimationFrames.Misses;
        var (first, second) = (Generated(Fresh()), Generated(Fresh()));
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(AnimationFrames.Hits + AnimationFrames.Misses, Is.EqualTo(counted));
        });
    }

    // Past MaxEntries compiled sets the cache starts over instead of growing on
    [Test]
    public void AFullCacheStartsOver()
    {
        Patch();
        Counting.Hud = true;
        var misses = AnimationFrames.Misses;
        var first = Generated(Fresh("a0"));
        for (var i = 1; i < AnimationFrames.MaxEntries; i++) _ = Generated(Fresh($"a{i}"));
        Assert.That(Generated(Fresh("a0")), Is.SameAs(first), "still held with the cache full");
        _ = Generated(Fresh("one-more"));
        Assert.Multiple(() =>
        {
            Assert.That(Generated(Fresh("a0")), Is.Not.SameAs(first), "started over");
            Assert.That(AnimationFrames.Misses - misses, Is.EqualTo(AnimationFrames.MaxEntries + 2));
        });
    }

    private static void ForeignRan()
    {
        _foreignCalls++;
    }

    // A mod that patches the compile expects its patch to run; the cache and the kernel would bypass it, so they step aside
    [Test]
    public void AForeignPatchOnTheCompileKeepsTheEngine()
    {
        using var foreign = new TestHarmony("komet-test-foreign");
        _ = foreign.Patch(AccessTools.Method(typeof(Animation), "GenerateFrameForElement"),
            postfix: new HarmonyMethod(typeof(AnimationFramesTests), nameof(ForeignRan)));
        Patch();
        Assert.That(AnimationFrames.Blocked, Is.True);
        _foreignCalls = 0;
        _ = Generated(Fresh());
        Assert.That(_foreignCalls, Is.GreaterThan(0), "the engine did not compile");
    }

    // A miss is the kernel's, not the engine's: a foreign postfix the recheck has not seen yet does not run, and runs once it has
    [Test]
    public void AMissIsCompiledByTheKernel()
    {
        Patch();
        Counting.Hud = true;
        var misses = AnimationFrames.Misses;
        using var foreign = new TestHarmony("komet-test-foreign-kernel");
        _ = foreign.Patch(AccessTools.Method(typeof(Animation), "GenerateFrameForElement"),
            postfix: new HarmonyMethod(typeof(AnimationFramesTests), nameof(ForeignRan)));
        _foreignCalls = 0;
        _ = Generated(Fresh());
        Assert.That((_foreignCalls, AnimationFrames.Misses - misses, AnimationFrames.Blocked),
            Is.EqualTo((0, 1L, false)));
        AnimationFrames.Recheck();
        _ = Generated(Fresh("engine"));
        Assert.That(_foreignCalls, Is.GreaterThan(0), "the engine did not compile");
    }

    // Patches are process wide and another mod may apply its own after Komet installed: the recheck on LevelFinalize sees it and
    // logs it once, however often it is asked
    [Test]
    public void AForeignPatchAppliedAfterInstallIsSeenByTheRecheck()
    {
        var logger = new CapturingLogger();
        Patch(logger);
        using var foreign = new TestHarmony("komet-test-foreign-late");
        _ = foreign.Patch(AccessTools.Method(typeof(Animation), "seekLeftKeyFrame"), transpiler: Foreign.Transpiler);
        Assert.That(AnimationFrames.Blocked, Is.False);
        AnimationFrames.Recheck();
        AnimationFrames.Recheck();
        Assert.That(AnimationFrames.Blocked, Is.True);
        Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("another mod"));
    }

    // Only render-thread compiles are timed, the ones that land in a frame, fast or the engine's, and the slowest is named
    [Test]
    public void TheSlowestCompileOfTheIntervalIsNamed()
    {
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        Patch();
        Counting.Hud = true;
        _ = Generated(Fresh());
        Assert.That((AnimationFrames.WorstCode, AnimationFrames.WorstMs > 0), Is.EqualTo(("test", true)));
        AnimationFrames.ResetPeaks();
        AnimationFrames.Enabled = false;
        _ = Generated(Fresh("engine"));
        Assert.That((AnimationFrames.WorstCode, AnimationFrames.WorstMs > 0), Is.EqualTo(("engine", true)));
        AnimationFrames.ResetPeaks();
        Assert.That((AnimationFrames.WorstMs, AnimationFrames.WorstCode), Is.EqualTo((0.0, "")));
    }

    [Test]
    public void OffTheRenderThreadNothingIsTimed()
    {
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId + 1;
        Patch();
        Counting.Hud = true;
        Assert.Multiple(() =>
        {
            Assert.That(Generated(Fresh()), Is.Not.Null);
            Assert.That(AnimationFrames.WorstMs, Is.Zero);
        });
    }

    // Animation.Clone shares jointsDone with the original; the clone gets its own, so two threads never clear one set
    [Test]
    public void ACloneHasItsOwnJointBookkeeping()
    {
        Patch();
        var animation = Fresh().Animations[0];
        Assert.That(JointsDone.GetValue(animation.Clone()), Is.Not.Null.And.Not.SameAs(JointsDone.GetValue(animation)));
    }

    // Equality that is not by reference: FastSmallDictionary's lookup calls the element's own Equals, which a subclass may
    // override. Here two "hand" elements are equal by name, so the engine poses both from the one the keyframes name.
    private sealed class ByName : ShapeElement
    {
        public override bool Equals(object? obj) =>
            obj is ShapeElement other && string.Equals(other.Name, Name, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            return 0;
            // equal by a mutable name, so no hash but a constant is safe
        }
    }
}
