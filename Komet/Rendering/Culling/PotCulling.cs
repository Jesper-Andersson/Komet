using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// PotInFirepitRenderer (survival) draws pot and wobbling lid in Opaque every frame without frustum test: a
// standard-shader setup and draw each (a tenth of the main thread in a kitchen base). Skipped when the bounding sphere
// lies outside the Opaque clip volume (as AnimatableCulling): pot fills its cell, lid sits 13/32 up and wobbles 5/16
// and 1/60 turn (Lid), plus Warp. Skipped draws leave GL state as drawn: culling off, blending on, shader stopped.
internal static class PotCulling
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0xE7ED08273DA67B79UL;

    private const string Renderer = "Vintagestory.GameContent.PotInFirepitRenderer";
    // Half the cell diagonal; the lid's reach beyond the cell, generous: too far costs a draw, too tight a hole
    private const float Cell = 0.8661f, Lid = 1.25f;

    private static readonly SeamGuard Guard = new(nameof(PotCulling), PatchKinds.Replacing, [typeof(PotCulling)]);

    public static bool Enabled { get; set; } = true;

    // Unverified body, survival missing or another mod's patch: engine draws
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    // Counting.Hud totals (main thread)
    public static long Culled { get; private set; }
    public static long Drawn { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 1) || seams[0] is null || !Guard.Match(seams, fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Frame)));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Renderer);
        MethodBase?[] seams =
            [type is null ? null : AccessTools.DeclaredMethod(type, "OnRenderFrame", [typeof(float), typeof(EnumRenderStage)])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Frame(BlockPos ___pos, ICoreClientAPI ___capi, EnumRenderStage stage)
    {
        if (!Enabled || Blocked || stage != EnumRenderStage.Opaque || ___pos is null ||
            ___capi?.Render is not { } render || render.DefaultFrustumCuller is not { } culler) return true;
        var radius = Cell + Lid + AnimatableCulling.Warp(render.ShaderUniforms);
        if (!Finite(radius) || culler.SphereInFrustum(___pos.X + 0.5, ___pos.Y + 0.5, ___pos.Z + 0.5, radius))
        {
            if (Counting.Hud) Drawn++;
            return true;
        }

        if (!LeftState.Holds(LeftState.Kind.Pot))
        {
            render.GlDisableCullFace();
            render.GlToggleBlend(true, EnumBlendMode.Standard);
            LeftState.Left(LeftState.Kind.Pot);
        }

        if (Counting.Hud) Culled++;
        _ = Assert(radius > 0);
        return false;
    }
}
