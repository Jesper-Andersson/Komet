using Komet.Chunks;
using Komet.Core;
using Komet.Tessellation;
using Komet.World;
using Vintagestory.ServerMods;
using ColumnNoise = Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise.ColumnNoise;

namespace Komet.Test.Core;

public sealed class IlTests
{
    private static readonly ConstructorInfo Queue = AccessTools.Constructor(typeof(QueueOfInt), []);

    private static Site Feature(string name)
    {
        _ = typeof(GenTerra).Assembly; // VSEssentials, which holds the cloud renderer, is found by name
        var decompress = AccessTools.Method(typeof(ICompression), nameof(ICompression.Decompress),
            [typeof(byte[]), typeof(int), typeof(int)]);
        return name switch
        {
            nameof(CloudTileScratch) => new Site(CloudTileScratch.Target()!,
                c => c.opcode == OpCodes.Newobj && Equals(c.operand, CloudTileScratch.Constructor()),
                AccessTools.Method(typeof(CloudTileScratch), nameof(CloudTileScratch.At)), true,
                CloudTileScratch.Substitute),
            nameof(DecompressScratch) => new Site(DecompressScratch.Target()!, c => c.Calls(decompress),
                AccessTools.Method(typeof(DecompressScratch), nameof(DecompressScratch.Decompress)), false,
                DecompressScratch.Rewrite),
            _ => new Site(LightScratch.Method("SpreadDarkness")!,
                c => c.opcode == OpCodes.Newobj && Equals(c.operand, Queue),
                AccessTools.Method(typeof(LightScratch), nameof(LightScratch.Queue)), true,
                LightScratch.RewriteDarkness)
        };
    }

    [Test]
    public void TakeKeepsTheWholeBody()
    {
        var original = PatchProcessor.GetOriginalInstructions(DecompressScratch.Target()!);
        Assert.Multiple(() =>
        {
            Assert.That(Il.Take(original, original.Count, out var all), Is.True);
            Assert.That(all, Is.EqualTo(original).And.Not.SameAs(original));
            Assert.That(Il.Take(original, original.Count - 1, out var over), Is.False, "one past the cap");
            Assert.That(over, Is.EqualTo(original), "rejected whole, not cut");
        });
    }

    // Single finds the one site the feature's transpiler rewrites, and Substitute turns it into the same call in place; where that is
    // all the feature changes, the whole body is the feature's
    [TestCase(nameof(CloudTileScratch))]
    [TestCase(nameof(DecompressScratch))]
    [TestCase(nameof(LightScratch))]
    public void SingleFindsAndSubstituteRewritesTheFeaturesSite(string feature)
    {
        var site = Feature(feature);
        var theirs = site.Transpile(PatchProcessor.GetOriginalInstructions(site.Target));
        var code = PatchProcessor.GetOriginalInstructions(site.Target);
        var at = Il.Single(code, site.Match);
        Assert.That(at, Is.GreaterThanOrEqualTo(0).And.EqualTo(theirs.FindIndex(c => c.Calls(site.Mine))),
            "the site the feature rewrites");
        var before = code[at];
        Assert.Multiple(() =>
        {
            Assert.That(Il.Count(code, site.Match), Is.EqualTo(1));
            Assert.That(Il.Single(code, c => c.opcode.FlowControl == FlowControl.Call), Is.EqualTo(-1), "several");
            Assert.That(Il.Single(code, _ => false), Is.EqualTo(-1), "none");
            Assert.That(Il.Substitute(code, at, site.Mine), Is.True);
            Assert.That(code[at], Is.SameAs(before), "in place: labels and exception blocks stay on it");
            Assert.That((code[at].opcode, code[at].operand), Is.EqualTo((theirs[at].opcode, theirs[at].operand)));
            if (site.OnlyThere)
                Assert.That(code.Select(Shape), Is.EqualTo(theirs.Select(Shape)), "the feature's whole body");
        });
    }

    [Test]
    public void SubstituteRefusesAnotherStackEffect()
    {
        var site = Feature(nameof(CloudTileScratch));
        var code = PatchProcessor.GetOriginalInstructions(site.Target);
        var at = Il.Single(code, site.Match);
        var abs = AccessTools.Method(typeof(Math), nameof(Math.Abs), [typeof(double)]);
        Assert.Multiple(() =>
        {
            Assert.That(Il.Substitute(code, at, abs), Is.False, "one double where new Vec3d takes three");
            Assert.That((code[at].opcode, code[at].operand),
                Is.EqualTo((OpCodes.Newobj, (object)CloudTileScratch.Constructor()!)));
        });
    }

