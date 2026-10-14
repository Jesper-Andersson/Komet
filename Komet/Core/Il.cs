using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Komet.Core;

// IL analysis for transpilers: every question answers no (false, -1) unless proven, so unexpected bodies stay
// unchanged.
internal static class Il
{
    [Flags]
    public enum Uses
    {
        None = 0,
        Load = 1,
        Address = 2,
        Store = 4,
        Any = Load | Address | Store
    }

    public const int MaxInstructions = 1 << 13, MaxReach = 32;
    private const int MaxParameters = 32;

    private static readonly string[] Injected =
    [
        "__instance", "__originalMethod", "__args", "__result", "__resultRef", "__state", "__exception", "__runOriginal"
    ];

    // A truncated body would be invalid IL: past the cap it is rejected, not cut.
    public static bool Take(IEnumerable<CodeInstruction> instructions, int max, out List<CodeInstruction> code)
    {
        code = NotNull(instructions) ? [.. instructions] : [];
        return Assert(max is > 0 and <= MaxInstructions) && code.Count <= max;
    }

    public static int Count(List<CodeInstruction> code, System.Func<CodeInstruction, bool> match)
    {
        if (!NotNull(code) || !NotNull(match) || !Assert(code.Count <= MaxInstructions)) return -1;
        var count = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++) count += match(code[i]) ? 1 : 0;
        return count;
    }

    // Loop test "field.Count > 0" (ldfld, call count, ldc.i4.0, bgt back): the call's index, -1 for none or several.
    public static int CountLoop(List<CodeInstruction> code, FieldInfo field, MethodInfo count)
    {
        if (!NotNull(code) || !NotNull(field) || !NotNull(count)) return -1;
        var site = -1;
        for (var i = 1; i < Math.Min(code.Count - 2, MaxInstructions); i++)
        {
            if (!code[i].Calls(count) || !code[i - 1].LoadsField(field) || code[i + 1].opcode != OpCodes.Ldc_I4_0 ||
                (code[i + 2].opcode != OpCodes.Bgt && code[i + 2].opcode != OpCodes.Bgt_S) ||
                code[i + 2].operand is not Label target || code.FindIndex(0, i, c => c.labels.Contains(target)) < 0)
                continue;
            if (site >= 0) return -1;
            site = i;
        }

        return site;
    }

    public static int Single(List<CodeInstruction> code, System.Func<CodeInstruction, bool> match)
    {
        if (!NotNull(code) || !NotNull(match) || !Assert(code.Count <= MaxInstructions)) return -1;
        var at = -1;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (!match(code[i])) continue;
            if (at >= 0) return -1;
            at = i;
        }

        return at;
    }

    // The one instruction that matches becomes a call of `method` (Single, then Substitute); false for none or several
    public static bool SubstituteSingle(List<CodeInstruction> code, System.Func<CodeInstruction, bool> match, MethodInfo? method)
    {
        var at = Single(code, match);
        return at >= 0 && Index(at, code.Count) && NotNull(method) && Substitute(code, at, method);
    }

    // The instruction at `at` becomes a call of `method`, keeping labels and blocks; same stack effect only.
    public static bool Substitute(List<CodeInstruction> code, int at, MethodInfo method)
    {
        if (!NotNull(code) || !NotNull(method) || !Index(at, code.Count) || !Assert(method.IsStatic)) return false;
        var effect = Effect(code[at]);
        if (effect.Pops < 0 || Effect(new CodeInstruction(OpCodes.Call, method)) != effect) return false;
        (code[at].opcode, code[at].operand) = (OpCodes.Call, method);
        return true;
    }

    // starg/ldarga of instance argument `index` (1 = first after `this`); short forms carry a byte, long a short
    public static bool WritesArgument(CodeInstruction code, int index)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0 && index > 0)) return true;
        var op = code.opcode;
        if (op != OpCodes.Starg && op != OpCodes.Starg_S && op != OpCodes.Ldarga && op != OpCodes.Ldarga_S)
            return false;
        var at = code.operand switch
        {
            byte b => b,
            short s => s,
            int n => n,
            ParameterInfo p => p.Position + 1,
            _ => -1
        };
        return at == index || at < 0;
    }

    // -1 unless one of the asked uses
    public static int Local(CodeInstruction code, Uses uses = Uses.Any)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return -1;
        var op = code.opcode;
        var use = Uses.None;
        if (code.IsStloc()) use = Uses.Store;
        // IsLdloc takes both
        else if (code.IsLdloc()) use = op == OpCodes.Ldloca || op == OpCodes.Ldloca_S ? Uses.Address : Uses.Load;
        if ((use & uses) == Uses.None) return -1;
        if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return 0;
        if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return 1;
        if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return 2;
        if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return 3;
        return code.operand switch
        {
            LocalBuilder builder => builder.LocalIndex,
            byte index => index,
            short index => index,
            int index => index,
            _ => -1
        };
    }

    // The instruction popping the value pushed at `at`, and as which operand, within `reach` non-branch ones; (-1, -1)
    // unproven
    public static (int At, int Operand) Consumer(List<CodeInstruction> code, int at, int reach = MaxReach)
    {
        if (!NotNull(code) || !Index(at, code.Count) || !Assert(reach is > 0 and <= MaxReach)) return (-1, -1);
        var height = 1; // the tracked value and everything pushed on top of it
        for (var step = 1; step <= Math.Min(Math.Min(code.Count - at - 1, reach), MaxReach); step++)
        {
            var i = at + step;
            var c = code[i];
            if (c.labels.Count > 0 || c.blocks.Count > 0) return (-1, -1);
            var (pops, pushes) = Effect(c);
            if (pops < 0) return (-1, -1);
            if (pops >= height) return (i, pops - height);
            if (c.opcode.FlowControl is not (FlowControl.Next or FlowControl.Call)) return (-1, -1);
            height += pushes - pops;
            if (!Assert(height > 0)) return (-1, -1);
        }

        return (-1, -1);
    }

    // (-1, -1) when the count depends on more than the operand
    public static (int Pops, int Pushes) Effect(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return (-1, -1);
        var op = code.opcode;
        if (op.FlowControl == FlowControl.Call || op == OpCodes.Newobj)
        {
            if (code.operand is not MethodBase m || op == OpCodes.Calli) return (-1, -1);
            var pops = m.GetParameters().Length + (m.IsStatic || op == OpCodes.Newobj ? 0 : 1);
            return (pops, op == OpCodes.Newobj || (m is MethodInfo i && i.ReturnType != typeof(void)) ? 1 : 0);
        }

        var popped = op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            _ => -1
        };
        var pushed = op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => -1
        };
        return popped < 0 || pushed < 0 ? (-1, -1) : (popped, pushed);
    }

    // Whether the local stored at `store` stays in the method: every use is one of `uses` and reaches an `allowed`
    // operand.
    public static bool Confined(List<CodeInstruction> code, int store, System.Func<CodeInstruction, int, bool> allowed,
        Uses uses = Uses.Load, int reach = MaxReach)
    {
        if (!NotNull(code) || !NotNull(allowed) || !Index(store, code.Count) || !Assert(code[store].IsStloc()))
            return false;
        var local = Local(code[store]);
        if (local < 0 || !Assert(uses is Uses.Load or Uses.Address or (Uses.Load | Uses.Address)) ||
            !Assert(code.Count <= MaxInstructions))
            return false;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (i == store || Local(code[i]) != local) continue;
            if (Local(code[i], uses) != local) return false;
            var (use, operand) = Consumer(code, i, reach);
            if (use < 0 || !allowed(code[use], operand)) return false;
        }

        return true;
    }

    // Harmony throws on an unbindable parameter (2.4 MethodCreatorTools.EmitCallParameter).
    public static bool Binds(MethodBase original, MethodInfo patch)
    {
        if (!NotNull(original) || !NotNull(patch)) return false;
        var (wanted, given) = (patch.GetParameters(), original.GetParameters());
        if (!Assert(wanted.Length <= MaxParameters) || !Assert(given.Length <= MaxParameters)) return false;
        for (var i = 0; i < Math.Min(wanted.Length, MaxParameters); i++)
        {
            var name = wanted[i].Name ?? "";
            var bound = name.StartsWith("___", StringComparison.Ordinal)
                ? AccessTools.Field(original.DeclaringType, name[3..]) is not null
                : Array.IndexOf(Injected, name) >= 0 || Array.Exists(given, parameter => parameter.Name == name);
            if (!bound) return false;
        }

        return true;
    }
}
