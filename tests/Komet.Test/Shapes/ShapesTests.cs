using Komet.Shapes;

namespace Komet.Test.Shapes;

public sealed class ElementWalkTests
{
    private static readonly string[] Visited = ["a@0<-1", "b@1<0", "null@1<0", "c@1<0", "d@2<3", "e@0<-1"];
    private static readonly string[] Skipped = ["a@0<-1", "b@1<0", "d@0<-1", "e@1<2"];

    private static ShapeElement E(string name, params ShapeElement[] children)
    {
        return new ShapeElement { Name = name, Children = children.Length == 0 ? null : children };
    }

    private static List<string> Walk(ShapeElement[] roots, string? skip = null)
    {
        var visits = new List<string>();
        var walk = new ElementWalk(roots);
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element)) break;
            visits.Add($"{element?.Name ?? "null"}@{walk.Depth}<{walk.Parent}");
            if (element?.Name == skip) walk.Skip();
        }

        if (walk.Failed) visits.Add("failed");
        return visits;
    }

    private static string Name(int i) => i.ToString(CultureInfo.InvariantCulture);

    private static ShapeElement[] Chain(int length)
    {
        var root = E(Name(0));
        var last = root;
        for (var i = 1; i < length; i++)
        {
            var next = E(Name(i));
            last.Children = [next];
            last = next;
        }

        return [root];
    }

    [Test]
    public void PreOrderWithDepthAndParentNullsIncluded()
    {
        ShapeElement[] roots = [E("a", E("b"), null!, E("c", E("d"))), E("e")];
        Assert.That(Walk(roots), Is.EqualTo(Visited));
    }

    [Test]
    public void SkippedChildrenAreNotVisited()
    {
        ShapeElement[] roots = [E("a", E("b", E("c"))), E("d", E("e"))];
        Assert.That(Walk(roots, "b"), Is.EqualTo(Skipped));
    }

    [Test]
    public void DeeperThanMaxDepthFailsUnlessSkipped()
    {
        var deepest = Walk(Chain(ElementWalk.MaxDepth));
        var tooDeep = Chain(ElementWalk.MaxDepth + 1);
        Assert.Multiple(() =>
        {
            Assert.That(deepest, Has.Count.EqualTo(ElementWalk.MaxDepth).And.No.Contains("failed"));
            Assert.That(Walk(tooDeep), Has.Count.EqualTo(ElementWalk.MaxDepth + 1).And.Contains("failed"));
            Assert.That(Walk(tooDeep, Name(ElementWalk.MaxDepth - 1)), Does.Not.Contain("failed"));
        });
    }

    [Test]
    public void MoreThanMaxElementsFails()
    {
        var most = Enumerable.Range(0, ElementWalk.MaxElements).Select(_ => E("x")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(Walk(most), Has.Count.EqualTo(ElementWalk.MaxElements).And.No.Contains("failed"));
            Assert.That(Walk([.. most, E("y")]), Has.Count.EqualTo(ElementWalk.MaxElements + 1).And.Contains("failed"));
        });
    }
}

// The descriptor is only worth having if it implies the thing the cache trades on: two shapes that describe the same compile to the
// same poses, checked with the engine's own Animation.GenerateAllFrames over separately built shapes. And the compile replaces the
// engine's on every miss, while the animator indexes what it returns by position without checking anything: a pose one bit off is a
// wrong pose on screen. So "bit-identical" is checked against the engine over every animation the game ships and over the player's
// shape, step-parented the way the game builds it.
public sealed class AnimationFramesGoldenTests
{
    private static ShapeElement Element(string name, params ShapeElement[] children)
    {
        return new ShapeElement
        {
            Name = name,
            From = [0, 0, 0],
            To = [1, 1, 1],
            RotationOrigin = [0, 0, 0],
            Children = children.Length == 0 ? null : children
        };
    }

    // A humanoid arm with a gear piece step-parented onto it, which is the shape the cache exists for
    private static ShapeElement[] Arm(bool geared)
    {
        return [Element("upperarm", geared ? [Element("lowerarm"), Element("sleeve")] : [Element("lowerarm")])];
    }

    // A group has to be set whole: IsSet reports PositionSet when any one of OffsetX/Y/Z has a value, but
    // lerpKeyFrameElement then reads all three through .Value. A shape's own json always writes complete triples.
    private static AnimationKeyFrameElement Rotated(double x, double y, double z)
    {
        return new AnimationKeyFrameElement { RotationX = x, RotationY = y, RotationZ = z };
    }

