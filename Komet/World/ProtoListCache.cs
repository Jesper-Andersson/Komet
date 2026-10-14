using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;

namespace Komet.World;

// protobuf-net asks reflection per collection, per chunk, for its item type (GetListItemType) and add method
// (ResolveListAdd); both depend only on model and type, so each is kept.
internal static class ProtoListCache
{
    private static readonly ConcurrentDictionary<(object, Type), Type?> Items = new();
    private static readonly ConcurrentDictionary<(object, Type, Type), (MethodInfo? Add, bool IsList)> Adds = new();

    private const int MaxTypes = 1 << 14; // collection types a game deserializes: a few hundred

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    public static void Install(Harmony harmony)
    {
        var model = AccessTools.TypeByName("ProtoBuf.Meta.TypeModel");
        var (items, adds) = (model is null ? null : AccessTools.Method(model, "GetListItemType"),
            model is null ? null : AccessTools.Method(model, "ResolveListAdd"));
        if (!NotNull(harmony) || items is null || adds is null || !Assert(items.IsStatic && adds.IsStatic)) return;
        _ = NotNull(harmony.Patch(items, new HarmonyMethod(ItemBefore), new HarmonyMethod(ItemAfter)));
        _ = NotNull(harmony.Patch(adds, new HarmonyMethod(AddBefore), new HarmonyMethod(AddAfter)));
        Patched = true;
    }

    private static bool ItemBefore(object model, Type listType, ref Type? __result) =>
        !Enabled || model is null || listType is null || !Items.TryGetValue((model, listType), out __result);

    private static void ItemAfter(object model, Type listType, Type? __result, bool __runOriginal)
    {
        if (__runOriginal && Enabled && model is not null && listType is not null && Assert(Items.Count < MaxTypes))
            Items[(model, listType)] = __result;
    }

    private static bool AddBefore(object model, Type listType, Type itemType, ref bool isList, ref MethodInfo? __result)
    {
        if (!Enabled || model is null || listType is null || itemType is null ||
            !Adds.TryGetValue((model, listType, itemType), out var known)) return true;
        (__result, isList) = known;
        return false;
    }

    private static void AddAfter(object model, Type listType, Type itemType, bool isList, MethodInfo? __result,
        bool __runOriginal)
    {
        if (__runOriginal && Enabled && model is not null && listType is not null && itemType is not null &&
            Assert(Adds.Count < MaxTypes)) Adds[(model, listType, itemType)] = (__result, isList);
    }
}
