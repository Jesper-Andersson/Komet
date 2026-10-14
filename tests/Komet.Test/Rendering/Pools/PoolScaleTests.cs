using Komet.Rendering;

namespace Komet.Test.Rendering;

// A chunk pass's pools grow: the first as configured, the second twice, every later one Scale times; any other manager, and every
// manager at Scale 1, keeps the configured size
public sealed class PoolScaleTests
{
    private const int Vertices = 1000, Indices = 1500, Parts = 16, Quads = 100;

    [TearDown]
    public void Restore() => PoolScale.Scale = PoolScale.DefaultScale;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-poolscale");
        PoolScale.Install(harmony);
        Assert.That(PoolScale.Rewritten, Is.True, "AddModel no longer reads the pool sizes");
    }

    [TestCase(4, true, new[] { 1, 2, 4, 4 })]
    [TestCase(3, true, new[] { 1, 2, 3, 3 })]
    [TestCase(1, true, new[] { 1, 1, 1, 1 })]
    [TestCase(4, false, new[] { 1, 1, 1, 1 })]
    public void PoolsGrowOnlyInAChunkPass(int scale, bool chunks, int[] factors)
    {
        ArgumentNullException.ThrowIfNull(factors);
        using var harmony = new TestHarmony("komet-test-poolscale");
        PoolScale.Install(harmony);
        PoolScale.Scale = scale;
        var master = new MeshDataPoolMasterManager(Answers.NullClient) { DelayedPoolLocationRemoval = true };
        var manager =
            new MeshDataPoolManager(master, new FrustumCulling(), Answers.NullClient, Vertices, Indices, Parts);
        if (chunks) PoolScale.Register(manager);
        var mesh = new MeshData(false)
        {
            xyz = new float[3 * 4 * Quads], Indices = new int[6 * Quads],
            VerticesCount = 4 * Quads, IndicesCount = 6 * Quads
        };
        var pools = AccessTools.FieldRefAccess<MeshDataPoolManager, List<MeshDataPool>>("pools")(manager);
        for (var i = 0; i < 200 && pools.Count < factors.Length; i++)
            _ = manager.AddModel(mesh, new Vec3i(0, 0, 0), 0, new Sphere(16, 16, 16, 20, 20, 20));
        Assert.Multiple(() =>
        {
            Assert.That(pools.Select(p => p.VerticesPoolSize), Is.EqualTo(factors.Select(f => f * Vertices)));
            Assert.That(pools.Select(p => p.IndicesPoolSize), Is.EqualTo(factors.Select(f => f * Indices)));
        });
    }
}
