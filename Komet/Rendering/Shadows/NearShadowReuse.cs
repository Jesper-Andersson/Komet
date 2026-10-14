using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// The near shadow map (30-39 blocks, the forest around the player: 1.5 ms of GPU a frame at ground level) is redrawn
// every frame though its light moves only with the sun. As FarShadowReuse, it is kept for a frame or two, sampled via
// its matrix composed with the camera's move. Its box follows the camera's view, so turning would leave the kept map:
// the box is drawn Margin blocks wider (a twentieth less resolution), and the map holds while the camera turns less
// than MaxTurn. Kept at most MaxAgeSeconds: at low frame rates every frame draws it, so nothing near lags visibly.
internal static class NearShadowReuse
{
    private const double MaxAgeSeconds = 0.017, MaxMove = 0.25, MaxTurn = 0.03, MaxLens = 1e-6, Margin = 2, NearBelow = 50;

    private static readonly ShadowReuse Map = new(new ShadowReuse.Pass(EnumRenderStage.ShadowNear,
        EnumRenderStage.ShadowNearDone, EnumFrameBuffer.ShadowmapNear, static g => NearMatrix(g),
        static (g, m) => g.shUniforms.ToShadowMapSpaceMatrixNear = m, MaxAgeSeconds, MaxMove, MaxTurn, MaxLens));

    public static bool Enabled
    {
        get => Map.Enabled;
        set => Map.Enabled = value;
    }

    // Counting.Hud totals (main thread)
    public static long Reused { get; private set; }
    public static long Redrawn { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "toShadowMapSpaceMatrixNear")]
    private static extern ref float[] NearMatrix(ClientMain game);

    public static void Install(Harmony harmony)
    {
        Clear();
        var stage = AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage),
            [typeof(EnumRenderStage), typeof(float)]);
        var near = AccessTools.DeclaredMethod(typeof(SystemRenderShadowMap), "OnRenderShadowNear", [typeof(float)]);
        var box = AccessTools.DeclaredMethod(typeof(ShadowBox), nameof(ShadowBox.update));
        if (!NotNull(stage) || !NotNull(near) || !NotNull(box) || !ShadowReuse.Watch(harmony, Map, 1)) return;
        _ = NotNull(harmony.Patch(box, postfix: new HarmonyMethod(Widened)));
        _ = NotNull(harmony.Patch(near, postfix: new HarmonyMethod(Drawn)));
        _ = NotNull(harmony.Patch(stage, new HarmonyMethod(Staging) { priority = Priority.First }));
    }

    public static void Clear()
    {
        Map.Clear();
        _ = Assert(!Map.Changed) && NotNull(Map);
    }

    // ShadowBox.update: the near box (SHADOW_DISTANCE below NearBelow; the far one is 60 or more) Margin wider each way
    internal static void Widened(ShadowBox __instance)
    {
        if (!Enabled || __instance is null || ShadowBox.SHADOW_DISTANCE >= NearBelow || !Assert(Margin > 0)) return;
        (__instance.minX, __instance.maxX) = (__instance.minX - Margin, __instance.maxX + Margin);
        (__instance.minY, __instance.maxY) = (__instance.minY - Margin, __instance.maxY + Margin);
    }

    // TriggerRenderStage: near pass and its end skipped while the kept map holds
    internal static bool Staging(ClientMain __instance, EnumRenderStage stage)
    {
        var run = Map.Staging(__instance, stage);
        if (!run && stage == EnumRenderStage.ShadowNear && Counting.Hud) Reused++;
        return run || !Assert(Reused >= 0);
    }

    // OnRenderShadowNear: what the map about to be drawn is kept with
    internal static void Drawn(SystemRenderShadowMap __instance)
    {
        if (NotNull(__instance) && Map.Drawn(Game(__instance)) && Counting.Hud) Redrawn++;
        _ = Assert(Redrawn >= 0);
    }
}
