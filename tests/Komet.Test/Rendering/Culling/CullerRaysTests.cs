using Komet.Rendering;


namespace Komet.Test.Rendering;

// The walk's face choice is the engine's getExitingFace for random rays through the cells they cross (corners and edges included)
public sealed class CullerRaysTests
{
    [Test]
    public void TheExitFaceIsTheEngines()
    {
        Assert.That(CullerRays.Faced(), Is.True);
        var culler = (ChunkCuller)RuntimeHelpers.GetUninitializedObject(typeof(ChunkCuller));
        culler.ray = new Ray();
        AccessTools.FieldRefAccess<ChunkCuller, Vec3d>("planePosition")(culler) = new Vec3d();
        var engine = AccessTools.Method(typeof(ChunkCuller), "getExitingFace");
        var random = new Random(7);
        Span<int> exits = stackalloc int[6];
        Span<double> dots = stackalloc double[6];
        var compared = 0;
        for (var r = 0; r < 2000; r++)
        {
            // the engine's rays: an origin in the centre cell at quarter offsets, a direction to a shell cell
            var (ox, oy, oz) = (random.Next(-3, 4) + 0.25 * random.Next(0, 4), random.Next(-3, 4) + 0.75, random.Next(-3, 4) + 0.5);
            var (dx, dy, dz) = (random.Next(-12, 13) + 0.5 * random.Next(0, 2), random.Next(-8, 9) + 0.25, random.Next(-12, 13) + 0.5);
            _ = culler.ray.origin.Set(ox, oy, oz);
            _ = culler.ray.dir.Set(dx, dy, dz);
            var n = CullerRays.Exits(dx, dy, dz, exits, dots);
            for (var c = 0; c < 6; c++)
            {
                var cell = new Vec3i((int)Math.Floor(ox) + random.Next(-1, 2) * c, (int)Math.Floor(oy) + random.Next(-1, 2) * c,
                    (int)Math.Floor(oz) + random.Next(-1, 2) * c);
                var face = (BlockFacing?)engine.Invoke(culler, [cell]);
                var mine = CullerRays.Exit(exits[..n], dots[..n], cell.X, cell.Y, cell.Z, ox, oy, oz, dx, dy, dz);
                Assert.That(mine, Is.EqualTo(face?.Index ?? -1), $"ray {ox},{oy},{oz} -> {dx},{dy},{dz} cell {cell}");
                compared++;
            }
        }

        Assert.That(compared, Is.EqualTo(12000));
    }
}
