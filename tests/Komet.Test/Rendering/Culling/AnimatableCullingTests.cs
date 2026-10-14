using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

internal class NoUbo : UBORef
{
    public override void Bind()
    {
    }

    public override void Unbind()
    {
    }

    public override void Update<T>(T data)
    {
    }

    public override void Update<T>(T data, int offset, int size)
    {
    }

    public override void Update(object data, int offset, int size)
    {
    }
}

// AnimatableRenderer skips only a draw that lies wholly outside the stage's clip volume - the model's reach taken through its joint
// matrices, its scale and the shader's warps - and a skipped draw leaves the GL state exactly as the engine's drawn one does
public sealed class AnimatableCullingTests
{
    private static readonly MethodInfo Draw =
        AccessTools.DeclaredMethod(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame));

    private static readonly EnumRenderStage[] Stages =
        [EnumRenderStage.Opaque, EnumRenderStage.OIT, EnumRenderStage.ShadowFar, EnumRenderStage.ShadowNear];

    private readonly List<string> _state = [];

    [TearDown]
    public void Restore()
    {
        AnimatableCulling.Enabled = true;
        Counting.Hud = false;
    }

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-animatableculling");
        AnimatableCulling.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(AnimatableCulling.Matched, Is.True,
                "AnimatableRenderer.OnRenderFrame is not the body verified");
            Assert.That(AnimatableCulling.Blocked, Is.False);
            Assert.That(Harmony.GetPatchInfo(Draw)?.Prefixes.Select(p => p.owner), Is.EqualTo([harmony.Id]));
        });
    }

    // In front of the camera it draws, behind it it does not; a model 400 blocks behind still draws when a joint its vertices read
    // carries it 800 blocks forward, or when it is scaled up that far, or placed there by its custom transform. A joint no vertex
    // reads (ElementTransforms.values[jointId] in the shaders) does not keep it.
    [Test]
    public void CullsOnlyWhatCannotReachTheView()
    {
        using var harmony = new TestHarmony("komet-test-animatableculling");
        AnimatableCulling.Install(harmony, new QuietLogger());
        var (ahead, behind) = (Renderer(new Vec3d(0, 0, -100)), Renderer(new Vec3d(0, 0, 400)));
        var carried = Renderer(new Vec3d(0, 0, 400), joint: 800, jointId: 1);
        var unread = Renderer(new Vec3d(0, 0, 400), joint: 800);
        var scaled = Renderer(new Vec3d(0, 0, 400));
        scaled.ScaleZ = 1000;
        var placed = Renderer(new Vec3d(0, 0, 400));
        placed.CustomTransform = Mat4f.Translate(Mat4f.Create(), Mat4f.Create(), 0, 0, -800);
        Assert.Multiple(() =>
        {
            Assert.That(AnimatableCulling.Frame(ahead, EnumRenderStage.Opaque), Is.True, "ahead");
            Assert.That(AnimatableCulling.Frame(behind, EnumRenderStage.Opaque), Is.False, "behind");
            Assert.That(AnimatableCulling.Frame(carried, EnumRenderStage.Opaque), Is.True, "carried by its joint");
            Assert.That(AnimatableCulling.Frame(unread, EnumRenderStage.Opaque), Is.False, "carried by a joint none reads");
            Assert.That(AnimatableCulling.Frame(scaled, EnumRenderStage.Opaque), Is.True, "scaled");
            Assert.That(AnimatableCulling.Frame(placed, EnumRenderStage.Opaque), Is.True, "custom transform");
        });
    }

    // Switched off, not animating, not uploaded, a joint id past the animator's matrices or a stage it does not know: the engine draws
    [Test]
    public void EverythingItCannotProveIsLeftToTheEngine()
    {
        using var harmony = new TestHarmony("komet-test-animatableculling");
        AnimatableCulling.Install(harmony, new QuietLogger());
        var off = Renderer(new Vec3d(0, 0, 400));
        var idle = Renderer(new Vec3d(0, 0, 400));
        idle.ShouldRender = false;
        var stray = Renderer(new Vec3d(0, 0, 400), jointId: 5);
        var unknown = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        unknown.ShouldRender = true;
        unknown.mtmeshrefOpaque = Uploaded();
        var behind = Renderer(new Vec3d(0, 0, 400));
        Assert.Multiple(() =>
        {
            Assert.That(AnimatableCulling.Frame(idle, EnumRenderStage.Opaque), Is.True, "not animating");
            Assert.That(AnimatableCulling.Frame(stray, EnumRenderStage.Opaque), Is.True, "joint id past the matrices");
            Assert.That(AnimatableCulling.Frame(unknown, EnumRenderStage.Opaque), Is.True, "never measured");
            Assert.That(AnimatableCulling.Frame(behind, EnumRenderStage.AfterOIT), Is.True, "another stage");
            AnimatableCulling.Enabled = false;
            Assert.That(AnimatableCulling.Frame(off, EnumRenderStage.Opaque), Is.True, "switched off");
        });
    }

    // The engine's own draw, run against a render API that records the state it leaves, and the skipped one in every stage and both
    // culling modes: the same depth mask, blend and face culling at the end
    [Test]
    public void ASkippedDrawLeavesTheStateADrawnOneLeaves([Values] bool backfaceCulling)
    {
        var engine = new List<string>();
        foreach (var stage in Stages)
        {
            var drawn = Renderer(new Vec3d(0, 0, 400));
            drawn.backfaceCulling = backfaceCulling;
            _state.Clear();
            _ = Draw.Invoke(drawn, [0.016f, stage]); // unpatched: the engine's own draw
            engine.Add(Final());
        }

        using var harmony = new TestHarmony("komet-test-animatableculling");
        AnimatableCulling.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            for (var i = 0; i < Stages.Length; i++)
            {
                var skipped = Renderer(new Vec3d(0, 0, 400));
                skipped.backfaceCulling = backfaceCulling;
                _state.Clear();
                Assert.That(AnimatableCulling.Frame(skipped, Stages[i]), Is.False, Stages[i].ToString());
                Assert.That(Final(), Is.EqualTo(engine[i]), $"{Stages[i]}, backface culling {backfaceCulling}");
            }
        });
    }

    private string Final()
    {
        var last = new SortedDictionary<string, string>();
        foreach (var call in _state)
        {
            var (key, value) = call.StartsWith("cull", StringComparison.Ordinal)
                ? ("cull", call)
                : (call.Split(' ')[0], call);
            last[key] = value;
        }

        return string.Join(", ", last.Values);
    }

    private AnimatableRenderer Renderer(Vec3d pos, int joint = 0, int jointId = 0)
    {
        var renderer = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        AccessTools.Field(typeof(AnimatableRenderer), "pos").SetValue(renderer, pos);
        AccessTools.Field(typeof(AnimatableRenderer), "capi").SetValue(renderer, Client());
        AccessTools.Field(typeof(AnimatableRenderer), "animator").SetValue(renderer, Animator(joint));
        (renderer.ModelMat, renderer.rotationDeg, renderer.renderColor) =
            (Mat4f.Create(), new Vec3f(), new Vec4f(1, 1, 1, 1));
        (renderer.ScaleX, renderer.ScaleY, renderer.ScaleZ, renderer.backfaceCulling) = (1, 1, 1, true);
        (renderer.ShouldRender, renderer.StabilityAffected, renderer.LightAffected) = (true, true, true);
        (renderer.mtmeshrefOpaque, renderer.mtmeshrefTransparent) = (Uploaded(), Uploaded());
        var mesh = new MeshData(8, 12) { CustomInts = new CustomMeshDataPartInt(8) };
        for (var i = 0; i < 8; i++)
        {
            mesh.AddVertexSkipTex(i & 1, (i >> 1) & 1, (i >> 2) & 1);
            mesh.CustomInts.Add(jointId);
        }

        AnimatableCulling.Measured(renderer, mesh);
        return renderer;
    }

    private static ClientAnimator Animator(int forward)
    {
        var animator = (ClientAnimator)RuntimeHelpers.GetUninitializedObject(typeof(ClientAnimator));
        var matrices = new float[32];
        Mat4f.Create().CopyTo(matrices, 0);
        var carry = Mat4f.Translate(Mat4f.Create(), Mat4f.Create(), 0, 0, -forward);
        carry.CopyTo(matrices, 16);
        AccessTools.Field(typeof(AnimatorBase), "TransformationMatricesDefaultPose").SetValue(animator, matrices);
        animator.jointsById = new Dictionary<int, AnimationJoint> { [1] = new() };
        return animator;
    }

    private static MultiTextureMeshRef Uploaded() => new([new NoMesh()], [1]);

    // A camera at the origin looking down -z, 70 degrees high, planes as MainRenderLoop computes them
    internal static FrustumCulling Culler()
    {
        var culler = new FrustumCulling();
        var projection = Mat4d.Perspective(Mat4d.Create(), 70 * GameMath.DEG2RAD, 16 / 9.0, 0.1, 1536);
        culler.CalcFrustumEquations(new BlockPos(0), projection, Mat4d.Create());
        return culler;
    }

    private ICoreClientAPI Client()
    {
        var culler = Culler();
        var shader = Answers.Of<IShaderProgram>(new() { ["get_UBOs"] = _ => Ubos() });
        var render = Answers.Of<IRenderAPI>(new()
        {
            ["get_DefaultFrustumCuller"] = _ => culler, ["get_ShaderUniforms"] = _ => new DefaultShaderUniforms(),
            ["GetEngineShader"] = _ => shader, ["get_CurrentModelviewMatrix"] = _ => Mat4f.Create(),
            ["GLDepthMask"] = a => Record($"depthmask {a![0]}"),
            ["GlToggleBlend"] = a => Record($"blend {a![0]} {a[1]}"),
            ["GlEnableCullFace"] = _ => Record("cull on"), ["GlDisableCullFace"] = _ => Record("cull off")
        });
        var player = Player();
        var world = Answers.Of<IClientWorldAccessor>(new()
        {
            ["get_Player"] = _ => player, ["get_BlockAccessor"] = _ => Answers.Of<IBlockAccessor>([])
        });
        return Answers.Of<ICoreClientAPI>(new() { ["get_Render"] = _ => render, ["get_World"] = _ => world });
    }

    // A ClientPlayer without a game, with an entity: the draw reads the camera position from it
    private static ClientPlayer Player()
    {
        var (player, data) = ((ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer)),
            ClientWorldPlayerData.CreateNew());
        AccessTools.Field(typeof(ClientWorldPlayerData), "entityplayer").SetValue(data, new EntityPlayer());
        AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(player, data);
        return player;
    }

    private object? Record(string call)
    {
        _state.Add(call);
        return null;
    }

    private static Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> Ubos() =>
        new() { ["Animation"] = new NoUbo() };
}
