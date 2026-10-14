using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Trade-off, off by default: high SSAO samples 24 kernel points and blurs three times; lean takes 16 and blurs once (as
// medium), a little softer and noisier where strongest. Kernel size is ssao.fsh's constant, blur count
// RenderPostprocessingEffects' (SSAOQuality == 1 ? 1 : 3), its 3 handed to Blurs.
internal static class SsaoLean
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0xB8493AEB2F6D2021UL;

    private const string Kernel = "int kernelSize = 24;", LeanKernel = "int kernelSize = 16;";
    private static MethodBase?[] _seams = [];
    private static bool _shaped, _rewritten;

    // Only the high quality's shader has the kernel
    public static bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (ClientSettings.SSAOQuality == 2) ShaderEdits.Reload();
            _ = Assert(field == value);
        }
    }

    internal static bool Matched => _shaped && _rewritten;

    public static void Install(Harmony harmony, ICoreClientAPI? api, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, _rewritten, _seams) = (false, false, Seams());
        ShaderEdits.Install(harmony, api);
        if (!NotNull(harmony) || !Assert(_seams.Length == 1) || _seams[0] is null) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(SsaoLean), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(_seams[0], transpiler: new HarmonyMethod(Rewrite)));
        if (!_rewritten) logger?.Warning("Komet SsaoLean: the SSAO blur count was not found; it blurs as the engine does");
    }

    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
            [AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderPostprocessingEffects))];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // The one ldc.i4.3 shortly after reading SSAOQuality
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;
        var quality = AccessTools.PropertyGetter(typeof(ClientSettings), nameof(ClientSettings.SSAOQuality));
        var read = NotNull(quality) ? Il.Single(code, c => c.Calls(quality)) : -1;
        var at = -1;
        for (var k = 1; k < 6 && read >= 0; k++)
            if (read + k < code.Count && code[read + k].opcode == OpCodes.Ldc_I4_3)
                at = at < 0 ? read + k : -2;
        _rewritten = at >= 0 && Il.Substitute(code, at, AccessTools.DeclaredMethod(typeof(SsaoLean), nameof(Blurs)));
        return code;
    }

    internal static int Blurs() => Enabled && Assert(_rewritten) ? 1 : 3;

    internal static void Edit(ShaderProgram program)
    {
        if (!Enabled || !NotNull(program) || program.PassName != "ssao" || program.FragmentShader is not { Code: { } code } shader ||
            !code.Contains(Kernel, StringComparison.Ordinal)) return;
        shader.Code = code.Replace(Kernel, LeanKernel, StringComparison.Ordinal);
        _ = Assert(shader.Code.Length == code.Length);
    }
}
