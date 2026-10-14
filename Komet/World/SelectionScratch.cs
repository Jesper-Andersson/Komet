using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Komet.World;

// The block selection ray asks every block it steps over for its decor selection boxes, and WorldChunk makes a new list
// for each (4 % of all allocations in a steady-state trace, nearly all of them empty). The rewrite hands the method one
// list per thread, cleared: its only caller (GameMain.GetBlockIntersectionBoxes) copies the boxes out at once.
internal static class SelectionScratch
{
    private const int MaxInstructions = 2048;

    [ThreadStatic] private static List<Cuboidf>? _boxes;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        var method = AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.GetDecorSelectionBoxes),
            [typeof(IBlockAccessor), typeof(BlockPos)]);
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite)));
    }

    // The one `new List<Cuboidf>()` becomes Scratch(); any other shape of the body is left as it is
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (ctor, mine) = (AccessTools.Constructor(typeof(List<Cuboidf>), []), AccessTools.Method(typeof(SelectionScratch), nameof(Scratch)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor) || !NotNull(mine)) return code;
        if (Il.Count(code, c => c.opcode == OpCodes.Newobj) != 1) return code;
        Rewritten = Il.SubstituteSingle(code, c => c.opcode == OpCodes.Newobj && ReferenceEquals(c.operand, ctor), mine);
        return code;
    }

    // Cleared for this call; a thread of its own for the server's
    internal static List<Cuboidf> Scratch()
    {
        if (!Enabled) return [];
        var boxes = _boxes ??= new List<Cuboidf>(8);
        boxes.Clear();
        return Assert(boxes.Count == 0) ? boxes : [];
    }
}
