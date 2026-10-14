using Komet.Rendering;
using Komet.Shapes;
using Vintagestory.GameContent;

namespace Komet.Test.Shapes;

// A clutter block entity whose mesh is built during tesselation hands it to the pool, keeps only the empty placeholder and builds
// the same mesh again at its next tesselation, in the thread's scratch: the pool gets what the engine's Clone gives it
public sealed class ClutterMeshesTests
{
    [Test]
    public void TheMeshIsBuiltForEachTesselationAndNotKept()
    {
        var harmony = new Harmony("komet-test-cluttermeshes");
        try
        {
            MeshRecycle.Install(harmony);
            ClutterMeshes.Install(harmony);
            Assert.That((ClutterMeshes.Patched, MeshRecycle.Patched), Is.EqualTo((true, true)));
            // the bookshelf's override builds its copy without calling the base createMesh
            Assert.That(Harmony.GetPatchInfo(AccessTools.DeclaredMethod(typeof(BEBehaviorClutterBookshelf), "createMesh"))
                ?.Transpilers, Has.Count.EqualTo(1));
            var drawn = new List<float[]>();
            var pool = Answers.Of<ITerrainMeshPool>(new()
            {
                ["AddMeshData"] = args =>
                {
                    var mesh = (MeshData)args![0]!;
                    drawn.Add(mesh.xyz.AsSpan(0, 3 * mesh.VerticesCount).ToArray());
                    return null;
                }
            });
            var client = Answers.Of<ICoreClientAPI>(new() { ["get_Side"] = _ => EnumAppSide.Client });
            var behavior = new BEBehaviorShapeFromAttributes(new StubEntity { Pos = new BlockPos(1, 2, 3) })
            {
                clutterBlock = new StubClutter(), Type = "stub", rotateX = 0.5f, Api = client
            };
            var (mesh, flag) = (AccessTools.FieldRefAccess<BEBehaviorShapeFromAttributes, MeshData?>("mesh"),
                AccessTools.FieldRefAccess<BEBehaviorShapeFromAttributes, bool>("loadMeshDuringTesselation"));
            flag(behavior) = true;

            Assert.That(behavior.OnTesselation(pool, null!), Is.True);
            Assert.That((mesh(behavior)?.VerticesCount, flag(behavior)), Is.EqualTo((0, true)));
            Assert.That(behavior.OnTesselation(pool, null!), Is.True);
            ClutterMeshes.Enabled = false; // the engine's own mesh, kept
            flag(behavior) = true;
            Assert.That(behavior.OnTesselation(pool, null!), Is.True);
            Assert.That(drawn, Has.Count.EqualTo(3));
            Assert.That(drawn[0], Has.Length.EqualTo(12));
            Assert.That(drawn[1], Is.EqualTo(drawn[0]));
            Assert.That(drawn[2], Is.EqualTo(drawn[0]));
            Assert.That(mesh(behavior)?.VerticesCount, Is.EqualTo(4));
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ClutterMeshes.Enabled = true;
        }
    }

    private sealed class StubEntity : BlockEntity;

    private sealed class StubClutter : BlockClutter
    {
        private readonly MeshData _mesh = Quad();
        private readonly ClutterTypeProps _props = new() { Code = "stub", RandomizeYSize = false };

        public override IShapeTypeProps GetTypeProps(string code, ItemStack stack, BEBehaviorShapeFromAttributes be) => _props;

        public override MeshData GetOrCreateMesh(IShapeTypeProps cprops, ITexPositionSource? overrideTexturesource = null,
            string? overrideTextureCode = null, bool forLOD2 = false) => _mesh;

        private static MeshData Quad()
        {
            var mesh = new MeshData(4, 6);
            mesh.AddVertexSkipTex(0, 0, 0);
            mesh.AddVertexSkipTex(1, 0, 0);
            mesh.AddVertexSkipTex(1, 1, 0.25f);
            mesh.AddVertexSkipTex(0, 1, 0.25f);
            mesh.AddQuadIndices(0);
            return mesh;
        }
    }
}
