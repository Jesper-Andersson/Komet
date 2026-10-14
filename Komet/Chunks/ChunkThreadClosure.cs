using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;

namespace Komet.Chunks;

// loadOrGenerateChunkColumn_OnChunkThread runs per queued request every tick and mostly requeues; its lambda's closure
// is allocated in the prologue regardless: 7.5 GB in one world join, a fifth of all allocation on the heap the client
// shares in singleplayer. The rewrite reads the argument directly and builds the closure where the delegate is made.
internal static class ChunkThreadClosure
{
    private const int MaxInstructions = 4096, Prologue = 8;

    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static MethodInfo? Target()
    {
        var supply = AccessTools.TypeByName("Vintagestory.Server.ServerSystemSupplyChunks"); // internal
        var request = AccessTools.TypeByName("Vintagestory.Server.ChunkColumnLoadRequest");
        if (!NotNull(supply) || !NotNull(request)) return null;
        var method = AccessTools.Method(supply, "loadOrGenerateChunkColumn_OnChunkThread", [request, typeof(int)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(bool)) ? method : null;
    }

    // Shape first: closure in the first eight instructions; later local 0 reads load chunkRequest, but one (ldftn)
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !Assert(code.Count > Prologue))
            return code;
        if (code[0].opcode != OpCodes.Newobj || code[0].operand is not ConstructorInfo ctor) return code;
        var closure = ctor.DeclaringType;
        var self = AccessTools.Field(closure, "<>4__this");
        var request = AccessTools.Field(closure, "chunkRequest");
        if (!NotNull(closure) || !NotNull(self) || !NotNull(request) ||
            !Assert(IsPrologue(code, self, request))) return code;
        var delegateAt = -1;
        for (var i = Prologue; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var local = Il.Local(code[i]);
            if (Il.WritesArgument(code[i], 1) || (local == 0 && Il.Local(code[i], Il.Uses.Load) != 0))
                return code; // chunkRequest could change
            if (local != 0 || (i + 1 < code.Count && code[i + 1].LoadsField(request))) continue;
            if (i + 1 >= code.Count || code[i + 1].opcode != OpCodes.Ldftn || !Assert(delegateAt < 0)) return code;
            delegateAt = i;
        }

        if (!Assert(delegateAt > 0)) return code;
        for (var i = Prologue; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (Il.Local(code[i], Il.Uses.Load) != 0 || i == delegateAt) continue;
            // In place: labels and exception blocks stay on it
            (code[i].opcode, code[i].operand) = (OpCodes.Ldarg_1, null);
            (code[i + 1].opcode, code[i + 1].operand) = (OpCodes.Nop, null);
        }

        for (var i = 0; i < Prologue; i++) (code[i].opcode, code[i].operand) = (OpCodes.Nop, null);
        (code[delegateAt].opcode, code[delegateAt].operand) = (OpCodes.Newobj, ctor);
        code.InsertRange(delegateAt + 1,
        [
            new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Stfld, self),
            new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Stfld, request)
        ]);
        Rewritten = true;
        return code;
    }

    // newobj, stloc.0, ldloc.0, ldarg.0, stfld this, ldloc.0, ldarg.1, stfld chunkRequest; no branch landing inside
    private static bool IsPrologue(List<CodeInstruction> code, FieldInfo self, FieldInfo request)
    {
        if (!Assert(code.Count > Prologue) || !NotNull(self)) return false;
        for (var i = 0; i < Prologue; i++)
            if (code[i].labels.Count > 0 || code[i].blocks.Count > 0) return false;

        return Il.Local(code[1], Il.Uses.Store) == 0 && Il.Local(code[2], Il.Uses.Load) == 0 && code[3].IsLdarg(0) &&
               code[4].StoresField(self) && Il.Local(code[5], Il.Uses.Load) == 0 && code[6].IsLdarg(1) &&
               code[7].StoresField(request);
    }
}
