using HarmonyLib;
using Komet.Options;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Komet's edits to engine shaders at program load, in this order, from one LoadShaderProgram postfix (DistantShadows
// has its own). A switch changed in-world asks for one reload, batched with the settings page's other changes.
internal static class ShaderEdits
{
    private static ICoreClientAPI? _api;

    public static void Install(Harmony harmony, ICoreClientAPI? api)
    {
        _api = api;
        var (load, edit) = (AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram",
            [typeof(ShaderProgram), typeof(bool)]), AccessTools.DeclaredMethod(typeof(ShaderEdits), nameof(Edit)));
        if (!NotNull(harmony) || !NotNull(load) || !NotNull(edit)) return;
        if (Harmony.GetPatchInfo(load)?.Postfixes.Any(p => p.PatchMethod == edit) == true) return; // the first feature patched it
        _ = NotNull(harmony.Patch(load, postfix: new HarmonyMethod(edit)));
    }

    internal static void Edit(ShaderProgram program)
    {
        if (!NotNull(program) || !Assert(program.VertexShader is null || NotNull(program.VertexShader.Code))) return;
        LeanVaryings.Edit(program);
        SharpUpscale.Edit(program);
        SsaoLean.Edit(program);
        LeanGodRays.Edit(program);
    }

    public static void Reload()
    {
        if (_api?.World is null || !NotNull(_api) || !Assert(_api.Shader is not null)) return;
        EngineOptions.ReloadShaders(_api);
    }
}
