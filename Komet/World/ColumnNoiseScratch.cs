using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using ColumnNoise = Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise.ColumnNoise;

namespace Komet.World;

// GenTerra.generate (VSEssentials) calls terrainNoise.ForColumn per block column in a Parallel.For; each ColumnNoise
// allocates four arrays (928 B at default height): 24.6 of 132 MB/s flying into new terrain, on server threads sharing
// the client's heap. The IL stays the engine's; the arrays come from Rent, read only after being written.
// - Rent gives the thread's zeroed array of exactly the newarr length; the element types hold only numbers.
// - Only a constructor run while Column (the body's one ForColumn call, the only one in the game) armed the thread gets
//   them; the first disarms.
// - Checked on the IL: the arrays leave the constructor only via its two array fields, and the body's column local is
//   only `this` of BoundMin, BoundMax, NoiseSign and dead on return.
// - Parallel.For bodies run one after another per thread and never wait, so one column is live per thread.
// - A foreign patch on ForColumn, constructor, body or members at Install leaves the engine alone; a later one on the
//   first three stands the arm down for good; later ones on members go unseen.
internal static class ColumnNoiseScratch
{
    private const string Terra = "Vintagestory.ServerMods.GenTerra", Body = "<generate>";

    private const int MaxInstructions = 2048, MaxLength = 64, MaxNested = 64, MaxMethods = 64, MaxReach = 6;
    private const int MaxParameters = 8, MaxFields = 16, Arrays = 4;
    private const int ArrayHeader = 24; // object header, method table and length of an array on 64 bit

    [ThreadStatic] private static bool _armed;

    [ThreadStatic] private static Array?[]? _arrays;

    private static long _saved;
    private static int _rewritten; // 1 constructor, 2 column body, 4 ForColumn watched
    private static bool _installed, _foreign;
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == 7 && !_foreign;
    public static bool StoodDown => _foreign; // another mod patches a seam, or a member is missing

    public static long Saved => Interlocked.Read(ref _saved);

