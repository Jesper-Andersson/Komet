using Komet.Rendering;

namespace Komet.Test.Rendering;

// The ranges handed to the GPU culling: the frame matrix, the draw ranges paired with their pools' locations in one walk, and each
// location's box as cull.comp and rows.comp read it
public sealed class OcclusionTests
{
    private static readonly (double X, double Y, double Z) Camera = (512000.5, 110.25, 511999.75);

    // ClientMain's CurrentProjectionMatrix and CurrentModelViewMatrix fill and return one shared array
    [Test]
    public void TheProjectionIsTakenBeforeTheSharedArrayIsRefilled()
    {
        var (shared, projection, view) = (new float[16], Mat4f.Perspective(new float[16], 1.2f, 1.5f, 0.1f, 1000),
            Mat4f.Translate(new float[16], Mat4f.Create(), 1, 2, 3));
        var render = Answers.Of<IRenderAPI>(new()
        {
            ["get_CurrentProjectionMatrix"] = _ => Fill(shared, projection),
            ["get_CurrentModelviewMatrix"] = _ => Fill(shared, view)
        });
        var into = new float[16];
        Assert.Multiple(() =>
        {
            Assert.That(Occlusion.Capture(render, into), Is.True);
            Assert.That(into, Is.EqualTo(Mat4f.Mul(new float[16], projection, view)).Within(1e-5f));
            Assert.That(into[11], Is.EqualTo(-1).Within(1e-5f), "w is the distance ahead, not 1");
        });
    }

    private static float[] Fill(float[] shared, float[] values)
    {
        values.CopyTo(shared, 0);
        return shared;
    }

    [Test]
    public void TheRangeIsSixteenBytesTwice()
    {
        Assert.That(Unsafe.SizeOf<Occlusion.Range>(), Is.EqualTo(32), "std430 Range in cull.comp");
    }

    private static ModelDataPoolLocation Location(int start, int end, float x) => new()
    {
        IndicesStart = start, IndicesEnd = end, FrustumCullSphere = new Sphere(x, 110, 512000, 32, 32, 32)
    };

    // A pool's draw list as FrustumCull leaves it: byte offsets of the starts in every other int, sizes in indices
    private static (int[] Starts, int[] Sizes) Drawn(params ModelDataPoolLocation[] drawn)
    {
        var (starts, sizes) = (new int[2 * drawn.Length + 2], new int[drawn.Length + 1]);
        for (var i = 0; i < drawn.Length; i++)
            (starts[2 * i], sizes[i]) = (drawn[i].IndicesStart * 4, drawn[i].IndicesEnd - drawn[i].IndicesStart);
        return (starts, sizes);
    }

    [Test]
    public void EveryDrawnRangeFindsItsLocation()
    {
        List<ModelDataPoolLocation> pool =
        [
            Location(0, 300, 511968), Location(300, 900, 512000), Location(900, 960, 512032),
            Location(960, 999, 512064)
        ];
        var (starts, sizes) = Drawn(pool[1], pool[3]);
        var into = new Occlusion.Range[8];
        var written = Occlusion.Match(starts, sizes, 2, pool, into, Camera, out var unmatched);
        Assert.Multiple(() =>
        {
            Assert.That((written, unmatched), Is.EqualTo((2, 0)));
            Assert.That((into[0].First, into[0].Indices), Is.EqualTo((300, 600)));
            Assert.That((into[1].First, into[1].Indices), Is.EqualTo((960, 39)));
            Assert.That(into[1].LowX, Is.EqualTo(512064 - 16 - Camera.X).Within(1e-3),
                "the box's west face, camera-relative");
        });
    }

    [Test]
    public void ARangeWithoutALocationIsCountedAndSkipped()
    {
        List<ModelDataPoolLocation> pool = [Location(0, 300, 511968), Location(900, 960, 512032)];
        var (starts, sizes) = Drawn(Location(300, 900, 0), pool[1]);
        var into = new Occlusion.Range[8];
        var written = Occlusion.Match(starts, sizes, 2, pool, into, Camera, out var unmatched);
        Assert.Multiple(() =>
        {
            Assert.That((written, unmatched), Is.EqualTo((1, 1)));
            Assert.That(into[0].First, Is.EqualTo(900));
        });
    }

    [Test]
    public void AFullBufferStopsTheWalk()
    {
        List<ModelDataPoolLocation> pool = [Location(0, 3, 0), Location(3, 6, 0), Location(6, 9, 0)];
        var (starts, sizes) = Drawn([.. pool]);
        var into = new Occlusion.Range[2];
        Assert.That(Occlusion.Match(starts, sizes, 3, pool, into, Camera, out _), Is.EqualTo(2));
    }

    [Test]
    public void TheBoxIsTheChunkTheSphereWasMadeFrom()
    {
        var sphere = new Sphere(512016, 128, 512016, 32, 32, 32); // a 32-block chunk at 512000..512032, 112..144
        var box = Occlusion.Box(sphere, 6, 0, (512000, 100, 512000));
        Assert.Multiple(() =>
        {
            Assert.That((box.LowX, box.LowY, box.LowZ), Is.EqualTo((0f, 12f, 0f)).Using(new Near()));
            Assert.That((box.HighX, box.HighY, box.HighZ), Is.EqualTo((32f, 44f, 32f)).Using(new Near()));
        });
    }

    private sealed class Near : IEqualityComparer<(float, float, float)>
    {
        public bool Equals((float, float, float) a, (float, float, float) b) =>
            Math.Abs(a.Item1 - b.Item1) < 1e-3 && Math.Abs(a.Item2 - b.Item2) < 1e-3 &&
            Math.Abs(a.Item3 - b.Item3) < 1e-3;

        public int GetHashCode((float, float, float) value) => 0;
    }
}
