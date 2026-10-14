using Komet.Rendering;

namespace Komet.Test.Rendering;

// The kept far map: the matrix that follows the camera samples a world point where it was drawn, and only a mesh inside the map's
// square (any depth) makes it stale. Leaving the pass out needs the game: checked in the bench.
public sealed class FarShadowReuseTests
{
    // An orthographic light from above and the side, into [0, 1] like the engine's toShadowMapSpaceMatrixFar
    private static float[] Map()
    {
        double[] ortho = Mat4d.Create(), view = Mat4d.Create(), both = Mat4d.Create(), offset = Mat4d.Create(),
            all = Mat4d.Create();
        _ = Mat4d.Ortho(ortho, -200, 200, -150, 150, -300, 300);
        _ = Mat4d.LookAt(view, [0.3, 0.9, 0.2], [0, 0, 0], [0, 1, 0]);
        _ = Mat4d.Translate(offset, offset, 0.5, 0.5, 0.5);
        _ = Mat4d.Scale(offset, offset, [0.5, 0.5, 0.5]);
        _ = Mat4d.Mul(both, ortho, view);
        _ = Mat4d.Mul(all, offset, both);
        return [.. all.Select(v => (float)v)];
    }

    private static double[] At(float[] m, double x, double y, double z) =>
        [.. Enumerable.Range(0, 3).Select(i => m[i] * x + m[4 + i] * y + m[8 + i] * z + m[12 + i])];

    [Test]
    public void TheKeptMapFollowsTheCameraAndGoesStaleOnlyInItsSquare()
    {
        var kept = Map();
        var moved = new float[16];
        var (c0, c1, world) = ((1000.25, 110.5, -2000.75), (1003.0, 110.0, -1998.5), (1040.0, 95.0, -1950.0));
        _ = Mat4f.Translate(moved, kept, (float)(c1.Item1 - c0.Item1), (float)(c1.Item2 - c0.Item2), (float)(c1.Item3 - c0.Item3));
        var drawn = At(kept, world.Item1 - c0.Item1, world.Item2 - c0.Item2, world.Item3 - c0.Item3);
        var sampled = At(moved, world.Item1 - c1.Item1, world.Item2 - c1.Item2, world.Item3 - c1.Item3);
        FarShadowReuse.Use(kept, c0.Item1, c0.Item2, c0.Item3);
        FarShadowReuse.Touched(new Sphere(1000 + 600, 100, -2000, 16, 16, 16)); // far outside the square
        var outside = FarShadowReuse.Changed;
        FarShadowReuse.Touched(new Sphere(1000 + 50, 400, -2000 + 30, 16, 16, 16)); // high above: depth does not count
        var inside = FarShadowReuse.Changed;
        FarShadowReuse.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(sampled, Is.EqualTo(drawn).Within(1e-5), "the camera's move is compensated");
            Assert.That(outside, Is.False, "a chunk beyond the map changes nothing");
            Assert.That(inside, Is.True, "a chunk in the map's square makes it stale");
        });
    }
}
