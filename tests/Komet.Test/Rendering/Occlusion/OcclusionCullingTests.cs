using Komet.Rendering;

namespace Komet.Test.Rendering;

// The culling's CPU side: what it patches, the draws IndirectDraw hands it, and the ranges a pool without its boxes goes up as
public sealed class OcclusionCullingTests
{
    [TearDown]
    public void SwitchOff()
    {
        OcclusionCulling.Clear();
        OcclusionCulling.Enabled = false;
    }

    private static MethodBase[] Seams() =>
    [
        AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)]),
        AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render))
    ];

    [Test]
    public void InstallPatchesTheOpaqueTerrainAndTakesTheDraws()
    {
        using var harmony = new TestHarmony("komet-test-culling");
        OcclusionCulling.Install(harmony, Answers.NullClient, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(OcclusionCulling.Installed, Is.True);
            foreach (var seam in Seams())
                Assert.That(Harmony.GetPatchInfo(seam)?.Owners.Contains(harmony.Id), Is.True, seam.Name);
            Assert.That(IndirectDraw.Intercept, Is.Not.Null);
        });
    }

    [Test]
    public void OutsideTheOpaqueTerrainNoDrawIsHeld()
    {
        using var harmony = new TestHarmony("komet-test-culling-hold");
        OcclusionCulling.Install(harmony, Answers.NullClient, new QuietLogger());
        OcclusionCulling.Enabled = true;
        // a VAO made here would crash the test host in its finalizer (it frees GL objects through the platform), so no mesh at all:
        // the question is only whether anything is held before RenderOpaque began
        Assert.That(IndirectDraw.Intercept?.Invoke(null!, false), Is.False);
    }

    [Test]
    public void ClearGivesTheDrawsBack()
    {
        using var harmony = new TestHarmony("komet-test-culling-clear");
        OcclusionCulling.Install(harmony, Answers.NullClient, new QuietLogger());
        OcclusionCulling.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(IndirectDraw.Intercept, Is.Null);
            Assert.That(OcclusionCulling.Installed, Is.False);
        });
    }

    [Test]
    public void AnUnboxedListIsKeptWhole()
    {
        var into = new Occlusion.Range[4];
        var written = OcclusionCulling.Unboxed([0, 0, 1200, 0, 0, 0], [300, 600], 2, into);
        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(2));
            Assert.That((into[1].First, into[1].Indices), Is.EqualTo((300, 600)));
            Assert.That(into[0].LowZ, Is.LessThan(-1e6f), "reaches behind the camera, which cull.comp always keeps");
        });
    }
}
