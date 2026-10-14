using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;

namespace Komet.World;

// BlockPatch.Generate (every vegetation patch the world generates) asks the mod loader for GenVegetationAndPatches
// and drops the answer: a LINQ walk over every mod system, an iterator and a closure each time (2 % of all allocations
// while terrain streams in). The call becomes Discard, which answers null without looking.
internal static class PatchLookup
{
    private const int MaxInstructions = 4096;
    private const string Patch = "Vintagestory.ServerMods.NoObf.BlockPatch", System = "GenVegetationAndPatches";

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        var type = AccessTools.TypeByName(Patch);
        var method = type is null ? null : AccessTools.DeclaredMethod(type, "Generate");
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var mine = AccessTools.Method(typeof(PatchLookup), nameof(Discard));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo { Name: "GetModSystem", IsGenericMethod: true } m &&
                                      m.GetGenericArguments() is [var arg] && arg.Name == System);
        if (at < 0 || !Index(at + 1, code.Count) || code[at + 1].opcode != OpCodes.Pop) return code; // the answer must be dropped
        Rewritten = Assert(Il.Substitute(code, at, mine));
        return code;
    }

    // In GetModSystem<T>(bool)'s place: the same stack, no answer
    internal static ModSystem? Discard(IModLoader loader, bool withInheritance)
    {
        if (Enabled || !NotNull(loader)) return null;
        _ = Assert(withInheritance || !withInheritance);
        return loader.GetModSystem(System); // off: the engine's own walk, by name
    }
}
