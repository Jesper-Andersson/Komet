using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Test.Rendering;

// PoolUpdates in place of UpdateMesh: installed on the body it was written against, and every upload of an update into the
// pool handed to Through with its bytes at their byte offset - the attributes in the engine's order, then the indices; without
// Through the engine's own body runs
[NonParallelizable]
public sealed class PoolUpdatesTests
{
    private static readonly float[] Xyz = [1f, 2f, 3f, 4f, 5f, 6f];
    private static readonly byte[] Rgba = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly int[] Indices = [0, 1, 1];
    private static readonly (int, long)[] Uploads = [(11, 24L), (12, 8L), (13, 12L)]; // xyz, rgba, indices: buffer and offset

    [TearDown]
    public void Restore() => FacePacking.Through = null;

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-poolupdates");
        PoolUpdates.Install(harmony, new QuietLogger());
        Assert.That(Harmony.GetPatchInfo(PoolUpdates.Seams()[0])?.Prefixes.Select(p => p.owner),
            Is.EqualTo([harmony.Id]), "UpdateMesh is not the body verified");
    }

    [Test]
    public void AnUpdateIntoThePoolGoesThroughWithItsBytes()
    {
        using var window = Gpu.GlContext.Context(); // a taken upload makes the engine's GL binds
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        using var harmony = new TestHarmony("komet-test-poolupdates");
        PoolUpdates.Install(harmony, new QuietLogger());
        using var pool = new VAO { xyzVboId = 11, rgbaVboId = 12, vboIdIndex = 13 }; // disposed: its finalizer wants a platform
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var data = new MeshData(2, 3, false, false, false, false)
        {
            xyz = Xyz, Rgba = Rgba, Indices = Indices, VerticesCount = 2, IndicesCount = 3, XyzOffset = 24, RgbaOffset = 8,
            IndicesOffset = 12
        };
        var seen = new List<(int Buffer, long Offset, byte[] Data)>();
        Assert.That(PoolUpdates.Updating(platform, pool, data), Is.True, "without Through: the engine's");
        FacePacking.Through = (vao, buffer, offset, bytes) =>
        {
            seen.Add((buffer, offset, bytes.ToArray()));
            return ReferenceEquals(vao, pool);
        };
        Assert.Multiple(() =>
        {
            Assert.That(PoolUpdates.Updating(platform, pool, data), Is.False, "made here");
            Assert.That(seen.Select(s => (s.Buffer, s.Offset)), Is.EqualTo(Uploads));
            Assert.That(seen[0].Data, Is.EqualTo(Xyz.SelectMany(BitConverter.GetBytes).ToArray()));
            Assert.That(seen[1].Data, Is.EqualTo(Rgba));
            Assert.That(seen[2].Data, Is.EqualTo(Indices.SelectMany(BitConverter.GetBytes).ToArray()));
            Assert.That(pool.IndicesCount, Is.EqualTo(3));
        });
    }
}
