using Komet.Rendering;

namespace Komet.Test.Rendering;

// FaceSorting on the engine's own cube (CubeMeshUtil.GetCube, the 2x2x2 cube the tessellator's faces are cut from): every face
// lands in its direction, front sides outwards - the +X face's plane is x = 1 and it faces +X - each direction's nearest plane
// is its face's, and every array of the mesh moves with its face. Faces the shader moves or pushes in depth go last, in order.
// A mesh not laid out as quads with the quad index pattern stays as it was.
public sealed class FaceSortingTests
{
    private const int WindMode = 2 << 25, ZOffset = 1 << 8;
    private static readonly int[] OnePerDirection = [1, 1, 1, 1, 1, 1];
    private static readonly float[] CubePlanes = [-1f, 1f, -1f, 1f, -1f, 1f];

    // The cube with a tag per face in every per-vertex and per-face array: face f's values are f (uv, flags' glow bits, rgba,
    // custom ints) so that where a face went its data must have gone too
    private static MeshData Cube(int faces = 6)
    {
        var mesh = CubeMeshUtil.GetCube();
        mesh.Indices = (int[])mesh.Indices.Clone(); // GetCube hands out the engine's static CubeVertexIndices
        mesh.Flags = new int[4 * faces];
        mesh.CustomInts = new CustomMeshDataPartInt(4 * faces) { Count = 4 * faces };
        mesh.ClimateColorMapIds = new byte[faces];
        mesh.SeasonColorMapIds = new byte[faces];
        mesh.ColorMapIdsCount = faces;
        for (var f = 0; f < faces; f++)
        {
            (mesh.ClimateColorMapIds[f], mesh.SeasonColorMapIds[f]) = ((byte)f, (byte)(10 + f));
            for (var k = 0; k < 4; k++)
            {
                var v = 4 * f + k;
                (mesh.Flags[v], mesh.CustomInts.Values[v], mesh.Rgba[4 * v]) = (f, 100 + f, (byte)(20 + f));
                mesh.Uv[2 * v] = f;
            }
        }

        return mesh;
    }

