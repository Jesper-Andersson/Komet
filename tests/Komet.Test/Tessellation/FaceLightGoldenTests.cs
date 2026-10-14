using Komet.Core;
using Komet.Tessellation;
using Vintagestory.GameContent;

namespace Komet.Test.Tessellation;

// Golden test of FaceLight against the engine's own TCTCache.CalcBlockFaceLight and JsonTesselator.SetUpLightRGBs, unpatched, on the
// same cells. The long sum, CurrentLightRGBByCorner and every entry of neighbourLightRGBS (and json light) must match where the fast
// path answers; where it does not it must have written nothing and called nothing, and it has to answer exactly the faces whose
// blocks are all plain and whose multipliers the lanes can take.
public sealed class FaceLightGoldenTests
{
    private const int Cases = 50_000, JsonBlocks = 100_000, Seed = 20260924, Filler = 0x5A5A5A5A;

    private static readonly int[] SpecialLights =
        [0x00030303, unchecked((int)0xFF000000), 0, -1, 0x18FFFFFF, 0x7F7F7F7F];

    private static readonly int[] Absorptions = [0, 0, 0, 1, 2, 31, 32, 33, 99, -3];

    private static int _seen;

    private static int Seen => Volatile.Read(ref _seen);

    [TearDown]
    public void Reset() => (FaceLight.Enabled, Counting.Hud) = (true, false);

    [Test]
    [Category("Slow")]
    public void FacesMatchTheEngine()
    {
        var r = new Random(Seed);
        var palette = Palette(r);
        using var rig = new TessRig(palette);
        int fast = 0, engine = 0;
        for (var i = 0; i < Cases; i++)
        {
            var e = Neighbourhood(rig, r, palette);
            Settings(rig.Vars, r, i);
            for (var t = 0; t < 6; t++)
            {
                var front = r.Next(16) == 0 ? e + TessRig.Moves[r.Next(6)] : e + TessRig.Moves[t];
                if (Face(rig, t, front)) fast++;
                else engine++;
            }
        }

        TestContext.Out.WriteLine($"faces lit fast {fast}, by the engine {engine}");
        Assert.That(fast, Is.GreaterThan(Cases), "a fair share of the faces is plain");
    }

    // The front occludes or not, all eight samples occlude, none do, leaves in leaves, water in front, absorption 32 and 33
    [Test]
    public void TargetedNeighbourhoodsMatchTheEngine()
    {
        var r = new Random(Seed + 1);
        var palette = Palette(r);
        using var rig = new TessRig(palette);
        Block[] picks = [palette[1], palette[2], palette[3], palette[4], palette[5]];
        (picks[0].EmitSideAo, picks[0].LightAbsorption, picks[0].BlockMaterial) = (63, 99, EnumBlockMaterial.Stone);
        (picks[1].EmitSideAo, picks[1].LightAbsorption, picks[1].BlockMaterial) = (0, 0, EnumBlockMaterial.Air);
        (picks[2].EmitSideAo, picks[2].LightAbsorption, picks[2].BlockMaterial) = (0, 1, EnumBlockMaterial.Leaves);
        (picks[3].EmitSideAo, picks[3].LightAbsorption, picks[3].BlockMaterial) = (21, 32, EnumBlockMaterial.Leaves);
        (picks[4].EmitSideAo, picks[4].LightAbsorption, picks[4].BlockMaterial) = (42, 33, EnumBlockMaterial.Stone);
        var water = palette.OfType<BlockForFluidsLayer>().First(block => block.LightAbsorption > 0);
        var cases = 0;
        for (var mask = 0; mask < 1 << 10; mask++)
        {
            var e = Neighbourhood(rig, r, palette);
            for (var i = 0; i < 27; i++)
            {
                var (dx, dz, dy) = (i % 3 - 1, i / 3 % 3 - 1, i / 9 - 1);
                var p = e + dy * TessRig.Plane + dz * TessRig.Ext + dx;
                rig.Solid[p] = picks[((mask >> ((dx + 1) * 3 + dz + dy + 2)) & 1) + (mask >> 8)];
                rig.Fluid[p] = (mask & 16) != 0 && dy == 1 ? water : palette[0];
            }

            rig.Vars.block = picks[mask % 5];
            (rig.Vars.aoAndSmoothShadows, rig.Vars.occ, rig.Vars.halfoccInverted) = (true, 0.67f, 0.0196875f);
            for (var t = 0; t < 6; t++, cases++) _ = Face(rig, t, e + TessRig.Moves[t]);
        }

        Assert.That(cases, Is.EqualTo(6 << 10));
    }

