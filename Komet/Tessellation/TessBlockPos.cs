using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// ChunkTesselator allocates a BlockPos for every IDrawYAdjustable block (plants, crops) it asks how far to lower: 1.8
// MB/s of garbage while tessellating. Vanilla answers never read the position (they check the block below in
// chunkExtBlocks), so calls get the thread's own BlockPos where the reached implementation is proven, once per type,
// only to read its fields; other types, and all calls while off, get a new one.
internal static class TessBlockPos
{
    private const int MaxInstructions = 8192, MaxBody = 4096, BlockBit = 1, DecorBit = 2, AllBits = 3;
    private const int DimensionSize = 32768; // BlockPos(int, int, int): Y is y % 32768, the dimension y / 32768

    [ThreadStatic] private static BlockPos? _scratch;
    private static readonly ConcurrentDictionary<Type, bool> Proven = new();
    private static int _rewritten;
    private static long _saved;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits;

    public static long Saved => Interlocked.Read(ref _saved);

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;
        var tesselator = typeof(ChunkTesselator);
        var block = AccessTools.DeclaredMethod(tesselator, "TesselateBlock",
            [typeof(Block), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)]);
        var decor = AccessTools.DeclaredMethod(tesselator, "BuildDecorPolygons",
            [typeof(int), typeof(int), typeof(int), typeof(Dictionary<int, Block>), typeof(bool)]);
        if (!NotNull(harmony) || !NotNull(block) || !NotNull(decor)) return;
        _ = NotNull(harmony.Patch(block, transpiler: new HarmonyMethod(Rewrite)));
        _ = NotNull(harmony.Patch(decor, transpiler: new HarmonyMethod(Rewrite)));
    }

    // AdjustYPosition calls taking a fresh new BlockPos(x, y, z): ctor -> At, call -> Adjust; else the IL stays
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var bit = original?.Name == "TesselateBlock" ? BlockBit : DecorBit;
        _rewritten &= ~bit;
        var (ctor, adjust) = (AccessTools.Constructor(typeof(BlockPos), [typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(typeof(IDrawYAdjustable), nameof(IDrawYAdjustable.AdjustYPosition)));
        var (at, call) = (AccessTools.Method(typeof(TessBlockPos), nameof(At)),
            AccessTools.Method(typeof(TessBlockPos), nameof(Adjust)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ctor) || !NotNull(adjust))
            return code;
        var site = Il.Single(code, c => c.Calls(adjust));
        if (!Assert(site > 0)) return code;
        var made = code.FindLastIndex(site, site + 1, c => c.Is(OpCodes.Newobj, ctor));
        if (!Assert(made >= 0) || Il.Consumer(code, made) != (site, 1)) return code;
        if (Il.Substitute(code, made, at) && Il.Substitute(code, site, call)) _rewritten |= bit;
        return code;
    }

    // Stands in for new BlockPos(x, y, z): the thread's own, set exactly as that constructor sets a new one
    internal static BlockPos At(int x, int y, int z)
    {
        var pos = _scratch ??= new BlockPos(0);
        (pos.X, pos.Y, pos.Z, pos.dimension) = (x, y % DimensionSize, z, y / DimensionSize);
        return pos;
    }

    // Replaces AdjustYPosition(pos, ...): the thread's position only where proven, else a new one (null throws alike)
    internal static float Adjust(IDrawYAdjustable target, BlockPos pos, Block[] blocks, int index)
    {
        var type = target.GetType();
        if (!Proven.TryGetValue(type, out var proven)) proven = Proven.GetOrAdd(type, Confined(type));
        if (!Enabled || !proven)
            pos = new BlockPos(pos.X, pos.dimension * DimensionSize + pos.Y, pos.Z);
        else if (Counting.Hud) _ = Interlocked.Increment(ref _saved);
        return target.AdjustYPosition(pos, blocks, index);
    }

    // Implementation a call on this type reaches, plus its override if virtual: each may only read position fields
    internal static bool Confined(Type type)
    {
        if (!NotNull(type) || !typeof(IDrawYAdjustable).IsAssignableFrom(type)) return false;
        var map = type.GetInterfaceMap(typeof(IDrawYAdjustable));
        var slot = Array.FindIndex(map.InterfaceMethods, m => m.Name == nameof(IDrawYAdjustable.AdjustYPosition));
        if (!Index(slot, map.TargetMethods.Length)) return false;
        var target = map.TargetMethods[slot];
        var derived = target.IsVirtual && !target.IsFinal
            ? AccessTools.Method(type, target.Name, [typeof(BlockPos), typeof(Block[]), typeof(int)]) ?? target
            : target;
        return Reads(target) && (ReferenceEquals(derived, target) || Reads(derived));
    }

    // Argument 1 (the position) is only loaded for an ldfld; an unreadable body counts as keeping it
    private static bool Reads(MethodInfo method)
    {
        // a body another mod patches may do anything with it
        if (!NotNull(method) || method.IsStatic || method.GetMethodBody() is null ||
            EngineShape.Patched(method))
            return false;
        List<CodeInstruction> code;
        try
        {
            code = PatchProcessor.GetOriginalInstructions(method);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        if (!Assert(code.Count <= MaxBody)) return false;
        for (var i = 0; i < Math.Min(code.Count, MaxBody); i++)
        {
            if (!Position(code[i])) continue;
            if (code[i].opcode != OpCodes.Ldarg_1 || !Index(i + 1, code.Count) || code[i + 1].opcode != OpCodes.Ldfld)
                return false;
        }

        return true;
    }

    // Any instruction naming argument 1: load, address or store (operand an index or ParameterInfo at position 0)
    private static bool Position(CodeInstruction code)
    {
        var op = code.opcode;
        if (op == OpCodes.Ldarg_1) return true;
        return (op == OpCodes.Ldarg || op == OpCodes.Ldarg_S || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S ||
                op == OpCodes.Starg || op == OpCodes.Starg_S) &&
               code.operand is (byte)1 or (short)1 or 1 or ParameterInfo { Position: 0 };
    }
}
