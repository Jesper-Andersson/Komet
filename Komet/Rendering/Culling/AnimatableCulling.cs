using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// AnimatableRenderer draws in Opaque, OIT, ShadowFar and ShadowNear every frame with no frustum test: bench world 45
// draws a frame, 38 out of view or shadow map, a tenth of the main thread. The default culler holds each stage's planes
// (MainRenderLoop sets the camera's before Opaque, SystemRenderShadowMap the light's first). The sphere covers every
// vertex the shader can place: mesh radius widened per joint (|M(v - c)| + |Mc - c| <= |M|F * r + |Mc - c|), through
// the model matrix, plus vertexwarp.vsh. A skipped draw leaves GL state as a drawn one (engine does not restore):
// outside OIT depth mask on, standard blending, culling off, or on in Opaque/ShadowNear without backface culling,
// textures, VAO, UBO are bound by the next drawer.
internal static class AnimatableCulling
{
    // EngineShape of Shaped() in 1.22.7
    internal const ulong Fingerprint = 0xDEECF7F8AC7EAFF5UL;

    private const int MaxVertices = 1 << 20, MaxJoints = 4096, MatrixFloats = 16;
    private const float Root3 = 1.7320508f;

    private static readonly ConditionalWeakTable<AnimatableRenderer, Bounds> Known = [];
    private static readonly float[] Model = Mat4f.Create(); // main thread only
    private static readonly SeamGuard Guard = new(nameof(AnimatableCulling), PatchKinds.Replacing, [typeof(AnimatableCulling)]);

    public static bool Enabled { get; set; } = true;

