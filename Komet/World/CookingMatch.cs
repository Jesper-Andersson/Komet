using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;

namespace Komet.World;

// A firepit's cooking pot tests every recipe twice a burn tick (CookingRecipe.Matches), allocating copies, counters and
// per GetMatchingStack a fresh IgnoredStackAttributes + "timeFrozen": 5.9 MB/s in the bench world, a tenth of all
// garbage.
// - Matches uses the thread's two lists and counter array, never lets them out and reads only the counters it zeroed. A
//   nested Matches (none exists) allocates as the engine; a prefix and finalizer count depth.
// - GetMatchingStack compares against one shared copy, rebuilt when the source changes; Equals only reads it
//   (EngineShape pins the body).
internal static class CookingMatch
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x473338848C356079UL;

    private const string RecipeType = "Vintagestory.GameContent.CookingRecipe";
    private const string IngredientType = "Vintagestory.GameContent.CookingRecipeIngredient";
    private const string Frozen = "timeFrozen";
    private const int MaxInstructions = 1024, MaxCounts = 4096, Arrays = 2, MatchesBit = 1, StackBit = 2, AllBits = 3;

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static List<ItemStack>? _stacks;
    [ThreadStatic] private static object? _items; // the List<CookingRecipeIngredient> of this thread
    [ThreadStatic] private static int[]? _counts;
    private static Shared? _ignored;
    private static int _rewritten;
    private static bool _shaped;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits;
    internal static bool Matched => _shaped;

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_rewritten, _shaped) = (0, false);
        var seams = Seams();
        if (!NotNull(harmony) || !Assert(seams.Length == 2) || seams[0] is not MethodInfo matches ||
            seams[1] is not MethodInfo matching) return;
        _shaped = EngineShape.Matches(seams, fingerprint, nameof(CookingMatch), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(matches, new HarmonyMethod(Enter), transpiler: new HarmonyMethod(RewriteMatches),
            finalizer: new HarmonyMethod(Leave)));
        _ = NotNull(harmony.Patch(matching, transpiler: new HarmonyMethod(RewriteMatching)));
    }

    internal static MethodBase?[] Seams()
    {
        var (recipe, ingredient) = (AccessTools.TypeByName(RecipeType), AccessTools.TypeByName(IngredientType));
        MethodBase?[] seams =
        [
            recipe is null
                ? null
                : AccessTools.DeclaredMethod(recipe, "Matches", [typeof(ItemStack[]), typeof(int).MakeByRefType()]),
            ingredient is null ? null : AccessTools.DeclaredMethod(ingredient, "GetMatchingStack", [typeof(ItemStack)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    private static void Enter()
    {
        _depth++;
        _ = Assert(_depth > 0);
    }

    // The main thread outlives the world the stacks and ingredients belong to
    private static void Leave()
    {
        if (--_depth == 0)
        {
            _stacks?.Clear();
            (_items as System.Collections.IList)?.Clear();
        }

        _ = Assert(_depth >= 0);
    }

    internal static List<CodeInstruction> RewriteMatches(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~MatchesBit;
        var ingredient = AccessTools.TypeByName(IngredientType);
        var (copy, toList) = (AccessTools.Constructor(typeof(List<ItemStack>), [typeof(IEnumerable<ItemStack>)]),
            ingredient is null
                ? null
                : AccessTools.Method(typeof(Enumerable), nameof(Enumerable.ToList))?.MakeGenericMethod(ingredient));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(ingredient) || !NotNull(copy) ||
            !NotNull(toList)) return code;
        var (stacks, items) = (Il.Single(code, c => c.Is(OpCodes.Newobj, copy)), Il.Single(code, c => c.Calls(toList)));
        var counts = Il.Single(code, c => c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof(int)));
        if (stacks < 0 || items < 0 || counts < 0) return code;
        var item = AccessTools.Method(typeof(CookingMatch), nameof(Items)).MakeGenericMethod(ingredient);
        var (stack, count) = (AccessTools.Method(typeof(CookingMatch), nameof(Stacks)),
            AccessTools.Method(typeof(CookingMatch), nameof(Counts)));
        (code[stacks].opcode, code[stacks].operand) = (OpCodes.Call, stack);
        (code[items].opcode, code[items].operand) = (OpCodes.Call, item);
        (code[counts].opcode, code[counts].operand) = (OpCodes.Call, count);
        _rewritten |= MatchesBit;
        return code;
    }

    internal static List<ItemStack> Stacks(IEnumerable<ItemStack> stacks)
    {
        if (!Enabled || _depth != 1 || !NotNull(stacks)) return [.. stacks];
        var list = _stacks ??= [];
        list.Clear();
        list.AddRange(stacks);
        return list;
    }

    internal static List<T> Items<T>(IEnumerable<T> items)
    {
        if (!Enabled || _depth != 1 || !NotNull(items)) return [.. items];
        if (_items is not List<T> list) _items = list = [];
        list.Clear();
        list.AddRange(items);
        return list;
    }

    // Stands in for new int[count]: at least count long, zeroed by the method itself
    internal static int[] Counts(int count)
    {
        // a negative count throws as newarr does
        if (!Enabled || _depth != 1 || !Index(count, MaxCounts + 1)) return new int[count];
        if (_counts is not { } counts || counts.Length < count) _counts = counts = new int[Math.Max(count, 16)];
        return counts;
    }

    // Both IgnoredStackAttributes + "timeFrozen" copies removed, Equals reads Ignored(); any other shape keeps the IL
    internal static List<CodeInstruction> RewriteMatching(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~StackBit;
        var source = AccessTools.Field(typeof(GlobalConstants), nameof(GlobalConstants.IgnoredStackAttributes));
        var equals = AccessTools.Method(typeof(ItemStack), nameof(ItemStack.Equals),
            [typeof(IWorldAccessor), typeof(ItemStack), typeof(string[])]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(source) || !NotNull(equals))
            return code;
        var starts = Enumerable.Range(0, code.Count).Where(i => code[i].LoadsField(source)).ToArray();
        if (!Assert(starts.Length == Arrays)) return code;
        var ends = starts.Select(s => Copy(code, s, equals)).ToArray();
        if (ends.Any(e => e < 0)) return code;
        var shared = AccessTools.Method(typeof(CookingMatch), nameof(Ignored));
        for (var k = 0; k < Arrays; k++)
        {
            for (var i = starts[k]; i < Math.Min(ends[k] + 1, MaxInstructions); i++)
                (code[i].opcode, code[i].operand) = (OpCodes.Nop, null);
            (code[ends[k] + 1].opcode, code[ends[k] + 1].operand) = (OpCodes.Call, shared);
        }

        _rewritten |= StackBit;
        return code;
    }

    // The "timeFrozen" store ending a jump-free copy from `start`, with the copy loaded for Equals right after; else -1
    private static int Copy(List<CodeInstruction> code, int start, MethodInfo equals)
    {
        var (arrays, end) = (0, -1);
        for (var i = start; i < Math.Min(code.Count - 2, MaxInstructions); i++)
        {
            var c = code[i];
            if ((i > start && c.labels.Count > 0) || c.blocks.Count > 0 || c.Branches(out _)) return -1;
            if (c.opcode == OpCodes.Newarr && ReferenceEquals(c.operand, typeof(string))) arrays++;
            if (!c.Is(OpCodes.Ldstr, Frozen)) continue;
            end = code[i + 1].opcode == OpCodes.Stelem_Ref ? i + 1 : -1;
            break;
        }

        return end >= 0 && Index(end + 2, code.Count) && arrays == 1 && code[end + 1].IsLdloc() &&
               code[end + 2].Calls(equals) && code[end + 1].labels.Count == 0 ? end : -1;
    }

    // IgnoredStackAttributes + "timeFrozen", shared while the source array and its entries are unchanged
    internal static string[] Ignored()
    {
        var source = GlobalConstants.IgnoredStackAttributes; // null throws in Same as the engine's ldlen does
        if (_ignored is { } shared && Same(shared, source)) return shared.Joined;
        var copy = new string[source.Length + 1];
        source.AsSpan().CopyTo(copy);
        copy[^1] = Frozen;
        _ignored = new Shared(source, copy, [.. source]);
        _ = Assert(copy.Length == source.Length + 1);
        return copy;
    }

    private static bool Same(Shared shared, string[] source)
    {
        var (entries, copy) = (shared.Entries, shared.Joined);
        if (!NotNull(source) || !ReferenceEquals(shared.Source, source) || source.Length != entries.Length ||
            copy.Length != entries.Length + 1 || !ReferenceEquals(copy[^1], Frozen)) return false;
        for (var i = 0; i < Math.Min(entries.Length, MaxCounts); i++)
            if (!ReferenceEquals(source[i], entries[i]) || !ReferenceEquals(copy[i], entries[i])) return false;
        return true;
    }

    private sealed record Shared(string[] Source, string[] Joined, string[] Entries);
}
