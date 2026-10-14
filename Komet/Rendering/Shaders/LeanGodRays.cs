using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// God rays march up to 172 steps a pixel sunward, adding colour * glow.g. Only sky (and its fringe) has glow.g,
// elsewhere a step adds exactly 0, so colour is read only where glow.g != 0: same sum, fewer reads (one-level target,
// so the branched read takes level 0 as texture() did).
internal static class LeanGodRays
{
    private const string Step = "col += texture(inputTexture, uv) * texture(glowParts, uv).g * weight;";

    private const string LeanStep =
        "float kometGlow = texture(glowParts, uv).g; if (kometGlow != 0.0) col += textureLod(inputTexture, uv, 0.0) * kometGlow * weight;";

    public static bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (ClientSettings.GodRayQuality > 0) ShaderEdits.Reload();
            _ = Assert(field == value);
        }
    } = true;

    public static void Install(Harmony harmony, ICoreClientAPI? api) => ShaderEdits.Install(harmony, api);

    internal static void Edit(ShaderProgram program)
    {
        if (!Enabled || !NotNull(program) || program.PassName != "godrays" ||
            program.FragmentShader is not { Code: { } code } shader || !code.Contains(Step, StringComparison.Ordinal)) return;
        shader.Code = code.Replace(Step, LeanStep, StringComparison.Ordinal);
        _ = Assert(shader.Code.Length > code.Length);
    }
}