    // Body unverified or patched by another mod: every draw is the engine's
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    // Counting.Hud totals (main thread)
    public static long Culled { get; private set; }
    public static long Drawn { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pos")]
    private static extern ref Vec3d Pos(AnimatableRenderer renderer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "animator")]
    private static extern ref AnimatorBase Animator(AnimatableRenderer renderer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "capi")]
    private static extern ref ICoreClientAPI Capi(AnimatableRenderer renderer);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 2) || !NotNull(seams[0]) || !NotNull(seams[1]) ||
            !Guard.Match(Shaped(), fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Frame)));
        _ = NotNull(harmony.Patch(seams[1], postfix: new HarmonyMethod(Measured)));
        Recheck();
    }

    private static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame),
                [typeof(float), typeof(EnumRenderStage)]),
            AccessTools.DeclaredMethod(typeof(AnimatableRenderer), "mainThreadInit",
                [typeof(ICoreClientAPI), typeof(MeshData), typeof(EnumRenderStage)])
        ];
        return Assert(seams.Length == 2) ? seams : [];
    }

    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped = [.. Seams().AsSpan(0, 1)];
        return Assert(shaped.Length <= EngineShape.MaxMethods) ? shaped : [];
    }

    internal static void Measured(AnimatableRenderer __instance, MeshData meshdata)
    {
        if (!NotNull(__instance) || meshdata?.xyz is not { } xyz) return;
        var count = Math.Min(meshdata.VerticesCount, Math.Min(xyz.Length / 3, MaxVertices));
        if (!Assert(count >= 0)) return;
        var (min, max) = (new Vec3f(float.MaxValue, float.MaxValue, float.MaxValue),
            new Vec3f(float.MinValue, float.MinValue, float.MinValue));
        for (var i = 0; i < Math.Min(count, MaxVertices); i++)
        {
            var (x, y, z) = (xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
            (min.X, min.Y, min.Z) = (Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
            (max.X, max.Y, max.Z) = (Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
        }

        var centre = count == 0
            ? new Vec3f() : new Vec3f((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var radius = 0f;
        for (var i = 0; i < Math.Min(count, MaxVertices); i++)
            radius = Math.Max(radius,
                Distance(xyz[3 * i] - centre.X, xyz[3 * i + 1] - centre.Y, xyz[3 * i + 2] - centre.Z));
        var bounds = new Bounds(centre, radius, Joints(meshdata.CustomInts));
        if (Finite(radius)) Known.AddOrUpdate(__instance, bounds);
    }

    // CustomInts is bound as jointId
    private static int Joints(CustomMeshDataPartInt? ints)
    {
        if (ints?.Values is not { } values || !Assert(ints.Count >= 0)) return 0;
        var (top, low, count) = (0, 0, Math.Min(ints.Count, values.Length));
        for (var i = 0; i < Math.Min(count, MaxVertices); i++)
            (top, low) = (Math.Max(top, values[i]), Math.Min(low, values[i]));
        return low < 0 ? MaxJoints : top; // a negative id is no joint this reasons about
    }

    internal static bool Frame(AnimatableRenderer __instance, EnumRenderStage stage)
    {
        if (!Enabled || Blocked || !NotNull(__instance) || !__instance.ShouldRender ||
            stage is not (EnumRenderStage.Opaque or EnumRenderStage.OIT or EnumRenderStage.ShadowFar
                or EnumRenderStage.ShadowNear)) return true;
        // the engine's own exit
        if (__instance.mtmeshrefOpaque is not { Disposed: false, Initialized: true }) return true;
        if (Visible(__instance))
        {
            if (Counting.Hud) Drawn++;
            return true;
        }

        Leave(__instance, stage);
        if (Counting.Hud) Culled++;
        return false;
    }

    private static bool Visible(AnimatableRenderer renderer)
    {
        var (capi, animator, pos) = (Capi(renderer), Animator(renderer), Pos(renderer));
        if (capi?.Render?.DefaultFrustumCuller is not { } culler || animator?.Matrices is not { } matrices ||
            pos is null || !Known.TryGetValue(renderer, out var bounds)) return true;
        if (!Assert(matrices.Length % MatrixFloats == 0) || bounds.TopJoint >= matrices.Length / MatrixFloats)
            return true;
        var reach = Reach(matrices, bounds);
        var (x, y, z, scale) = Place(renderer, pos, bounds.Centre);
        var radius = scale * reach + Warp(capi.Render.ShaderUniforms);
        return !Finite(radius) || culler.SphereInFrustum(x, y, z, radius);
    }

    // Only joints up to the mesh's highest id: walking all animator matrices was most of a culled draw's cost
    private static float Reach(float[] matrices, Bounds bounds)
    {
        var (c, reach) = (bounds.Centre, 0f);
        var read = Math.Min(bounds.TopJoint + 1, matrices.Length / MatrixFloats);
        _ = Assert(read >= 1 || matrices.Length == 0);
        for (var j = 0; j < Math.Min(read, MaxJoints); j++)
        {
            var m = matrices.AsSpan(j * MatrixFloats, MatrixFloats);
            var (x, y, z) = (m[0] * c.X + m[4] * c.Y + m[8] * c.Z + m[12] - c.X,
                m[1] * c.X + m[5] * c.Y + m[9] * c.Z + m[13] - c.Y,
                m[2] * c.X + m[6] * c.Y + m[10] * c.Z + m[14] - c.Z);
            reach = Math.Max(reach, Frobenius(m) * bounds.Radius + Distance(x, y, z));
        }

        return Assert(reach >= 0) ? reach : float.PositiveInfinity;
    }

    // Model matrix as OnRenderFrame builds it, without camera offset; centre as doubles, no Vec3d per draw
    private static (double X, double Y, double Z, float Scale) Place(AnimatableRenderer renderer, Vec3d pos, Vec3f c)
    {
        var model = Model;
        _ = Mat4f.Identity(model);
        if (renderer.CustomTransform is { Length: MatrixFloats } custom) _ = Mat4f.Multiply(model, model, custom);
        else
        {
            _ = Mat4f.Translate(model, model, 0.5f, 0f, 0.5f);
            _ = Mat4f.Scale(model, model, renderer.ScaleX, renderer.ScaleY, renderer.ScaleZ);
            _ = Mat4f.RotateY(model, model, (renderer.rotationDeg?.Y ?? 0f) * GameMath.DEG2RAD);
            _ = Mat4f.Translate(model, model, -0.5f, 0f, -0.5f);
        }

        return (pos.X + model[0] * c.X + model[4] * c.Y + model[8] * c.Z + model[12],
            pos.Y + model[1] * c.X + model[5] * c.Y + model[9] * c.Z + model[13],
            pos.Z + model[2] * c.X + model[6] * c.Y + model[10] * c.Z + model[14], Frobenius(model));
    }

    // vertexwarp.vsh reach: wind bend (<= 4) and wiggle, water waves, global/drunk warp (<= intensity per axis),
    // temporal glitch (<= 50 * (waviness - 0.1) per axis)
    internal static float Warp(DefaultShaderUniforms? uniforms)
    {
        if (!NotNull(uniforms)) return float.PositiveInfinity;
        var wind = 6f + 6f * Math.Abs(uniforms.WindWaveIntensity) * (1 + Math.Abs(uniforms.WindSpeed)) +
                   Math.Abs(uniforms.WaterWaveIntensity);
        var warp = 50f * Math.Max(0f, uniforms.GlitchWaviness - 0.1f) + 0.1f * Math.Abs(uniforms.GlobalWorldWarp) +
                   2f * Math.Abs(uniforms.PerceptionEffectIntensity);
        return wind + Root3 * warp;
    }

    private static void Leave(AnimatableRenderer renderer, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.OIT || Capi(renderer)?.Render is not { } render) return;
        var cull = stage is EnumRenderStage.Opaque or EnumRenderStage.ShadowNear && !renderer.backfaceCulling;
        _ = Assert(stage != EnumRenderStage.OIT);
        if (LeftState.Holds(cull ? LeftState.Kind.Culling : LeftState.Kind.Unculled)) return;
        render.GLDepthMask(true);
        render.GlToggleBlend(true);
        if (cull) render.GlEnableCullFace();
        else render.GlDisableCullFace();
        LeftState.Left(cull ? LeftState.Kind.Culling : LeftState.Kind.Unculled);
    }

    // At least the 3x3 part's largest stretch
    private static float Frobenius(ReadOnlySpan<float> m) => MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2] +
        m[4] * m[4] + m[5] * m[5] + m[6] * m[6] + m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);

    private static float Distance(float x, float y, float z) => MathF.Sqrt(x * x + y * y + z * z);

    private sealed record Bounds(Vec3f Centre, float Radius, int TopJoint);
}
