using Komet.Diagnostics;
using static Komet.Test.Diagnostics.DiagnosticsTests;

namespace Komet.Test.Diagnostics;

public sealed class RenderCostTests
{
    private static readonly string[] Stages =
        ["OnRenderBefore", "RenderShadow", "OnBeforeRenderOpaque", "RenderOpaque", "RenderOIT", "RenderAfterOIT"];

    [TearDown]
    public void SwitchOff()
    {
        RenderCost.Clear();
    }

    private static MethodInfo RenderMesh() =>
        AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderMesh),
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);

    [Test]
    public void InstallPatchesNothing()
    {
        using var harmony = new TestHarmony("komet-test-rendercost-off");
        RenderCost.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(RenderCost.Patched, Is.False);
            Assert.That(PatchedBy(RenderMesh(), harmony), Is.False);
            foreach (var stage in Stages)
                Assert.That(PatchedBy(AccessTools.DeclaredMethod(typeof(ChunkRenderer), stage), harmony), Is.False,
                    stage);
        });
    }

    [Test]
    public void TheFirstSwitchOnPatchesEveryTerrainCall()
    {
        using var harmony = new TestHarmony("komet-test-rendercost-on");
        RenderCost.Install(harmony, new QuietLogger());
        RenderCost.Enabled = true;
        Assert.That(RenderCost.Patched, Is.True);
        Assert.Multiple(() =>
        {
            foreach (var stage in Stages)
            {
                var method = AccessTools.DeclaredMethod(typeof(ChunkRenderer), stage, [typeof(float)]);
                Assert.That(method, Is.Not.Null, $"ChunkRenderer.{stage} is gone");
                Assert.That(PatchedBy(method!, harmony), Is.True, stage);
            }

            Assert.That(PatchedBy(RenderMesh(), harmony), Is.True, "RenderMesh");
        });
    }

    [TestCase("Vintagestor:gl0", 1)]
    [TestCase("glxgears:gdrv0", 2)]
    [TestCase("Vintagesto:cs0\n", 3)]
    [TestCase("glxgea:sh_opt0", -1)]
    [TestCase("glxgears:sh1", -1)]
    [TestCase("Vintagestory", -1)]
    [TestCase("gl0", -1)]
    [TestCase("foo:gl", -1)]
    public void DriverThreadsAreKnownByName(string comm, int kind)
    {
        Assert.That(RenderCost.Classify(comm), Is.EqualTo(kind));
    }
}