    private static Animation Walk(double upper, double lower)
    {
        var first = Rotated(upper, 0, 0);
        (first.OffsetX, first.OffsetY, first.OffsetZ) = (0, 1, 0);
        var second = Rotated(-upper, 0, 0);
        (second.StretchX, second.StretchY, second.StretchZ) = (1, 2, 1);
        return new Animation
        {
            Code = "walk",
            QuantityFrames = 8,
            KeyFrames =
            [
                new AnimationKeyFrame
                {
                    Frame = 0,
                    Elements = new Dictionary<string, AnimationKeyFrameElement>
                    {
                        ["upperarm"] = first,
                        ["lowerarm"] = Rotated(0, 0, lower)
                    }
                },
                new AnimationKeyFrame
                {
                    Frame = 4,
                    Elements = new Dictionary<string, AnimationKeyFrameElement> { ["upperarm"] = second }
                }
            ]
        };
    }

    private static AnimationFrame[][] Compile(Animation animation, ShapeElement[] roots)
    {
        var byName = AnimationShapes.Elements(roots).ToDictionary(e => e.Name!);
        foreach (var key in animation.KeyFrames) key.Resolve(byName);
        animation.GenerateAllFrames(roots, []);
        return animation.PrevNextKeyFrameByFrame;
    }

    // The property the cache is built on, for the geared shape it was built for
    [TestCase(true)]
    [TestCase(false)]
    public void TheSameDescriptorMeansTheSameCompiledPoses(bool geared)
    {
        var (one, two) = (Walk(30, 10), Walk(30, 10));
        var (rootsOne, rootsTwo) = (Arm(geared), Arm(geared));
        var (compiledOne, compiledTwo) = (Compile(one, rootsOne), Compile(two, rootsTwo));
        Assert.That(AnimationFramesTests.Descriptor(one, rootsOne),
            Is.Not.Null.And.EqualTo(AnimationFramesTests.Descriptor(two, rootsTwo)),
            "separately built but structurally identical shapes must describe the same");
        Assert.That(AnimationShapes.Diff(compiledOne, compiledTwo, true), Is.Null);
    }

    // And the converse where it matters: step-parented gear really does compile differently, so separating it is not
    // conservatism but necessity. A shared entry here would animate a geared entity with a bare entity's pose tree.
    [Test]
    public void StepParentedGearCompilesToADifferentShapeOfPoseTree()
    {
        var (bare, geared) = (Walk(30, 10), Walk(30, 10));
        var (rootsBare, rootsGeared) = (Arm(false), Arm(true));
        var (compiledBare, compiledGeared) = (Compile(bare, rootsBare), Compile(geared, rootsGeared));
        Assert.That(AnimationFramesTests.Descriptor(bare, rootsBare),
            Is.Not.Null.And.Not.EqualTo(AnimationFramesTests.Descriptor(geared, rootsGeared)));
        var posesBare = compiledBare[0][0].RootElementTransforms[0].ChildElementPoses.Count;
        var posesGeared = compiledGeared[0][0].RootElementTransforms[0].ChildElementPoses.Count;
        Assert.That(posesGeared, Is.Not.EqualTo(posesBare),
            "the gear adds a child pose, so the trees cannot be indexed interchangeably");
    }

    // A changed keyframe value has to reach the compiled pose, or the descriptor would be separating for nothing
    [Test]
    public void ADifferentKeyframeValueCompilesDifferently()
    {
        var (one, two) = (Walk(30, 10), Walk(45, 10));
        var (rootsOne, rootsTwo) = (Arm(true), Arm(true));
        var (compiledOne, compiledTwo) = (Compile(one, rootsOne), Compile(two, rootsTwo));
        Assert.That(AnimationFramesTests.Descriptor(one, rootsOne),
            Is.Not.Null.And.Not.EqualTo(AnimationFramesTests.Descriptor(two, rootsTwo)));
        Assert.That(compiledOne[0][0].RootElementTransforms[0].degX,
            Is.Not.EqualTo(compiledTwo[0][0].RootElementTransforms[0].degX));
    }

