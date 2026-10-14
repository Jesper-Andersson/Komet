using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// The far shadow map is redrawn each frame, though its light view moves only with the sun (thousandths of a degree a
// frame) and the camera position only shifts the camera-relative map. So it is kept a few frames, sampled via its
// matrix composed with the camera's move. Casters were culled by the view (ShadowView), so it is kept only while the
// view stays (MaxTurn, MaxMove). Redrawn at MaxAge, on a light turn, or when terrain in range changed.
internal static class FarShadowReuse
{
    private const double MaxAgeSeconds = 0.033, MaxMove = 0.5, MaxTurn = 1e-3;

    private static readonly ShadowReuse Map = new(new ShadowReuse.Pass(EnumRenderStage.ShadowFar,
        EnumRenderStage.ShadowFarDone, EnumFrameBuffer.ShadowmapFar, static g => FarMatrix(g),
        static (g, m) => g.shUniforms.ToShadowMapSpaceMatrixFar = m, MaxAgeSeconds, MaxMove, MaxTurn, MaxTurn));

    public static bool Enabled
    {
        get => Map.Enabled;
        set => Map.Enabled = value;
    }

    internal static bool Changed => Map.Changed;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "toShadowMapSpaceMatrixFar")]
    private static extern ref float[] FarMatrix(ClientMain game);

    public static void Install(Harmony harmony)
    {
        Clear();
        var stage = AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage),
            [typeof(EnumRenderStage), typeof(float)]);
        var far = AccessTools.DeclaredMethod(typeof(SystemRenderShadowMap), "OnRenderShadowFar", [typeof(float)]);
        // terrain watchers first: a failed patch leaves nothing kept
        if (!NotNull(stage) || !NotNull(far) || !ShadowReuse.Watch(harmony, Map, 0)) return;
        _ = NotNull(harmony.Patch(far, postfix: new HarmonyMethod(Drawn)));
        _ = NotNull(harmony.Patch(stage, new HarmonyMethod(Staging) { priority = Priority.First }));
    }

    public static void Clear()
    {
        Map.Clear();
        _ = Assert(!Map.Changed) && NotNull(Map);
    }

    // TriggerRenderStage: far pass and its end skipped while the kept map holds; the distant cascade follows the camera
    // and draws its tile every frame
    internal static bool Staging(ClientMain __instance, EnumRenderStage stage, float dt)
    {
        var run = Map.Staging(__instance, stage);
        if (!run && stage == EnumRenderStage.ShadowFarDone) DistantShadows.Tick(__instance, dt);
        return run || !Assert(Finite(dt));
    }

    // OnRenderShadowFar: what the map about to be drawn is kept with
    internal static void Drawn(SystemRenderShadowMap __instance) =>
        _ = NotNull(__instance) && Map.Drawn(Game(__instance)) && Assert(Map.Enabled);

    // Tests: a map kept with this matrix at this camera position, and a mesh added there
    internal static void Use(float[] kept, double x, double y, double z)
    {
        if (NotNull(kept) && Assert(kept.Length == 16)) Map.Use(kept, x, y, z);
    }

    internal static void Touched(Sphere frustumCullSphere)
    {
        if (Finite(frustumCullSphere.radius) && Assert(frustumCullSphere.radius >= 0)) Map.Touch(frustumCullSphere);
    }
}
