using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Chunks;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ChunkCuller (own thread) casts three rays from the camera chunk to each shell chunk, marking chunks visible until an
// untraversable one stops it. Each step's getExitingFace tests six faces via Vec3d objects, BlockFacing properties and
// culler fields: in world-join traces 11 % of all process samples, a core taken from tessellation. Replaced by the same
// walk on locals: same double expressions and order, faces in ALLFACES order, only those the ray can leave (engine dot
// product, once per ray), same lookups, SetVisible/IsTraversable calls and stops; fields left as the engine leaves
// them.
internal static class CullerRays
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0x095C271DADB9F4DAUL;

    private const int Faces = 6, SeamCount = 2, MaxSteps = 1 << 16; // a ray crosses a few hundred chunks at most
    private const double Parallel = 1E-05;

    private static readonly int[] NormalX = new int[Faces], NormalY = new int[Faces], NormalZ = new int[Faces];
    private static readonly double[] CenterX = new double[Faces], CenterY = new double[Faces], CenterZ = new double[Faces];
    private static readonly BlockFacing?[] Facings = new BlockFacing?[Faces];
    private static readonly SeamGuard Guard = new(nameof(CullerRays), PatchKinds.All, [typeof(CullerRays)]);

    public static bool Enabled { get; set; } = true;

    public static bool Blocked => Guard.Blocked;

    public static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetVisible")]
    private static extern void SetVisible(ClientChunk chunk, bool visible);

    public static void Install(Harmony harmony, ILogger? logger = null, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || seams.Length != SeamCount || seams[0] is not MethodInfo walk || !Faced()) return;
        if (!Guard.Match(seams, fingerprint) || !Assert(Facings.Length == Faces)) return;
        _ = NotNull(harmony.Patch(walk, new HarmonyMethod(Walk)));
        Recheck();
    }

    // [0] is replaced, [1] is what it called per step
    internal static MethodBase?[] Seams()
    {
        var (culler, cell, d) = (typeof(ChunkCuller), typeof(Vec3i), typeof(double));
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(culler, "traverseRayAndMarkVisible", [cell, cell, d, d]),
            AccessTools.DeclaredMethod(culler, "getExitingFace", [cell])
        ];
        return Assert(seams.Length == SeamCount) ? seams : [];
    }

    // Read once: BlockFacing's are static and never written
    internal static bool Faced()
    {
        if (BlockFacing.ALLFACES is not { Length: Faces } all) return false;
        for (var i = 0; i < Faces; i++)
        {
            if (all[i] is not { Normali: { } normal, PlaneCenter: { } center } face) return false;
            (Facings[i], NormalX[i], NormalY[i], NormalZ[i]) = (face, normal.X, normal.Y, normal.Z);
            (CenterX[i], CenterY[i], CenterZ[i]) = (center.X, center.Y, center.Z);
        }

        return Assert(Facings[Faces - 1] is not null);
    }

    // traverseRayAndMarkVisible(fromPos, toPosRel, yoffset, xoffset)
    private static bool Walk(ChunkCuller __instance, Vec3i fromPos, Vec3i toPosRel, double yoffset, double xoffset,
        ClientMain ___game, bool ___isAboveHeightLimit, Vec3i ___toPos)
    {
        if (!Enabled || Blocked || !NotNull(__instance) || ___game?.WorldMap is not { } map || ChunkLookup.Chunks(map) is not { } chunks ||
            fromPos is null || toPosRel is null || ___toPos is null || __instance.ray is not { origin: { } o, dir: { } dir } ||
            __instance.curpos is not { } cur) return true;
        _ = o.Set(fromPos.X + xoffset, fromPos.Y + yoffset, fromPos.Z + 0.5);
        _ = dir.Set(toPosRel.X + xoffset, toPosRel.Y + yoffset, toPosRel.Z + 0.5);
        _ = ___toPos.Set(fromPos.X + toPosRel.X, fromPos.Y + toPosRel.Y, fromPos.Z + toPosRel.Z);
        var (ox, oy, oz, dx, dy, dz) = (o.X, o.Y, o.Z, dir.X, dir.Y, dir.Z);

        Span<int> exits = stackalloc int[Faces];
        Span<double> dots = stackalloc double[Faces];
        var n = Exits(dx, dy, dz, exits, dots);

        var (fx, fy, fz) = (fromPos.X, fromPos.Y, fromPos.Z);
        var (cx, cy, cz) = (fx, fy, fz);
        var reach = Math.Abs(fx - ___toPos.X) + Math.Abs(fy - ___toPos.Y) + Math.Abs(fz - ___toPos.Z);
        _ = Assert(reach >= 0);
        var (mulX, mulZ) = (map.index3dMulX, map.index3dMulZ);
        BlockFacing? entry = null;
        for (var step = 0; step < MaxSteps; step++)
        {
            var walked = Math.Abs(cx - fx) + Math.Abs(cy - fy) + Math.Abs(cz - fz);
            if (walked > reach + 2) break;
            var face = Exit(exits[..n], dots[..n], cx, cy, cz, ox, oy, oz, dx, dy, dz);
            if (face < 0) break;
            var exit = Facings[face]!;
            var key = ((long)cy * mulZ + cz) * mulX + cx;
            _ = chunks.TryGetValue(key, out var chunk); // unlocked, as the engine reads it
            if (chunk != null)
            {
                SetVisible(chunk, true);
                if (walked > 1 && !chunk.IsTraversable(entry, exit)) break;
            }

            (cx, cy, cz) = (cx + NormalX[face], cy + NormalY[face], cz + NormalZ[face]);
            entry = exit.Opposite;
            if (!map.IsValidChunkPosFast(cx, cy, cz) && (!___isAboveHeightLimit || cy <= 0)) break;
        }

        _ = cur.Set(cx, cy, cz);
        return false;
    }

    // Exitable faces for this direction in ALLFACES order, engine dot product; returns how many
    internal static int Exits(double dx, double dy, double dz, Span<int> exits, Span<double> dots)
    {
        var n = 0;
        _ = Assert(exits.Length >= 3) && Assert(dots.Length >= exits.Length);
        for (var i = 0; i < Faces; i++)
        {
            var dot = NormalX[i] * dx + NormalY[i] * dy + NormalZ[i] * dz;
            if (dot <= Parallel || !Index(n, exits.Length)) continue;
            (exits[n], dots[n]) = (i, dot);
            n++;
        }

        _ = Assert(n <= 3); // a ray leaves through at most one face per axis
        return n;
    }

    // getExitingFace: first face in ALLFACES order whose plane the ray meets ahead within the face, or -1
    internal static int Exit(ReadOnlySpan<int> exits, ReadOnlySpan<double> dots, int cx, int cy, int cz, double ox, double oy,
        double oz, double dx, double dy, double dz)
    {
        for (var k = 0; k < Math.Min(exits.Length, Faces); k++)
        {
            var i = exits[k];
            _ = Index(i, Faces) && Assert(dots[k] > Parallel);
            var (px, py, pz) = (cx + CenterX[i], cy + CenterY[i], cz + CenterZ[i]);
            var (ax, ay, az) = (px - ox, py - oy, pz - oz);
            var t = (ax * NormalX[i] + ay * NormalY[i] + az * NormalZ[i]) / dots[k];
            if (t >= 0.0 && Math.Abs(ox + dx * t - px) <= 0.5 && Math.Abs(oy + dy * t - py) <= 0.5 &&
                Math.Abs(oz + dz * t - pz) <= 0.5)
                return i;
        }

        return -1;
    }
}