    // SetUpLightRGBs: the fused six faces against the engine's, including blocks where only some faces take the engine's path, both on
    // the vars' own scratch and from the same junk
    [Test]
    [Category("Slow")]
    public void FusedJsonLightMatchesTheEngine()
    {
        var r = new Random(Seed + 2);
        var palette = Palette(r);
        using var rig = new TessRig(palette);
        var json = new JsonTesselator();
        int fused = 0, handedBack = 0;
        for (var i = 0; i < JsonBlocks; i++)
        {
            _ = Neighbourhood(rig, r, palette);
            Settings(rig.Vars, r, i);
            var vars = rig.Vars;
            Junk(vars);
            AoRecordingBlock.Log = [];
            var engineSum = json.SetUpLightRGBs(vars);
            var engineJson = (int[])TessRig.JsonLight(json).Clone();
            var (engineCorners, engineNeighbours) = ((int[])vars.CurrentLightRGBByCorner.Clone(),
                (int[])TessRig.Neighbours(vars).Clone());
            var engineLog = AoRecordingBlock.Log;
            AoRecordingBlock.Log = [];
            Junk(vars);
            int[] mine = [.. Enumerable.Repeat(Filler, 25)];
            if (!FaceLight.Six(vars, mine, out var sum))
            {
                Assert.That(vars.CurrentLightRGBByCorner.Concat(TessRig.Neighbours(vars)).Concat(mine),
                    Is.All.EqualTo(Filler), "written though handed back");
                handedBack++;
                continue;
            }

            fused++;
            Assert.Multiple(() =>
            {
                Assert.That(sum, Is.EqualTo(engineSum), $"sum of case {i}");
                Assert.That(mine, Is.EqualTo(engineJson), $"json light of case {i}");
                Assert.That(vars.CurrentLightRGBByCorner, Is.EqualTo(engineCorners), $"corners of case {i}");
                Assert.That(TessRig.Neighbours(vars).AsSpan(1).ToArray(),
                    Is.EqualTo(engineNeighbours.AsSpan(1).ToArray()), $"neighbours of case {i}");
                Assert.That(AoRecordingBlock.Log, Is.EqualTo(engineLog),
                    "the engine's faces called the same, in the same order");
            });
        }

        AoRecordingBlock.Log = null;
        TestContext.Out.WriteLine($"blocks fused {fused}, handed back {handedBack}");
        Assert.That(fused, Is.GreaterThan(JsonBlocks * 3 / 4));
    }

    // Only Block's own AO methods, or a ForFluidsLayer that is a constant, make a block plain
    [Test]
    public void KindsFollowTheOverrides()
    {
        List<MethodBase> watched = [];
        Assert.Multiple(() =>
        {
            Assert.That(FaceLight.Classify(typeof(Block), watched), Is.EqualTo(1));
            Assert.That(FaceLight.Classify(typeof(BlockForFluidsLayer), watched), Is.EqualTo(2));
            Assert.That(FaceLight.Classify(typeof(BlockWater), watched), Is.EqualTo(2));
            Assert.That(FaceLight.Classify(typeof(BlockLakeIce), watched), Is.EqualTo(2));
            Assert.That(FaceLight.Classify(typeof(BlockMicroBlock), watched), Is.Zero);
            Assert.That(FaceLight.Classify(typeof(AoRecordingBlock), watched), Is.Zero);
            Assert.That(FaceLight.Classify(typeof(FalseFluidsBlock), watched), Is.EqualTo(1));
            Assert.That(FaceLight.Classify(typeof(ComputedFluidsBlock), watched), Is.Zero);
            Assert.That(FaceLight.Classify(typeof(BlockSoil), watched), Is.EqualTo(1));
        });
    }