    [Test]
    public void LocalReadsTheIndexHarmonyReads()
    {
        MethodBase?[] methods =
        [
            LightScratch.Method("CollectLightValuesForLightSource"), LightScratch.Method("UpdateLightAt"), ColumnBody(),
            ExtendedRows.Target()
        ];
        var seen = Il.Uses.None;
        foreach (var method in methods)
        {
            Assert.That(method, Is.Not.Null);
            foreach (var c in PatchProcessor.GetOriginalInstructions(method!))
            {
                var kind = Kind(c);
                var index = kind == Il.Uses.None ? -1 : c.LocalIndex();
                seen |= kind;
                Assert.That(Il.Local(c), Is.EqualTo(index), c.ToString());
                Assert.That((Il.Local(c, Il.Uses.Load), Il.Local(c, Il.Uses.Address), Il.Local(c, Il.Uses.Store)),
                    Is.EqualTo((kind == Il.Uses.Load ? index : -1, kind == Il.Uses.Address ? index : -1,
                        kind == Il.Uses.Store ? index : -1)), c.ToString());
            }
        }

        Assert.That(seen, Is.EqualTo(Il.Uses.Any), "every kind came up");
    }

    // The value pushed at an index goes to the instruction that pops it, as the operand it lands in; a jump or a label on the way, or
    // a walk past its reach, proves nothing
    [Test]
    public void ConsumerFollowsTheStack()
    {
        var max = AccessTools.Method(typeof(Math), nameof(Math.Max), [typeof(int), typeof(int)]);
        var length = AccessTools.PropertyGetter(typeof(string), nameof(string.Length));
        List<CodeInstruction> sum =
        [
            new(OpCodes.Ldloc_0), new(OpCodes.Ldc_I4_1), new(OpCodes.Ldc_I4_2), new(OpCodes.Add),
            new(OpCodes.Call, max), new(OpCodes.Pop)
        ];
        List<CodeInstruction> labelled =
            [new(OpCodes.Ldloc_0), new(OpCodes.Ldc_I4_1), new(OpCodes.Call, max) { labels = [new Label()] }];
        Assert.Multiple(() =>
        {
            Assert.That(Il.Consumer(sum, 0), Is.EqualTo((4, 0)), "under what add left, the first of Max");
            Assert.That(Il.Consumer(sum, 2), Is.EqualTo((3, 1)), "the second of add");
            Assert.That(Il.Consumer(sum, 4), Is.EqualTo((5, 0)));
            Assert.That(Il.Consumer(sum, 0, 3), Is.EqualTo((-1, -1)), "past its reach");
            Assert.That(Il.Consumer(labelled, 0), Is.EqualTo((-1, -1)), "a jump could land with another stack");
            Assert.That(
                Il.Consumer([new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Br, new Label())], 0),
                Is.EqualTo((-1, -1)));
            Assert.That(Il.Consumer([new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Dup)], 0),
                Is.EqualTo((1, 0)), "a copy is a use");
            Assert.That(
                Il.Consumer([new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Callvirt, length)], 0),
                Is.EqualTo((1, 0)), "`this`");
            Assert.That(Il.Effect(new CodeInstruction(OpCodes.Newobj, CloudTileScratch.Constructor())),
                Is.EqualTo((3, 1)));
            Assert.That(Il.Effect(new CodeInstruction(OpCodes.Ret)), Is.EqualTo((-1, -1)), "depends on the method");
        });
    }

    // LightScratch rewrites the node walks' queue: every load of it is the receiver of Enqueue, Dequeue or Count. Without Count
    // allowed, the same local escapes.
    [Test]
    public void ConfinedAcceptsTheQueueLightScratchRewrites()
    {
        var enqueue = AccessTools.Method(typeof(QueueOfInt), nameof(QueueOfInt.Enqueue), [typeof(int)]);
        var dequeue = AccessTools.Method(typeof(QueueOfInt), nameof(QueueOfInt.Dequeue));
        var count = AccessTools.Field(typeof(QueueOfInt), nameof(QueueOfInt.Count));
        var mine = AccessTools.Method(typeof(LightScratch), nameof(LightScratch.Queue));
        foreach (var name in (string[])["SpreadDarkness", "CollectLightValuesForLightSource"])
        {
            var method = LightScratch.Method(name)!;
            var code = PatchProcessor.GetOriginalInstructions(method);
            var store = Il.Single(code, c => c.opcode == OpCodes.Newobj && Equals(c.operand, Queue)) + 1;
            // a transpiler rewrites the instructions themselves
            var fresh = PatchProcessor.GetOriginalInstructions(method);
            var theirs = name == "SpreadDarkness"
                ? LightScratch.RewriteDarkness(fresh)
                : LightScratch.RewriteCollect(fresh);
            Assert.Multiple(() =>
            {
                Assert.That(theirs.Exists(c => c.Calls(mine)), Is.True, name);
                Assert.That(Il.Confined(code, store,
                    (c, operand) => operand == 0 && (c.Calls(enqueue) || c.Calls(dequeue) || c.LoadsField(count))),
                    Is.True, name);
                Assert.That(
                    Il.Confined(code, store, (c, operand) => operand == 0 && (c.Calls(enqueue) || c.Calls(dequeue))),
                    Is.False, name);
            });
        }
    }

    // GenTerra's column body only addresses its ColumnNoise for the struct's own double members, and ColumnNoiseScratch rewrites it.
    // The column copied, loaded through its address or overwritten escapes, and ColumnNoiseScratch leaves those bodies alone too.
    [Test]
    public void ConfinedAcceptsTheColumnColumnNoiseScratchRewrites()
    {
        var body = ColumnBody();
        Assert.That(body, Is.Not.Null);
        var forColumn = ColumnNoiseScratch.ForColumn()!;
        var original = PatchProcessor.GetOriginalInstructions(body!);
        var store = Il.Single(original, c => c.Calls(forColumn)) + 1;
        var local = original[store].operand;

        List<CodeInstruction> Variant(params CodeInstruction[] inserted)
        {
            var code = PatchProcessor.GetOriginalInstructions(body!);
            code.InsertRange(store + 1, inserted);
            return code;
        }

        bool Confined(List<CodeInstruction> code, Il.Uses uses) =>
            Il.Confined(code, store, Member, uses, 6);

        var variants = new[]
        {
            Variant(new CodeInstruction(OpCodes.Ldloc_S, local), new CodeInstruction(OpCodes.Pop)),
            Variant(new CodeInstruction(OpCodes.Ldloca_S, local),
                new CodeInstruction(OpCodes.Ldobj, typeof(ColumnNoise)), new CodeInstruction(OpCodes.Pop)),
            Variant(new CodeInstruction(OpCodes.Ldloca_S, local),
                new CodeInstruction(OpCodes.Initobj, typeof(ColumnNoise)))
        };
        Assert.Multiple(() =>
        {
            Assert.That(Confined(original, Il.Uses.Address), Is.True);
            Assert.That(ColumnNoiseScratch.RewriteBody(Variant()).Exists(c =>
                c.Calls(AccessTools.Method(typeof(ColumnNoiseScratch), nameof(ColumnNoiseScratch.Column)))), Is.True);
            Assert.That(Confined(original, Il.Uses.Load), Is.False, "it is only ever addressed");
            foreach (var code in variants)
            {
                Assert.That(Confined(code, Il.Uses.Address | Il.Uses.Load), Is.False);
                Assert.That(ColumnNoiseScratch.RewriteBody(code).Exists(c =>
                    c.operand is MethodInfo { DeclaringType: var t } && t == typeof(ColumnNoiseScratch)), Is.False);
            }
        });
    }

    private static MethodInfo? ColumnBody()
    {
        _ = typeof(GenTerra).Assembly; // VSEssentials is found by name, so it has to be loaded first
        return ColumnNoiseScratch.ColumnBody();
    }

    private static bool Member(CodeInstruction c, int operand) =>
        operand == 0 && c.opcode == OpCodes.Call && c.operand is MethodInfo { IsStatic: false } m &&
            m.DeclaringType == typeof(ColumnNoise) && m.ReturnType == typeof(double);

    // Each call of GetOriginalInstructions declares its own LocalBuilders: a local by its index
    private static (OpCode, object?) Shape(CodeInstruction code) =>
        (code.opcode, code.operand is LocalBuilder local ? local.LocalIndex : code.operand);

    private static Il.Uses Kind(CodeInstruction code)
    {
        if (code.IsStloc()) return Il.Uses.Store;
        if (!code.IsLdloc()) return Il.Uses.None;
        return code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S ? Il.Uses.Address : Il.Uses.Load;
    }

    private sealed record Site(MethodBase Target, System.Func<CodeInstruction, bool> Match, MethodInfo Mine,
        bool OnlyThere, System.Func<IEnumerable<CodeInstruction>, List<CodeInstruction>> Transpile);
}