    // ForColumn and constructor first, body last: an arm nobody listens for is only cleared again
    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        (_rewritten, _installed, _foreign, _logger) = (0, false, false, logger);
        var (ctor, body, forColumn) = (Constructor(), ColumnBody(), ForColumn());
        if (!NotNull(harmony) || !NotNull(ctor) || !NotNull(body) || !NotNull(forColumn)) return;
        MethodBase?[] seams = [forColumn, ctor, body, .. Members()];
        if (Foreign(seams, harmony.Id, logger)) return;
        _ = NotNull(harmony.Patch(forColumn, transpiler: new HarmonyMethod(WatchForColumn)));
        _ = NotNull(harmony.Patch(ctor, transpiler: new HarmonyMethod(RewriteConstructor)));
        if ((_rewritten & 5) != 5) return;
        _ = NotNull(harmony.Patch(body, transpiler: new HarmonyMethod(RewriteBody)));
        Volatile.Write(ref _installed, true);
        // A patch added meanwhile reran the transpilers early; GetPatchInfo shares Harmony's lock, so it shows here
        if (!Volatile.Read(ref _foreign) && Foreign(seams, harmony.Id, logger)) Volatile.Write(ref _foreign, true);
    }

    private static bool Foreign(ReadOnlySpan<MethodBase?> seams, string owner, ILogger? logger)
    {
        var at = EngineShape.FirstForeign(seams, owner);
        if (at < 0) return false;
        if (at < seams.Length)
            logger?.Notification("Komet ColumnNoiseScratch stands down: {0}; terrain columns allocate as shipped",
                seams[at] is { } seam
                    ? $"another mod patches {seam.DeclaringType?.Name}.{seam.Name}"
                    : "a ColumnNoise member is missing");
        return true;
    }

    internal static ConstructorInfo? Constructor()
    {
        var ctor = AccessTools.Constructor(typeof(ColumnNoise),
        [
            typeof(NewNormalizedSimplexFractalNoise), typeof(double), typeof(double[]), typeof(double[]),
            typeof(double), typeof(double)
        ]);
        return NotNull(ctor) && Assert(!ctor.IsStatic) ? ctor : null;
    }

    internal static MethodInfo? ForColumn()
    {
        var method = AccessTools.Method(typeof(NewNormalizedSimplexFractalNoise),
            nameof(NewNormalizedSimplexFractalNoise.ForColumn),
            [typeof(double), typeof(double[]), typeof(double[]), typeof(double), typeof(double)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(ColumnNoise)) ? method : null;
    }

    private static MethodBase?[] Members()
    {
        MethodBase?[] members =
        [
            AccessTools.PropertyGetter(typeof(ColumnNoise), nameof(ColumnNoise.BoundMin)),
            AccessTools.PropertyGetter(typeof(ColumnNoise), nameof(ColumnNoise.BoundMax)),
            AccessTools.Method(typeof(ColumnNoise), nameof(ColumnNoise.NoiseSign), [typeof(double), typeof(double)])
        ];
        return Assert(members[0] is null || members[0]!.DeclaringType == typeof(ColumnNoise)) ? members : [null];
    }

    // generate()'s Parallel.For lambda, found by its callees (<>c__DisplayClass34_0.<generate>b__0 in 1.22.7)
    internal static MethodInfo? ColumnBody()
    {
        var terra = AccessTools.TypeByName(Terra);
        var forColumn = ForColumn();
        if (!NotNull(terra) || !NotNull(forColumn)) return null;
        MethodInfo? found = null;
        foreach (var nested in AccessTools.InnerTypes(terra).Bounded(MaxNested))
        {
            foreach (var method in AccessTools.GetDeclaredMethods(nested).Bounded(MaxMethods))
            {
                if (!method.Name.StartsWith(Body, StringComparison.Ordinal) || !CallsForColumn(method, forColumn))
                    continue;
                if (found != null) return null; // two candidates: not the engine this was verified against
                found = method;
            }
        }

        return NotNull(found) && Assert(found.ReturnType == typeof(void)) ? found : null;
    }

    private static bool CallsForColumn(MethodInfo method, MethodInfo forColumn)
    {
        if (!NotNull(method) || !NotNull(forColumn) || method.GetMethodBody() == null) return false;
        foreach (var code in PatchProcessor.GetOriginalInstructions(method).Bounded(MaxInstructions))
            if (code.Calls(forColumn)) return true;
        return false;
    }

    // A transpiler rerun after Install is another mod's patch; Harmony's global lock lets one run log it
    private static void Rerun(int bit)
    {
        _rewritten &= ~bit;
        if (!Assert(bit is 1 or 2 or 4) || !Volatile.Read(ref _installed) || Volatile.Read(ref _foreign)) return;
        Volatile.Write(ref _foreign, true); // read by the generation threads
        _logger?.Notification("Komet ColumnNoiseScratch stands down: another mod patched {0} after install; " +
                              "terrain columns allocate as shipped", bit switch
            {
                4 => "NewNormalizedSimplexFractalNoise.ForColumn",
                1 => "the ColumnNoise constructor",
                _ => "GenTerra's column body"
            });
    }

    // Patched only to hear of other patches; the IL goes back unchanged
    internal static List<CodeInstruction> WatchForColumn(IEnumerable<CodeInstruction> instructions)
    {
        Rerun(4);
        var ctor = Constructor();
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor)) return code;
        var (calls, builds) = (Il.Count(code, c => c.opcode.FlowControl == FlowControl.Call),
            Il.Count(code, c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor)));
        if (Assert(calls == 1 && builds == 1)) _rewritten |= 4;
        return code;
    }

    // Four newarr, two to locals, two to fields on `this`, nothing else leaking a reference; each becomes Rent
    internal static List<CodeInstruction> RewriteConstructor(IEnumerable<CodeInstruction> instructions,
        ILGenerator generator)
    {
        Rerun(1);
        var (entries, past) = (AccessTools.Field(typeof(ColumnNoise), "orderedOctaveEntries"),
            AccessTools.Field(typeof(ColumnNoise), "pastEvaluations"));
        var (take, rent) = (AccessTools.Method(typeof(ColumnNoiseScratch), nameof(Take)),
            AccessTools.Method(typeof(ColumnNoiseScratch), nameof(Rent)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(generator) || !NotNull(entries) ||
            !NotNull(past) || !NotNull(take) || !NotNull(rent)) return code;
        if (!Assert(code.Count > 0) || code[0].labels.Count > 0 || code[0].blocks.Count > 0) return code;
        Type?[] expected =
            [typeof(double), typeof(int), entries.FieldType.GetElementType(), past.FieldType.GetElementType()];
        if (!Numbers(expected[2]) || !Numbers(expected[3])) return code;
        var at = new int[Arrays];
        var found = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (code[i].opcode == OpCodes.Newarr)
            {
                if (found >= Arrays || !Equals(code[i].operand, expected[found]) ||
                    !Held(code, i, found, entries, past)) return code;
                at[found++] = i;
                continue;
            }

            // `this` holds an array from here on: no instance call
            if (found > 2 && code[i].operand is MethodInfo { IsStatic: false }) return code;
            // the instruction after a newarr is the store Held checked
            if (!Allowed(code[i]) && !(i > 0 && code[i - 1].opcode == OpCodes.Newarr)) return code;
        }

        if (!Assert(found == Arrays)) return code;
        var scratch = generator.DeclareLocal(typeof(Array[]));
        for (var j = 0; j < Arrays; j++)
        {
            var i = at[Arrays - 1 - j]; // from the last: the inserts shift only what comes after them
            if (!Index(i, code.Count) || code[i].operand is not Type element) return code;
            // in place: labels and exception blocks stay on it
            (code[i].opcode, code[i].operand) = (OpCodes.Ldloc, scratch);
            code.InsertRange(i + 1,
            [
                new CodeInstruction(OpCodes.Ldc_I4, Arrays - 1 - j),
                new CodeInstruction(OpCodes.Call, rent.MakeGenericMethod(element))
            ]);
        }

        code.InsertRange(0, [new CodeInstruction(OpCodes.Call, take), new CodeInstruction(OpCodes.Stloc, scratch)]);
        _rewritten |= 1;
        return code;
    }

    // Locals: newarr; stloc. Fields: ldarg.0; ldloc; newarr; stfld of exactly their field on `this`
    private static bool Held(List<CodeInstruction> code, int at, int array, FieldInfo entries, FieldInfo past)
    {
        if (!Index(at + 1, code.Count) || !Index(array, Arrays) || at < 2) return false;
        var next = code[at + 1];
        return array switch
        {
            0 or 1 => next.IsStloc(),
            2 => next.StoresField(entries) && code[at - 2].IsLdarg(0),
            _ => next.StoresField(past) && code[at - 2].IsLdarg(0)
        };
    }

    // Besides the arrays' stores: no address taken, no reference stored, no call that could take one
    private static bool Allowed(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return false;
        var op = code.opcode;
        if (op.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch || op == OpCodes.Ret) return true;
        if (op == OpCodes.Stfld) return code.operand is FieldInfo { FieldType.IsPrimitive: true };
        if (op == OpCodes.Stelem || op == OpCodes.Initobj) return code.operand is Type element && Numbers(element);
        if (op == OpCodes.Call) return code.operand is MethodInfo method && Numeric(method);
        var name = op.Name ?? "";
        if (name.StartsWith("stelem.", StringComparison.Ordinal)) return op != OpCodes.Stelem_Ref;
        if (name.StartsWith("ldelem", StringComparison.Ordinal)) return op != OpCodes.Ldelema;
        if (op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) return false;
        return name.StartsWith("ldarg", StringComparison.Ordinal) ||
               name.StartsWith("ldloc", StringComparison.Ordinal) ||
               name.StartsWith("stloc", StringComparison.Ordinal) ||
               name.StartsWith("ldc.", StringComparison.Ordinal) ||
               name.StartsWith("conv.", StringComparison.Ordinal) ||
               name is "ldfld" or "ldlen" or "ldnull" or "dup" or "pop" or "nop" or "add" or "sub" or "mul" or "div"
                   or "div.un" or "rem" or "rem.un" or "neg" or "and" or "or" or "xor" or "not" or "shl" or "shr"
                   or "shr.un" or "ceq" or "cgt" or "cgt.un" or "clt" or "clt.un";
    }

    // Static, or one of ColumnNoise's bound setters on `this`, and nothing but numbers in or out
    private static bool Numeric(MethodInfo method)
    {
        if (!NotNull(method) || !NotNull(method.DeclaringType)) return false;
        if (!method.IsStatic && !Setter(method)) return false;
        if (method.ReturnType != typeof(void) && !method.ReturnType.IsPrimitive) return false;
        var parameters = method.GetParameters();
        for (var i = 0; i < Math.Min(parameters.Length, MaxParameters); i++)
            if (!parameters[i].ParameterType.IsPrimitive) return false;
        return parameters.Length <= MaxParameters;
    }

    // Only ColumnNoise's auto-property setters (ldarg.0; ldarg.1; stfld number; ret), called before the arrays reach
    // `this`
    private static bool Setter(MethodInfo method)
    {
        if (!NotNull(method) || !Assert(!method.IsStatic) || method.DeclaringType != typeof(ColumnNoise) ||
            method.GetMethodBody() == null) return false;
        var code = PatchProcessor.GetOriginalInstructions(method);
        return code.Count == 4 && code[0].IsLdarg(0) && code[1].IsLdarg(1) && code[3].opcode == OpCodes.Ret &&
               code[2].opcode == OpCodes.Stfld &&
               code[2].operand is FieldInfo { FieldType.IsPrimitive: true, IsStatic: false } field &&
               field.DeclaringType == typeof(ColumnNoise);
    }

    // A number, or a struct of numbers: nothing in it can refer to an array
    private static bool Numbers(Type? type)
    {
        if (!NotNull(type) || !type.IsValueType) return false;
        if (type.IsPrimitive) return true;
        var fields = AccessTools.GetDeclaredFields(type);
        for (var i = 0; i < Math.Min(fields.Count, MaxFields); i++)
            if (!fields[i].IsStatic && !fields[i].FieldType.IsPrimitive) return false;
        return Assert(fields.Count <= MaxFields);
    }

    internal static List<CodeInstruction> RewriteBody(IEnumerable<CodeInstruction> instructions)
    {
        Rerun(2);
        var (forColumn, ctor, mine) = (ForColumn(), Constructor(),
            AccessTools.Method(typeof(ColumnNoiseScratch), nameof(Column)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(forColumn) || !NotNull(ctor) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.Calls(forColumn));
        // a column the arm would not cover
        var built = Il.Count(code, c => c.operand is MethodBase method && method == ctor);
        if (!Assert(at >= 0) || built != 0 || !Index(at + 1, code.Count) || !code[at + 1].IsStloc()) return code;
        if (!Il.Confined(code, at + 1, Member, Il.Uses.Address, MaxReach)) return code;
        if (Assert(Il.Substitute(code, at, mine))) _rewritten |= 2;
        return code;
    }

    // The column's address only ever as `this` of ColumnNoise's own double-returning members
    private static bool Member(CodeInstruction code, int operand) =>
        NotNull(code) && Assert(operand >= 0) && operand == 0 && code.opcode == OpCodes.Call &&
        code.operand is MethodInfo { IsStatic: false } member && member.DeclaringType == typeof(ColumnNoise) &&
        member.ReturnType == typeof(double);

    // Replaces terrainNoise.ForColumn(...) with the thread armed for its one constructor; finally disarms on any throw
    internal static ColumnNoise Column(NewNormalizedSimplexFractalNoise noise, double relativeYFrequency,
        double[] amplitudes, double[] thresholds, double noiseX, double noiseZ)
    {
        if (!Enabled || Volatile.Read(ref _foreign) || !Assert(!_armed))
            return noise.ForColumn(relativeYFrequency, amplitudes, thresholds, noiseX, noiseZ);
        _armed = true;
        try
        {
            return noise.ForColumn(relativeYFrequency, amplitudes, thresholds, noiseX, noiseZ);
        }
        finally
        {
            _armed = false; // also when no constructor took it: ForColumn threw before it ran one, say
        }
    }

    // First thing in every ColumnNoise constructor: the thread's arrays if armed, else null; non-null once per arming
    private static Array?[]? Take()
    {
        if (!_armed) return null;
        _armed = false;
        var arrays = _arrays ??= new Array?[Arrays * MaxLength];
        return Assert(arrays.Length == Arrays * MaxLength) ? arrays : null;
    }

    // Replaces newarr T, the slot-th of four: the thread's zeroed array when armed and in range, else a new one
    internal static T[] Rent<T>(int length, Array?[]? arrays, int slot)
    {
        if (arrays is null || (uint)length >= MaxLength || !Index(slot, Arrays)) return new T[length];
        var at = slot * MaxLength + length;
        var cached = arrays[at];
        // exactly T[] in one compare; a cast to int[] would also take uint[], via a helper call
        if (cached?.GetType() != typeof(T[]))
        {
            var fresh = new T[length];
            arrays[at] = fresh;
            return fresh;
        }

        var array = Unsafe.As<T[]>(cached);
        array.AsSpan().Clear();
        if (Counting.Hud) _ = Interlocked.Add(ref _saved, (ArrayHeader + (long)length * Unsafe.SizeOf<T>() + 7) & ~7L);
        return Assert(array.Length == length) ? array : new T[length];
    }
}
