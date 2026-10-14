using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ClientMain reads the GL error flag twice a frame (CheckGlErrorAlways); under glthread each read syncs (0.13 ms a
// frame). Both sites now ask every EveryCalls-th call; the flag stays set, so errors log later and GL_OUT_OF_MEMORY
// still ends the game. GL debug mode asks every call; other callers (mods) untouched.
internal static class GlErrorPoll
{
    public const int EveryCalls = 16;
    private const int MaxInstructions = 256, FinalBit = 1, BlitBit = 2, AllBits = 3;

    private static int _calls, _rewritten;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits; // both call sites ask through Check
    public static long Skipped { get; private set; } // asks left out, a total while Counting.Hud (main thread)

    public static void Install(Harmony harmony)
    {
        (_calls, _rewritten) = (0, 0);
        var final = AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.RenderAfterFinalComposition),
            [typeof(float)]);
        var blit = AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.RenderAfterBlit), [typeof(float)]);
        if (!NotNull(harmony) || !NotNull(final) || !NotNull(blit)) return;
        _ = NotNull(harmony.Patch(final, transpiler: new HarmonyMethod(Rewrite)));
        _ = NotNull(harmony.Patch(blit, transpiler: new HarmonyMethod(Rewrite)));
    }

    // The one CheckGlErrorAlways call goes through Check; any other shape keeps the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var bit = original?.Name == nameof(ClientMain.RenderAfterBlit) ? BlitBit : FinalBit;
        _rewritten &= ~bit;
        var (always, check) = (AccessTools.Method(typeof(ClientPlatformAbstract),
            nameof(ClientPlatformAbstract.CheckGlErrorAlways)), AccessTools.Method(typeof(GlErrorPoll), nameof(Check)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(always) || !NotNull(check))
            return code;
        if (Assert(Il.SubstituteSingle(code, c => c.Calls(always), check))) _rewritten |= bit;
        return code;
    }

    internal static void Check(ClientPlatformAbstract platform, string message)
    {
        if (Ask(FrameSettings.GlDebugMode))
            platform.CheckGlErrorAlways(message); // a null platform throws as the engine's callvirt did
        else if (Counting.Hud) Skipped++;
    }

    internal static bool Ask(bool debugMode)
    {
        if (Enabled && !debugMode && ++_calls < EveryCalls) return false;
        _calls = 0;
        return Assert(_calls < EveryCalls);
    }
}