    // Engine first, then Komet's compile on the same Animation. Where the engine throws, the compile must decline or throw the same
    // exception type out of the engine method they share (CacheInverseTransformMatrixRecursive); where it does not, it must not
    // decline and must match it bit for bit.
    private static int Check(Shape shape, string name, List<string> failures)
    {
        var rejected = 0;
        foreach (var animation in shape.Animations ?? [])
        {
            Exception? thrown = null;
            try
            {
                animation.GenerateAllFrames(shape.Elements, shape.JointsById);
            }
            catch (Exception e)
            {
                thrown = e;
            }

            var engine = animation.PrevNextKeyFrameByFrame;
            animation.PrevNextKeyFrameByFrame = null;
            bool compiled;
            try
            {
                compiled = AnimationFrames.Compile(animation, shape.Elements, shape.JointsById);
            }
            catch (Exception e) when (thrown != null && e.GetType() == thrown.GetType())
            {
                compiled = false;
            }

            if (thrown != null)
            {
                rejected++;
                if (compiled)
                    failures.Add($"{name}/{animation.Code}: the engine threw {thrown.GetType().Name}, Komet compiled");
                continue;
            }

            if (!compiled) failures.Add($"{name}/{animation.Code}: declined");
            else if (AnimationShapes.Diff(engine, animation.PrevNextKeyFrameByFrame) is { } diff)
                failures.Add($"{name}/{animation.Code}: {diff}");
            // what the game ships holds no object twice, so the cache answers it
            if (AnimationFramesTests.Descriptor(animation, shape.Elements) is null)
                failures.Add($"{name}/{animation.Code}: not described");
        }

        return rejected;
    }

    [Test]
    [Category("Slow")]
    public void EveryAnimatedVanillaShapeCompilesBitIdentical()
    {
        GameInstall.RequireAssets();
        var failures = new List<string>();
        int shapes = 0, animations = 0, rejected = 0;
        foreach (var (domain, path, text) in AnimationShapes.Animated())
        {
            Shape shape;
            try
            {
                shape = AnimationShapes.Resolve(AnimationShapes.Parse(domain, path, text), path);
            }
            catch (Exception e) when (e is not AssertionException)
            {
                continue; // a file the game would not load either
            }

            if (shape.Animations is not { Length: > 0 } || shape.Elements is null) continue;
            rejected += Check(shape, $"{domain}:{path}", failures);
            shapes++;
            animations += shape.Animations.Length;
        }

        TestContext.Out.WriteLine($"{shapes} shapes, {animations} animations, {rejected} the engine rejects");
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
            Assert.That(shapes, Is.GreaterThan(600),
                "the shape walk found far fewer animated shapes than the game ships");
            Assert.That(animations, Is.GreaterThan(5000));
        });
    }

    // Seraph-faceless plus four skin parts and seven or eight pieces of gear, reparented by Shape.StepParentShape with the gear's
    // keyframes merged into the player's animations: the shape the cache and the compile exist for
    [TestCase("naked")]
    [TestCase("clothed")]
    [TestCase("armored")]
    [Category("Slow")]
    public void TheComposedPlayerCompilesBitIdentical(string outfit)
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.Player(outfit);
        var failures = new List<string>();
        var rejected = Check(shape, "player-" + outfit, failures);
        var elements = AnimationShapes.Elements(shape.Elements).Count;
        TestContext.Out.WriteLine(
            $"{outfit}: {elements} elements, {shape.JointsById.Count} joints, {shape.Animations.Length} animations");
        Assert.Multiple(() =>
        {
            Assert.That(failures, Is.Empty, string.Join("\n", failures.Take(20)));
            Assert.That(rejected, Is.Zero, "every player animation compiles in the engine");
            Assert.That(shape.Animations, Has.Length.GreaterThan(200));
            Assert.That(elements, outfit == "naked" ? Is.GreaterThan(20) : Is.GreaterThan(120),
                "the gear did not step-parent");
        });
    }

    // calculateMatrices multiplies by every element's inverseModelTransform; GenerateAllFrames fills it first thing, so the
    // replacement has to as well – even for a shape nobody called CacheInvTransforms on
    [Test]
    public void EveryElementHasItsInverseTransformAfterwards()
    {
        GameInstall.RequireAssets();
        var shape = AnimationShapes.Resolve(AnimationShapes.Load("survival", "entity/lore/shiver/surface"), "shiver");
        foreach (var element in AnimationShapes.Elements(shape.Elements)) element.inverseModelTransform = null;
        var animation = shape.Animations.First(a => a.KeyFrames is { Length: > 0 });
        Assert.That(AnimationFrames.Compile(animation, shape.Elements, shape.JointsById), Is.True);
        Assert.That(
            AnimationShapes.Elements(shape.Elements).Where(e => e.inverseModelTransform is null).Select(e => e.Name),
            Is.Empty);
    }
}
