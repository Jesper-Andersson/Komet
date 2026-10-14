using System.Runtime.InteropServices;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// The terrain ranges OcclusionCulling and FrustumSweep hand the GPU: boxes relative to the camera, and the frame matrix
internal static class Occlusion
{
    private const int MaxLocations = 65536;
    private const float Sqrt3 = FrustumSweep.Sqrt3;

    private static readonly float[] Projection = new float[16];

    // ClientMain refills one array for both matrices per read: copy projection before reading model view
    internal static bool Capture(IRenderAPI render, float[] into)
    {
        if (!NotNull(render) || !Assert(into.Length == 16) ||
            render.CurrentProjectionMatrix is not { Length: 16 } projection) return false;
        projection.CopyTo(Projection, 0);
        if (render.CurrentModelviewMatrix is not { Length: 16 } view) return false;
        _ = Mat4f.Mul(into, Projection, view);
        return Finite(into[15]) && Finite(into[0]);
    }

    // FrustumCull emits visible locations in index order, so one walk pairs ranges with locations
    internal static int Match(int[] starts, int[] sizes, int groups, List<ModelDataPoolLocation> locations,
        Span<Range> into, (double X, double Y, double Z) camera, out int unmatched)
    {
        unmatched = 0;
        if (!NotNull(starts) || !NotNull(sizes) || !NotNull(locations) || !Assert(groups >= 0)) return 0;
        var (written, next) = (0, 0);
        var count = Math.Min(Math.Min(groups, sizes.Length), starts.Length / 2);
        for (var i = 0; i < Math.Min(count, MaxLocations) && written < into.Length; i++)
        {
            var start = starts[2 * i] / 4;
            next = Seek(locations, next, start);
            if (next >= locations.Count || locations[next].IndicesStart != start)
            {
                unmatched++;
                continue;
            }

            into[written++] = Box(locations[next].FrustumCullSphere, sizes[i], start, camera);
            next++;
        }

        return written;
    }

    private static int Seek(List<ModelDataPoolLocation> locations, int from, int start)
    {
        if (!Assert(from >= 0) || !Assert(start >= 0)) return locations.Count;
        for (var j = from; j < Math.Min(locations.Count, MaxLocations); j++)
            if (locations[j].IndicesStart >= start)
                return j;
        return locations.Count;
    }

    // In doubles until the difference
    internal static Range Box(Sphere sphere, int count, int first, (double X, double Y, double Z) camera)
    {
        _ = Assert(sphere.radius >= 0) && Assert(sphere.radiusY >= 0);
        return Box((sphere.x, sphere.y, sphere.z), (sphere.radius / Sqrt3, sphere.radiusY / Sqrt3, sphere.radiusZ / Sqrt3),
            count, first, camera);
    }

    internal static Range Box((float X, float Y, float Z) centre, (float X, float Y, float Z) half, int count, int first,
        (double X, double Y, double Z) camera)
    {
        var (cx, cy, cz) = ((float)(centre.X - camera.X), (float)(centre.Y - camera.Y), (float)(centre.Z - camera.Z));
        _ = Assert(count >= 0) && Assert(half.X >= 0 && half.Y >= 0 && half.Z >= 0);
        return new Range(cx - half.X, cy - half.Y, cz - half.Z, count, cx + half.X, cy + half.Y, cz + half.Z, first);
    }

    // std430 as in cull.comp and rows.comp: vec3 + uint fill 16 bytes
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Range(
        float LowX, float LowY, float LowZ, int Indices, float HighX, float HighY, float HighZ, int First);
}