    [Test]
    public void TheCubesFacesGoToTheirDirectionsFrontOutwards()
    {
        var mesh = Cube();
        var original = (float[])mesh.xyz.Clone();
        var sorted = FaceSorting.Sorting(mesh);
        Assert.That(sorted, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sorted!.Counts, Is.EqualTo(OnePerDirection), "one face per direction");
            Assert.That(Enumerable.Range(0, 6).Select(d => sorted.Low[4 * d + 3]), Is.EqualTo(CubePlanes),
                "one face in each direction's last slab: -X at x = -1, +X at x = 1, ...");
            for (var d = 0; d < 6; d++)
            {
                var (axis, plane) = (d / 2, d % 2 == 0 ? -1f : 1f);
                for (var k = 0; k < 4; k++) Assert.That(mesh.xyz[3 * (4 * d + k) + axis], Is.EqualTo(plane), $"face {d}");
            }

            Assert.That(mesh.Indices[..36], Is.EqualTo(CubeMeshUtil.CubeVertexIndices), "the quad pattern stays");
            Assert.That(Moved(mesh, original), Is.True, "every array moved with its face");
        });
    }

    [Test]
    public void MovedAndPushedFacesGoLastInTheirOrder()
    {
        var mesh = Cube();
        for (var k = 0; k < 4; k++) (mesh.Flags[k], mesh.Flags[12 + k]) = (mesh.Flags[k] | WindMode, mesh.Flags[12 + k] | ZOffset);
        var original = (float[])mesh.xyz.Clone();
        var sorted = FaceSorting.Sorting(mesh);
        Assert.That(sorted, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sorted!.Counts.Sum(), Is.EqualTo(4), "four faces with a direction");
            Assert.That(mesh.CustomInts.Values[16], Is.EqualTo(100), "the wind's face after them");
            Assert.That(mesh.CustomInts.Values[20], Is.EqualTo(103), "then the pushed one");
            Assert.That(Moved(mesh, original), Is.True);
        });
    }

    [Test]
    public void AMeshNotLaidOutAsQuadsStaysAsItWas()
    {
        var mesh = Cube();
        (mesh.Indices[1], mesh.Indices[2]) = (mesh.Indices[2], mesh.Indices[1]);
        var (xyz, ints) = ((float[])mesh.xyz.Clone(), (int[])mesh.CustomInts.Values.Clone());
        Assert.Multiple(() =>
        {
            Assert.That(FaceSorting.Sorting(mesh), Is.Null);
            Assert.That(mesh.xyz, Is.EqualTo(xyz));
            Assert.That(mesh.CustomInts.Values, Is.EqualTo(ints));
        });
    }

    // Within a direction the faces go by their plane, least first (equal planes in their order), in four slabs of about equal
    // count with their least and greatest plane: +Y at 1 1 3 5 9 as 1 | 1 | 3 | 5 9, -Y at 2 8 as - | 2 | - | 8; the planes move
    // with the upload
    [Test]
    [NonParallelizable]
    public void FacesGoByTheirPlaneInSlabs()
    {
        var mesh = Flats(FlatUp, FlatDown);
        var sorted = FaceSorting.Sorting(mesh);
        Assert.That(sorted, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sorted!.Counts, Is.EqualTo(FlatCounts));
            Assert.That(Enumerable.Range(0, 7).Select(f => mesh.xyz[12 * f + 1]), Is.EqualTo(FlatHeights));
            Assert.That(Enumerable.Range(0, 7).Select(f => (int)mesh.Uv[8 * f]), Is.EqualTo(FlatOrder),
                "every face's uvs with it, equal planes in their order");
            Assert.That(sorted.SlabCounts[8..16], Is.EqualTo(FlatSlabs), "-Y's two faces and +Y's five, about a quarter each");
            Assert.That(sorted.Low[12..16], Is.EqualTo(FlatLows), "+Y's slabs' least planes");
            Assert.That(sorted.High[12..16], Is.EqualTo(FlatHighs), "and greatest");
        });
        FaceSorting.Sort(mesh, culled: true);
        for (var v = 0; v < mesh.VerticesCount; v++) mesh.xyz[3 * v + 1] += 64;
        FaceSorting.Uploaded(78, mesh);
        try
        {
            var placed = FaceSorting.Of(78, 0);
            Assert.That(placed, Is.Not.Null);
            Assert.That((placed!.SlabCounts[15], placed.Low[11], placed.Low[15], placed.High[15]), Is.EqualTo((2, 72f, 69f, 73f)));
        }
        finally
        {
            FaceSorting.Gone(78, 0);
        }
    }

    // Unit quads at x, z in [0, 1]: +Y ones at the heights up, then -Y ones at down; face f's uvs are f
    private static MeshData Flats(int[] up, int[] down)
    {
        var faces = up.Length + down.Length;
        var mesh = new MeshData(false)
        {
            VerticesCount = 4 * faces, IndicesCount = 6 * faces, xyz = new float[12 * faces], Uv = new float[8 * faces],
            Flags = new int[4 * faces], Indices = [.. Enumerable.Range(0, 6 * faces).Select(i => i / 6 * 4 + Quad[i % 6])],
            VerticesPerFace = 4, IndicesPerFace = 6
        };
        ReadOnlySpan<(int X, int Z)> corners = [(0, 0), (0, 1), (1, 1), (1, 0)];
        for (var f = 0; f < faces; f++)
            for (var k = 0; k < 4; k++)
            {
                var (v, rising) = (4 * f + k, f < up.Length);
                var (x, z) = corners[rising ? k : 3 - k];
                (mesh.xyz[3 * v], mesh.xyz[3 * v + 1], mesh.xyz[3 * v + 2]) = (x, rising ? up[f] : down[f - up.Length], z);
                mesh.Uv[2 * v] = f;
            }

        return mesh;
    }

    private static readonly int[] Quad = [0, 1, 2, 0, 2, 3];
    private static readonly int[] FlatUp = [5, 1, 3, 1, 9], FlatDown = [2, 8], FlatCounts = [0, 0, 2, 5, 0, 0];
    private static readonly int[] FlatOrder = [5, 6, 1, 3, 2, 0, 4], FlatSlabs = [0, 1, 0, 1, 1, 1, 1, 2];
    private static readonly float[] FlatLows = [1, 1, 3, 5], FlatHighs = [1, 1, 3, 9];
    private static readonly float[] FlatHeights = [2, 8, 1, 1, 3, 5, 9];

    // Every location uploaded sorted is let go by RemoveLocation, with or without the GPU culling that reads the groups: rounds of
    // filling a pool and emptying it end where they began
    [Test]
    [NonParallelizable]
    public void RemovedLocationsLetGoOfTheirGroups()
    {
        const int Parts = 300, Rounds = 3;
        using var harmony = new TestHarmony("komet-test-facesorting");
        FaceSorting.Install(harmony);
        var pool = Pool();
        var vao = Stand;
        vao.VaoId = 77;
        var (mesh, baseline) = (Cube(), FaceSorting.Located);
        FaceSorting.Sort(mesh, culled: true);
        for (var round = 0; round < Rounds; round++)
        {
            PoolModel.SetValue(pool, new NoMesh());
            var located = new List<ModelDataPoolLocation>();
            for (var p = 0; p < Parts; p++)
                located.Add(pool.TryAdd(Answers.NullClient, new MeshData(false)
                {
                    VerticesCount = 24, IndicesCount = 36, xyz = new float[72], Indices = new int[36]
                }, null, 0, default)!);
            PoolModel.SetValue(pool, vao);
            foreach (var location in located)
            {
                FaceSorting.Sort(mesh, culled: true); // one tessellation a location: the upload takes the mesh's groups
                mesh.XyzOffset = location.VerticesStart * 12;
                FaceSorting.Uploaded(vao.VaoId, mesh);
            }

            Assert.That(FaceSorting.Located, Is.EqualTo(baseline + Parts), "each location's groups");
            foreach (var location in located) pool.RemoveLocation(location);
            Assert.That(FaceSorting.Located, Is.EqualTo(baseline), $"round {round}: every one gone with its location");
        }

        FaceSorting.Clear();
        Assert.That(FaceSorting.Located, Is.Zero, "and all with the world");
    }

    // Held for the whole run: the engine's VAO finalizer frees GL state this stand-in never had, and on the finalizer thread that
    // ends the test process
    private static readonly VAO Stand = (VAO)RuntimeHelpers.GetUninitializedObject(typeof(VAO));

    private static readonly FieldInfo PoolModel = AccessTools.Field(typeof(MeshDataPool), "modelRef");

    private static MeshDataPool Pool()
    {
        const int Max = 400, Vertices = 150_000;
        var pool = (MeshDataPool)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPool));
        AccessTools.Field(typeof(MeshDataPool), "poolLocations").SetValue(pool, new List<ModelDataPoolLocation>());
        (pool.MaxPartsPerPool, pool.VerticesPoolSize, pool.IndicesPoolSize) = (Max, Vertices, Vertices * 3 / 2);
        (pool.indicesStartsByte, pool.indicesSizes) = (new int[2 * Max], new int[Max]);
        return pool;
    }

    private static bool Moved(MeshData mesh, float[] original)
    {
        for (var f = 0; f < 6; f++)
        {
            var was = mesh.CustomInts.Values[4 * f] - 100;
            if (mesh.ClimateColorMapIds[f] != was || mesh.SeasonColorMapIds[f] != 10 + was) return false;
            for (var k = 0; k < 4; k++)
            {
                var v = 4 * f + k;
                if ((mesh.Flags[v] & 0xFF) != was || mesh.Rgba[4 * v] != 20 + was || (int)mesh.Uv[2 * v] != was ||
                    mesh.CustomInts.Values[v] != 100 + was) return false;
                for (var c = 0; c < 3; c++)
                    if (BitConverter.SingleToInt32Bits(mesh.xyz[3 * v + c]) !=
                        BitConverter.SingleToInt32Bits(original[3 * (4 * was + k) + c]))
                        return false;
            }
        }

        return true;
    }
}
