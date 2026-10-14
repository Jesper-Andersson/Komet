using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// PotInFirepitRenderer skips only the draws of a pot whose block cell, widened by the lid and the shader's warps, lies wholly
// outside the Opaque stage's clip volume, and a skipped draw leaves face culling off and blending on in the standard mode, as the
// engine's drawn one does. Everything it cannot judge is left to the engine.
[NonParallelizable]
public sealed class PotCullingTests
{
    private static readonly string[] Left = ["cull off", "blend True Standard"];
    private readonly List<string> _state = [];

    [TearDown]
    public void Restore()
    {
        PotCulling.Enabled = true;
        Counting.Hud = false;
    }

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-potculling");
        PotCulling.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(PotCulling.Seams()[0], Is.Not.Null, "the survival mod's pot renderer");
            Assert.That(PotCulling.Matched, Is.True, "PotInFirepitRenderer.OnRenderFrame is not the body verified");
            Assert.That(PotCulling.Blocked, Is.False);
            Assert.That(Harmony.GetPatchInfo(PotCulling.Seams()[0])?.Prefixes.Select(p => p.owner),
                Is.EqualTo([harmony.Id]));
        });
    }

    // In front of the camera and beside it, just past the edge, it draws; behind it, and far to the side, it does not
    [Test]
    public void CullsOnlyWhatCannotReachTheView()
    {
        using var harmony = new TestHarmony("komet-test-potculling");
        PotCulling.Install(harmony, new QuietLogger());
        var client = Client();
        Assert.Multiple(() =>
        {
            Assert.That(PotCulling.Frame(new BlockPos(0, 0, -10), client, EnumRenderStage.Opaque), Is.True, "ahead");
            Assert.That(PotCulling.Frame(new BlockPos(0, 0, 40), client, EnumRenderStage.Opaque), Is.False, "behind");
            Assert.That(PotCulling.Frame(new BlockPos(0, 0, 0), client, EnumRenderStage.Opaque), Is.True, "at the camera");
            Assert.That(PotCulling.Frame(new BlockPos(1, 0, 1), client, EnumRenderStage.Opaque), Is.True,
                "its cell behind, the lid's reach in front of the near plane's corner");
            Assert.That(PotCulling.Frame(new BlockPos(200, 0, -10), client, EnumRenderStage.Opaque), Is.False, "far aside");
        });
    }

    [Test]
    public void EverythingItCannotJudgeIsLeftToTheEngine()
    {
        using var harmony = new TestHarmony("komet-test-potculling");
        PotCulling.Install(harmony, new QuietLogger());
        var (client, behind) = (Client(), new BlockPos(0, 0, 40));
        Assert.Multiple(() =>
        {
            Assert.That(PotCulling.Frame(behind, client, EnumRenderStage.OIT), Is.True, "another stage");
            Assert.That(PotCulling.Frame(null!, client, EnumRenderStage.Opaque), Is.True, "no position");
            Assert.That(PotCulling.Frame(behind, null!, EnumRenderStage.Opaque), Is.True, "no client");
            PotCulling.Enabled = false;
            Assert.That(PotCulling.Frame(behind, client, EnumRenderStage.Opaque), Is.True, "switched off");
        });
    }

    // The engine's body disables face culling and turns blending on in the standard mode before it draws, and leaves it so
    [Test]
    public void ASkippedDrawLeavesTheStateADrawnOneLeaves()
    {
        using var harmony = new TestHarmony("komet-test-potculling");
        PotCulling.Install(harmony, new QuietLogger());
        _state.Clear();
        Assert.That(PotCulling.Frame(new BlockPos(0, 0, 40), Client(), EnumRenderStage.Opaque), Is.False);
        Assert.That(_state, Is.EqualTo(Left));
    }

    private ICoreClientAPI Client()
    {
        var culler = AnimatableCullingTests.Culler();
        var render = Answers.Of<IRenderAPI>(new()
        {
            ["get_DefaultFrustumCuller"] = _ => culler, ["get_ShaderUniforms"] = _ => new DefaultShaderUniforms(),
            ["GlToggleBlend"] = a => Record($"blend {a![0]} {a[1]}"), ["GlDisableCullFace"] = _ => Record("cull off"),
            ["GlEnableCullFace"] = _ => Record("cull on")
        });
        return Answers.Of<ICoreClientAPI>(new() { ["get_Render"] = _ => render });
    }

    private object? Record(string call)
    {
        _state.Add(call);
        return null;
    }
}