    // A microblock anywhere the face looks sends the face to the engine, before anything is written
    [Test]
    public void MicroblockGoesToTheEngine()
    {
        var r = new Random(Seed + 3);
        var palette = Palette(r);
        var micro = (BlockMicroBlock)RuntimeHelpers.GetUninitializedObject(typeof(BlockMicroBlock));
        micro.BlockId = palette.Length - 1;
        palette[^1] = micro;
        using var rig = new TessRig(palette);
        var e = Neighbourhood(rig, r, palette);
        for (var i = 0; i < 27; i++)
            rig.Solid[e + Around(i)] = palette[1];
        rig.Fluid.AsSpan().Fill(palette[0]);
        (rig.Vars.block, rig.Vars.aoAndSmoothShadows) = (palette[1], true);
        palette[1].SideAo = new SmallBoolArray(63);
        rig.Solid[e + TessRig.Plane + 1] = micro; // a sample of the up face
        int[] corners = [Filler, Filler, Filler, Filler];
        var neighbours = Enumerable.Repeat(Filler, 9).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(FaceLight.Face(rig.Vars, 4, e + TessRig.Plane, corners, neighbours, out _), Is.False);
            Assert.That(corners, Is.All.EqualTo(Filler));
            Assert.That(neighbours, Is.All.EqualTo(Filler));
            Assert.That(FaceLight.Face(rig.Vars, 5, e - TessRig.Plane, corners, neighbours, out _), Is.True,
                "the down face does not see it");
        });
    }

    [Test]
    [Category("Slow")]
    public void PatchedMethodsMatchTheEngine()
    {
        var r = new Random(Seed + 4);
        var palette = Palette(r);
        using var rig = new TessRig(palette);
        var json = new JsonTesselator();
        using var harmony = new TestHarmony("komet-test-facelight");
        FaceLight.Install(harmony);
        Assert.That(FaceLight.Installed && FaceLight.FusedInstalled, Is.True);
        Counting.Hud = true;
        var (faces, blocks) = (FaceLight.FastFaces, FaceLight.FusedBlocks);
        for (var i = 0; i < 20_000; i++)
        {
            var e = Neighbourhood(rig, r, palette);
            Settings(rig.Vars, r, i);
            var t = i % 6;
            FaceLight.Enabled = false;
            var engine = Outputs(rig, () => TessRig.CalcBlockFaceLight(rig.Vars, t, e + TessRig.Moves[t]), json);
            var engineJson = Outputs(rig, () => json.SetUpLightRGBs(rig.Vars), json);
            FaceLight.Enabled = true;
            var mine = Outputs(rig, () => TessRig.CalcBlockFaceLight(rig.Vars, t, e + TessRig.Moves[t]), json);
            var mineJson = Outputs(rig, () => json.SetUpLightRGBs(rig.Vars), json);
            Assert.That(mine, Is.EqualTo(engine), $"face of case {i}");
            Assert.That(mineJson, Is.EqualTo(engineJson), $"json block of case {i}");
        }

        Assert.That(FaceLight.FastFaces - faces, Is.GreaterThan(20_000));
        Assert.That(FaceLight.FusedBlocks - blocks, Is.GreaterThan(0));
    }

    // A changed face body declines the whole install; a changed SetUpLightRGBs only the fused block. The log says why.
    [Test]
    public void AChangedEngineDeclinesTheInstall()
    {
        using var harmony = new TestHarmony("komet-test-facelight-changed");
        var logger = new CapturingLogger();
        FaceLight.Install(harmony, logger, FaceLight.Shape ^ 1);
        Assert.Multiple(() =>
        {
            Assert.That((FaceLight.Installed, FaceLight.FusedInstalled), Is.EqualTo((false, false)));
            Assert.That(harmony.GetPatchedMethods(), Is.Empty);
        });
        FaceLight.Install(harmony, logger, FaceLight.Shape, FaceLight.FusedShape ^ 1);
        Assert.Multiple(() =>
        {
            Assert.That((FaceLight.Installed, FaceLight.FusedInstalled), Is.EqualTo((true, false)),
                "the faces without the fused block");
            Assert.That(Harmony.GetPatchInfo(FaceLight.FusedTarget()), Is.Null);
            Assert.That(logger.Lines, Has.Count.EqualTo(2).And.All.Contains("1.22.7"));
        });
    }

    // Another mod's prefix on CalcBlockFaceLight would be skipped with the original: the engine lights every face while it is there
    [Test]
    public void AnotherModsPrefixSendsFacesToTheEngine()
    {
        var r = new Random(Seed + 7);
        var palette = Palette(r);
        using var rig = new TessRig(palette);
        using var harmony = new TestHarmony("komet-test-facelight-own");
        using var other = new TestHarmony("komet-test-facelight-other");
        FaceLight.Install(harmony);
        Counting.Hud = true;
        var faces = FaceLight.FastFaces;
        _ = other.Patch(FaceLight.Target(), Foreign.Prefix);
        FaceLight.Recheck();
        Assert.That(FaceLight.StoodDown, Is.True);
        for (var i = 0; i < 200; i++)
        {
            var e = Neighbourhood(rig, r, palette);
            Settings(rig.Vars, r, i);
            _ = TessRig.CalcBlockFaceLight(rig.Vars, i % 6, e + TessRig.Moves[i % 6]);
        }

        Assert.That(FaceLight.FastFaces, Is.EqualTo(faces));
        other.UnpatchAll(other.Id);
        FaceLight.Recheck();
        Assert.That(FaceLight.StoodDown, Is.False);
    }

    // A postfix on CalcBlockFaceLight sees every face the engine or the per-face prefix lights, but the fused JSON faces never enter
    // the method: while it is there, JSON blocks go through the engine's SetUpLightRGBs, and so through the method, face by face
    [Test]
    public void AForeignPostfixOnTheFaceSendsJsonBlocksToTheEngine()
    {
        using var rig = PlainNeighbourhood(out var e);
        var json = new JsonTesselator();
        using var harmony = new TestHarmony("komet-test-postfix-own");
        using var other = new TestHarmony("komet-test-postfix-other");
        FaceLight.Install(harmony);
        Counting.Hud = true;
        var blocks = FaceLight.FusedBlocks;
        _ = json.SetUpLightRGBs(rig.Vars);
        Assert.That((FaceLight.FusedBlocks - blocks, FaceLight.StoodDown), Is.EqualTo((1L, false)));
        Forget();
        _ = other.Patch(FaceLight.Target(), postfix: new HarmonyMethod(typeof(FaceLightGoldenTests), nameof(See)));
        FaceLight.Recheck();
        var (faces, fused) = (FaceLight.FastFaces, FaceLight.FusedBlocks);
        _ = json.SetUpLightRGBs(rig.Vars);
        _ = TessRig.CalcBlockFaceLight(rig.Vars, 4, e + TessRig.Plane);
        Assert.Multiple(() =>
        {
            Assert.That(FaceLight.StoodDown, Is.True);
            Assert.That(FaceLight.FusedBlocks - fused, Is.Zero, "the JSON block went to the engine");
            Assert.That(Seen, Is.EqualTo(7), "the postfix saw its six faces and the single face");
            Assert.That(FaceLight.FastFaces - faces, Is.EqualTo(7),
                "the faces themselves are still lit here, the postfix runs after");
        });
    }

    // Another prefix on SetUpLightRGBs concerns the fused block only: its faces then come through CalcBlockFaceLight, still fast
    [Test]
    public void AForeignPrefixOnTheJsonBlockLeavesTheFacesFast()
    {
        using var rig = PlainNeighbourhood(out _);
        var json = new JsonTesselator();
        using var harmony = new TestHarmony("komet-test-fusedprefix-own");
        using var other = new TestHarmony("komet-test-fusedprefix-other");
        FaceLight.Install(harmony);
        Counting.Hud = true;
        _ = other.Patch(FaceLight.FusedTarget(), Foreign.Prefix);
        FaceLight.Recheck();
        var (faces, fused) = (FaceLight.FastFaces, FaceLight.FusedBlocks);
        _ = json.SetUpLightRGBs(rig.Vars);
        Assert.Multiple(() =>
        {
            Assert.That(FaceLight.StoodDown, Is.True);
            Assert.That(FaceLight.FusedBlocks - fused, Is.Zero, "the engine's loop ran");
            Assert.That(FaceLight.FastFaces - faces, Is.EqualTo(6), "and lit its six faces through the fast face");
        });
    }

    // Any patch on what no fast face calls - CornerAoRGB, the AO methods the kind table stands for -: the engine lights every face
    [TestCase("CornerAoRGB", 28)] // four corners of seven faces
    [TestCase(nameof(Block.DoEmitSideAo), 7)] // the front of seven faces
    public void APatchOnWhatTheFacesSkipStandsBothDown(string name, int calls)
    {
        using var rig = PlainNeighbourhood(out var e);
        var json = new JsonTesselator();
        var skipped = name == nameof(Block.DoEmitSideAo) ? typeof(Block) : typeof(TCTCache);
        using var harmony = new TestHarmony("komet-test-corner-own");
        using var other = new TestHarmony("komet-test-corner-other");
        FaceLight.Install(harmony);
        Counting.Hud = true;
        _ = other.Patch(AccessTools.DeclaredMethod(skipped, name),
            postfix: new HarmonyMethod(typeof(FaceLightGoldenTests), nameof(See)));
        FaceLight.Recheck();
        var counted = FaceLight.FastFaces + FaceLight.FusedBlocks;
        Forget();
        _ = json.SetUpLightRGBs(rig.Vars);
        _ = TessRig.CalcBlockFaceLight(rig.Vars, 4, e + TessRig.Plane);
        Assert.Multiple(() =>
        {
            Assert.That(FaceLight.StoodDown, Is.True);
            Assert.That(FaceLight.FastFaces + FaceLight.FusedBlocks, Is.EqualTo(counted));
            Assert.That(Seen, Is.EqualTo(calls), "the engine called it");
        });
    }

    // Smooth shadows off: the fused block has no face to light itself, so the engine's loop runs, with the same outputs
    [Test]
    public void JsonBlockWithoutAoIsTheEngines()
    {
        using var rig = PlainNeighbourhood(out _);
        rig.Vars.aoAndSmoothShadows = false;
        var mine = new int[25];
        Assert.That(FaceLight.Six(rig.Vars, mine, out _), Is.False);
        rig.Vars.aoAndSmoothShadows = true;
        rig.Vars.block.SideAo = new SmallBoolArray(64 | 128); // bits beyond the six faces do not count
        Assert.That(FaceLight.Six(rig.Vars, mine, out _), Is.False);
        rig.Vars.block.SideAo = new SmallBoolArray(32);
        Assert.That(FaceLight.Six(rig.Vars, mine, out _), Is.True, "one face with AO");
    }

    private static void Forget() => _ = Interlocked.Exchange(ref _seen, 0);

    private static void See() => _ = Interlocked.Increment(ref _seen);

    // The index offset of cell i of the 3x3x3 block around a cell, numbered (y + 1) * 9 + (z + 1) * 3 + x + 1
    private static int Around(int i) => (i / 9 - 1) * TessRig.Plane + (i / 3 % 3 - 1) * TessRig.Ext + (i % 3 - 1);

    private static TessRig PlainNeighbourhood(out int e)
    {
        var palette = new Block[3];
        for (var i = 0; i < palette.Length; i++)
            palette[i] = new Block
            {
                BlockId = i, EmitSideAo = (byte)(i == 0 ? 0 : 63), LightAbsorption = i == 0 ? 0 : 99,
                SideAo = new SmallBoolArray(63)
            };
        var rig = new TessRig(palette);
        rig.Solid.AsSpan().Fill(palette[0]);
        rig.Fluid.AsSpan().Fill(palette[0]);
        for (var i = 0; i < TessRig.ExtCells; i++) rig.Rgb[i] = TessMix.Hash(i, 3, 5);
        e = (17 * TessRig.Ext + 17) * TessRig.Ext + 17;
        for (var i = 0; i < 27; i += 2)
            rig.Solid[e + Around(i)] = palette[1];
        (rig.Vars.extIndex3d, rig.Vars.block, rig.Vars.aoAndSmoothShadows) = (e, palette[2], true);
        return rig;
    }

    // One face both ways from the same junk; true when the fast path answered
    private static bool Face(TessRig rig, int t, int front)
    {
        var vars = rig.Vars;
        Junk(vars);
        AoRecordingBlock.Log = [];
        var engine = TessRig.CalcBlockFaceLight(vars, t, front);
        AoRecordingBlock.Log = [];
        int[] corners = [Filler, Filler, Filler, Filler];
        var neighbours = Enumerable.Repeat(Filler, 9).ToArray();
        var answered = FaceLight.Face(vars, t, front, corners, neighbours, out var sum);
        var log = AoRecordingBlock.Log;
        AoRecordingBlock.Log = null;
        Assert.That(log, Is.Empty, "the fast path calls no block");
        Assert.That(answered, Is.EqualTo(Expected(rig, t, front)), $"answered face {t} front {front}");
        if (!answered)
        {
            Assert.That(corners.Concat(neighbours), Is.All.EqualTo(Filler), "written though handed back");
            return false;
        }

        var engineNeighbours = TessRig.Neighbours(vars);
        if (sum != engine || !corners.AsSpan().SequenceEqual(vars.CurrentLightRGBByCorner) ||
            !neighbours.AsSpan().SequenceEqual(engineNeighbours))
            Assert.Fail(
                $"face {t} front {front} self {vars.block.BlockId}: sum {sum} vs {engine}, corners [{string.Join(",", corners)}] vs " +
                $"[{string.Join(",", vars.CurrentLightRGBByCorner)}], neighbours [{string.Join(",", neighbours)}] vs [{string.Join(",", engineNeighbours)}]");

        return true;
    }

    // What the fast path must answer: AO on for the face, multipliers within [0, 16] (occ and its full-occlusion minimum), and the
    // front block the engine asks and all 8 solid samples plain
    private static bool Expected(TessRig rig, int t, int front)
    {
        var vars = rig.Vars;
        if (!vars.aoAndSmoothShadows || !vars.block.SideAo[t]) return false;
        var full = Math.Min(vars.occ, 1f - vars.halfoccInverted * GameMath.Clamp(vars.block.LightAbsorption, 0, 32));
        if (vars.occ is not (>= 0 and <= 16) || full is not (>= 0 and <= 16)) return false;
        var asked = rig.Fluid[front].LightAbsorption > 0 ? rig.Fluid[front] : rig.Solid[front];
        if (!Plain(asked)) return false;
        foreach (var vec in CubeFaceVertices.blockFaceVerticesCentered[t].Take(8))
            if (!Plain(rig.Solid[vars.extIndex3d + vec.extIndexOffset]))
                return false;
        return true;
    }

    private static bool Plain(Block block) =>
        block.GetType() == typeof(Block) || block is BlockForFluidsLayer or FalseFluidsBlock;

    private static long[] Outputs(TessRig rig, Func<long> call, JsonTesselator json)
    {
        Junk(rig.Vars);
        TessRig.JsonLight(json).AsSpan().Fill(Filler);
        var sum = call();
        return [sum, .. rig.Vars.CurrentLightRGBByCorner, .. TessRig.Neighbours(rig.Vars), .. TessRig.JsonLight(json)];
    }

    private static void Junk(TCTCache vars)
    {
        vars.CurrentLightRGBByCorner.AsSpan().Fill(Filler);
        TessRig.Neighbours(vars).AsSpan().Fill(Filler);
    }

    private static void Settings(TCTCache vars, Random r, int i)
    {
        vars.aoAndSmoothShadows = r.Next(10) != 0;
        (vars.occ, vars.halfoccInverted) = (i % 50) switch
        {
            7 => (1f, 0.0196875f),
            13 => (0.5f, 0.05f),
            17 => (0.3f + (float)r.NextDouble(), (float)r.NextDouble() / 16),
            23 => (1.7f, 0.0196875f),
            29 => (float.NaN, 0.0196875f),
            31 => (40f, 0.0196875f),
            37 => (0.67f, 0.5f),
            41 => (-0.25f, 0.0196875f),
            43 => (0.67f, float.PositiveInfinity),
            _ => (0.67f, 0.0196875f)
        };
    }

    private static int Neighbourhood(TessRig rig, Random r, Block[] palette)
    {
        var e = ((1 + r.Next(32)) * TessRig.Ext + 1 + r.Next(32)) * TessRig.Ext + 1 + r.Next(32);
        var uniform = r.Next(4) == 0 ? palette[1 + r.Next(palette.Length - 1)] : null;
        for (var i = 0; i < 27; i++)
        {
            var p = e + Around(i);
            rig.Solid[p] = uniform is not null && r.Next(4) != 0 ? uniform : palette[r.Next(palette.Length)];
            rig.Fluid[p] = r.Next(6) == 0 ? palette[r.Next(palette.Length)] : palette[0];
            rig.Rgb[p] = r.Next(3) == 0 ? SpecialLights[r.Next(SpecialLights.Length)] : r.Next() ^ (r.Next(2) << 31);
        }

        rig.Vars.extIndex3d = e;
        rig.Vars.block = palette[r.Next(palette.Length)];
        return e;
    }

    private static Block[] Palette(Random r)
    {
        const int count = 48;
        var palette = new Block[count];
        for (var i = 0; i < count; i++)
        {
            var block = (i % 12) switch
            {
                0 when i == 0 => new Block(),
                5 => new BlockForFluidsLayer(),
                6 => new BlockWater(),
                7 => new AoRecordingBlock(),
                8 => new FalseFluidsBlock(),
                9 => new ComputedFluidsBlock(),
                _ => new Block()
            };
            block.BlockId = i;
            block.EmitSideAo = (byte)(r.Next(3) == 0 ? 63 : r.Next(256));
            block.LightAbsorption = i == 0 ? 0 : Absorptions[r.Next(Absorptions.Length)];
            block.BlockMaterial = r.Next(3) == 0 ? EnumBlockMaterial.Leaves : EnumBlockMaterial.Stone;
            if (i == 0) block.BlockMaterial = EnumBlockMaterial.Air;
            block.SideAo = new SmallBoolArray(r.Next(3) == 0 ? r.Next(64) : 63);
            block.DrawType = r.Next(2) == 0 ? EnumDrawType.JSON : EnumDrawType.Cube;
            palette[i] = block;
        }

        return palette;
    }
}

internal sealed class AoRecordingBlock : Block
{
    public static List<(int Block, int Flags, int X)>? Log { get; set; }

    public override bool DoEmitSideAo(IGeometryTester caller, BlockFacing facing)
    {
        Log?.Add((BlockId, facing.Flag, -1));
        return TessMix.Bit(1, BlockId, facing.Flag);
    }

    public override bool DoEmitSideAoByFlag(IGeometryTester caller, Vec3iAndFacingFlags vec, int flags)
    {
        Log?.Add((BlockId, flags, vec.extIndexOffset));
        return TessMix.Bit(2, BlockId, flags, vec.extIndexOffset);
    }
}

internal sealed class FalseFluidsBlock : Block
{
    public override bool ForFluidsLayer => false;
}

internal sealed class ComputedFluidsBlock : Block
{
    public override bool ForFluidsLayer => LightAbsorption > 5;
}
