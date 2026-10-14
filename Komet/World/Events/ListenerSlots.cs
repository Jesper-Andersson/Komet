using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.World;

// Three Initialize registrations search a session-growing list from the start: AddGameTickListenerBlockInternal,
// RegisterRenderer, RegisterEventBusListener. Each part knows where the search ends, else the engine searches:
// - Tick slots: no slot below the mark is empty; RemoveGameTickListener, the only null writer, lowers it.
// - Renderers: a stage proven sorted with fixed RenderOrders takes one above its last at the end.
// - Event bus: a list proven sorted by falling priority takes one at most its last's priority at the end.
// A proof holds for its List<T>._version; the engine's inserts and single removals carry it over.
internal static class ListenerSlots
{
    // EngineShape of TickSeams(), RendererSeams() and BusSeams() in Vintage Story 1.22.7
    internal const ulong TickFingerprint = 0x19C99C1EAAD550FFUL, RendererFingerprint = 0x04CA523EABBC56AAUL,
        BusFingerprint = 0x5AD59681C6AC02EBUL;

    private const int Ticks = 0, Renderers = 1, Bus = 2, Parts = 3;
    private const int MaxListeners = 1 << 24, MaxRenderers = 1 << 16, MaxReservations = 64, MaxStages = 64, MaxTypes = 4096;
    private const int MaxSteps = 64, MaxDepth = 32, MaxMembers = 4096, ConstantGetter = 10, FieldGetter = 6;

    // Per block tick list: no slot below Lowest is empty; Count is the length when Lowest was last raised
    private static readonly ConditionalWeakTable<List<GameTickListenerBlock>, Free> Slots = [];

    // Main thread: per render stage, its list's last proof; per renderer type, whether its RenderOrder is fixed
    private static readonly Order[] Orders = new Order[MaxStages];
    private static readonly Dictionary<Type, bool> Types = [];

    private static readonly bool[] Shaped = new bool[Parts], Foreign = new bool[Parts], Patched = new bool[Parts];
    private static readonly MethodBase?[][] Seams = new MethodBase?[Parts][];

    private static AccessTools.FieldRef<List<GameTickListenerBlock>, int>? _tickVersion;
    private static AccessTools.FieldRef<List<RenderHandler>, int>? _renderVersion;
    private static AccessTools.FieldRef<List<EventBusListener>, int>? _busVersion;
    private static MethodInfo? _orderGetter, _dummyGetter;
    private static ILogger? _logger;
    private static int _epoch; // moved by every write of a DummyRenderer's RenderOrder

    // Main thread: the event bus list last looked at, its version then, and whether it was sorted
    private static List<EventBusListener>? _bus;
    private static int _busSeen;
    private static bool _busProven;

    public static bool Enabled { get; set; } = true;

    // Bodies verified / foreign patch: that part leaves its registrations to the engine
    internal static bool Matched => Array.TrueForAll(Shaped, shaped => shaped);
    internal static bool Blocked => Array.Exists(Foreign, foreign => foreign);

    // Registrations placed without the engine's search, while Counting.Hud

    private sealed class Free
    {
        public int Lowest;
        public int Count;
    }

    private sealed class Order
    {
        public List<RenderHandler>? List;
        public int Version, Epoch, Unfixed;
        public bool Proven;
    }

    public static void Install(Harmony harmony, ILogger? logger)
    {
        _logger = logger;
        Clear();
        Array.Clear(Shaped);
        Array.Clear(Foreign);
        Array.Clear(Patched);
        (Seams[Ticks], Seams[Renderers], Seams[Bus]) = (TickSeams(), RendererSeams(), BusSeams());
        if (!NotNull(harmony) || !Assert(Array.TrueForAll(Seams, seams => seams is { Length: > 0 }))) return;
        Shaped[Ticks] = EngineShape.Matches(Seams[Ticks], TickFingerprint, nameof(ListenerSlots), logger);
        Shaped[Renderers] = EngineShape.Matches(Seams[Renderers], RendererFingerprint, nameof(ListenerSlots), logger);
        Shaped[Bus] = EngineShape.Matches(Seams[Bus], BusFingerprint, nameof(ListenerSlots), logger);
        if (Shaped[Ticks]) Patched[Ticks] = PatchTicks(harmony);
        if (Shaped[Renderers]) Patched[Renderers] = PatchRenderers(harmony);
        if (Shaped[Bus]) Patched[Bus] = PatchBus(harmony);
        Recheck();
    }

    // A foreign prefix, transpiler or infix on what a part answers or reads its proof off stands that part down
    internal static void Recheck()
    {
        var was = Blocked;
        for (var part = 0; part < Parts; part++)
            Foreign[part] = Patched[part] && Seams[part] is { Length: > 0 } seams &&
                            EngineShape.Foreign(seams, PatchKinds.Replacing, null, typeof(ListenerSlots));
        _ = EngineShape.Report(_logger, nameof(ListenerSlots), was, Blocked);
        _ = Assert(Foreign.Length == Seams.Length) && Assert(!Blocked || Array.Exists(Patched, patched => patched));
    }

    // At world leave: the lists go with the world
    public static void Clear()
    {
        Slots.Clear();
        Array.Clear(Orders);
        Types.Clear();
        (_bus, _busSeen, _busProven) = (null, 0, false);
        _ = Assert(Types.Count == 0) && Assert(Orders.Length == MaxStages);
    }

    private static bool On(int part) => Index(part, Parts) && Enabled && Patched[part] && !Foreign[part];

    private static bool Main => Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId;

    private static HarmonyMethod Own(string name)
    {
        var method = AccessTools.DeclaredMethod(typeof(ListenerSlots), name);
        _ = NotNull(method); // Binds found it first
        return new HarmonyMethod(method);
    }

    // Whether Harmony binds every patch's parameters by name, checked before any goes in
    private static bool Binds(MethodBase? target, params ReadOnlySpan<string> patches)
    {
        if (!NotNull(target) || !Assert(patches.Length <= MaxSteps)) return false;
        for (var i = 0; i < Math.Min(patches.Length, MaxSteps); i++)
            if (AccessTools.DeclaredMethod(typeof(ListenerSlots), patches[i]) is not { } patch || !Il.Binds(target, patch))
                return false;
        return true;
    }

    private static AccessTools.FieldRef<List<T>, int>? Versioned<T>() =>
        AccessTools.DeclaredField(typeof(List<T>), "_version")?.FieldType == typeof(int)
            ? AccessTools.FieldRefAccess<List<T>, int>("_version")
            : null;

    internal static MethodBase?[] TickSeams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(EventManager), "AddGameTickListenerBlockInternal",
                [typeof(GameTickListenerBlock), typeof(long)]),
            AccessTools.DeclaredMethod(typeof(EventManager), nameof(EventManager.RemoveGameTickListener), [typeof(long)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    private static bool PatchTicks(Harmony harmony)
    {
        _tickVersion = Versioned<GameTickListenerBlock>();
        if (!NotNull(harmony) || Seams[Ticks] is not [{ } add, { } remove] || !NotNull(_tickVersion) || !Binds(add, nameof(AddTick)) ||
            !Binds(remove, nameof(RemovingTick), nameof(RemovedTick))) return false;
        _ = NotNull(harmony.Patch(add, Own(nameof(AddTick))));
        _ = NotNull(harmony.Patch(remove, Own(nameof(RemovingTick)), Own(nameof(RemovedTick))));
        return true;
    }

    // Prefix on AddGameTickListenerBlockInternal: the first-empty search, starting at the mark
    private static bool AddTick(List<GameTickListenerBlock> ___GameTickListenersBlock,
        ConcurrentDictionary<long, int> ___GameTickListenersBlockIndices, GameTickListenerBlock listener, long newListenerId,
        ref long __result)
    {
        var (list, indices) = (___GameTickListenersBlock, ___GameTickListenersBlockIndices);
        if (!On(Ticks) || list is null || indices is null || list.Count >= MaxListeners) return true;
        var free = Slots.GetValue(list, static _ => new Free());
        var read = Volatile.Read(ref free.Lowest);
        var from = list.Count < free.Count || read > list.Count ? 0 : read; // shrunk by another hand: from the start
        var slot = list.Count;
        for (var i = from; i < Math.Min(list.Count, MaxListeners); i++)
        {
            if (list[i] is not null) continue;
            slot = i;
            break;
        }

        if (slot < list.Count) list[slot] = listener;
        else list.Add(listener);
        indices[newListenerId] = slot;
        _ = Interlocked.CompareExchange(ref free.Lowest, slot + 1, read); // a removal lowered it meanwhile: that one stands
        free.Count = list.Count;
        __result = newListenerId;
        _ = Assert(slot < list.Count) && Assert(slot >= from);
        return false;
    }

    // Prefix on RemoveGameTickListener: the block slot it empties (0 when scanning, -1 for an entity slot), version
    private static void RemovingTick(List<GameTickListener> ___GameTickListenersEntity,
        ConcurrentDictionary<long, int> ___GameTickListenersEntityIndices, List<GameTickListenerBlock> ___GameTickListenersBlock,
        ConcurrentDictionary<long, int> ___GameTickListenersBlockIndices, long listenerId, out (int Slot, int Version) __state)
    {
        __state = (-1, 0);
        var (entities, blocks) = (___GameTickListenersEntity, ___GameTickListenersBlock);
        if (listenerId == 0 || _tickVersion is not { } version || entities is null || blocks is null ||
            ___GameTickListenersEntityIndices is not { } byEntity || ___GameTickListenersBlockIndices is not { } byBlock) return;
        int slot;
        if (byEntity.TryGetValue(listenerId, out var e))
            slot = e >= 0 && e < entities.Count && entities[e] is { } entity && entity.ListenerId == listenerId ? -1 : 0;
        else
            slot = byBlock.TryGetValue(listenerId, out var b) && b >= 0 && b < blocks.Count && blocks[b] is { } block &&
                   block.ListenerId == listenerId
                ? b
                : 0;
        __state = (slot, version(blocks));
        _ = Assert(slot < Math.Max(blocks.Count, 1));
    }

    // Postfix: when the list changed, the slot emptied lowers the mark
    private static void RemovedTick(List<GameTickListenerBlock> ___GameTickListenersBlock, (int Slot, int Version) __state)
    {
        var list = ___GameTickListenersBlock;
        if (__state.Slot < 0 || _tickVersion is not { } version || list is null || version(list) == __state.Version ||
            !Slots.TryGetValue(list, out var free)) return;
        for (var i = 0; i < MaxSteps; i++)
        {
            var lowest = Volatile.Read(ref free.Lowest);
            if (lowest <= __state.Slot || Interlocked.CompareExchange(ref free.Lowest, __state.Slot, lowest) == lowest) return;
        }

        Volatile.Write(ref free.Lowest, 0); // contended past MaxSteps: the start is never wrong
        _ = Assert(__state.Slot < Math.Max(list.Count, 1));
    }

    internal static MethodBase?[] RendererSeams()
    {
        var (manager, dummy) = (typeof(ClientEventManager), typeof(DummyRenderer));
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(manager, nameof(ClientEventManager.RegisterRenderer),
                [typeof(IRenderer), typeof(EnumRenderStage), typeof(string)]),
            AccessTools.DeclaredMethod(manager, nameof(ClientEventManager.UnregisterRenderer),
                [typeof(IRenderer), typeof(EnumRenderStage)]),
            AccessTools.DeclaredPropertyGetter(dummy, nameof(DummyRenderer.RenderOrder)),
            AccessTools.DeclaredPropertySetter(dummy, nameof(DummyRenderer.RenderOrder)),
            AccessTools.DeclaredMethod(typeof(RenderOrderReservation), nameof(RenderOrderReservation.Conflicts),
                [typeof(IRenderer), typeof(double)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    private static bool PatchRenderers(Harmony harmony)
    {
        (_renderVersion, _orderGetter) = (Versioned<RenderHandler>(),
            AccessTools.DeclaredPropertyGetter(typeof(IRenderer), nameof(IRenderer.RenderOrder)));
        if (!NotNull(harmony) || Seams[Renderers] is not [{ } register, { } unregister, MethodInfo getter, { } setter, _] ||
            !NotNull(_renderVersion) || !NotNull(_orderGetter) || !Binds(register, nameof(Rendering), nameof(Rendered)) ||
            !Binds(unregister, nameof(Unrendering), nameof(Unrendered))) return false;
        if (!Binds(setter, nameof(Moved))) return false;
        _dummyGetter = getter;
        _ = NotNull(harmony.Patch(setter, postfix: Own(nameof(Moved))));
        _ = NotNull(harmony.Patch(register, Own(nameof(Rendering)), Own(nameof(Rendered))));
        _ = NotNull(harmony.Patch(unregister, Own(nameof(Unrendering)), Own(nameof(Unrendered))));
        return true;
    }

    // Postfix on DummyRenderer's RenderOrder setter: every stage is proven again
    private static void Moved(DummyRenderer __instance)
    {
        _ = NotNull(__instance);
        _ = Interlocked.Increment(ref _epoch);
    }

    private static List<RenderHandler>? StageList(ClientEventManager? manager, EnumRenderStage stage)
    {
        var at = (int)stage;
        if (manager?.renderersByStage is not { } lists || at < 0 || at >= Math.Min(lists.Length, MaxStages)) return null;
        return Assert(lists.Length <= MaxStages) ? lists[at] : null;
    }

    // The stage's proof, renewed when its list changed under another hand or a DummyRenderer moved
    private static Order? Proof(List<RenderHandler> list, EnumRenderStage stage)
    {
        if (_renderVersion is not { } version || !Index((int)stage, Orders.Length)) return null;
        var order = Orders[(int)stage] ??= new Order();
        var epoch = Volatile.Read(ref _epoch);
        if (ReferenceEquals(order.List, list) && order.Version == version(list) && order.Epoch == epoch) return order;
        var (sorted, unfixed, previous) = (list.Count <= MaxRenderers, 0, double.NegativeInfinity);
        for (var i = 0; i < Math.Min(list.Count, MaxRenderers); i++)
        {
            if (list[i]?.Renderer is not { } renderer || !Fixed(renderer))
            {
                unfixed++;
                continue;
            }

            var value = renderer.RenderOrder;
            sorted &= value >= previous; // a NaN never is
            previous = value;
        }

        (order.List, order.Version, order.Epoch, order.Proven, order.Unfixed) =
            (list, version(list), epoch, sorted && unfixed == 0, unfixed);
        _ = Assert(unfixed <= list.Count);
        return order;
    }

    // Prefix on RegisterRenderer: a fixed renderer above a proven stage goes on the end unless a reservation refuses
    private static bool Rendering(ClientEventManager __instance, IRenderer handler, EnumRenderStage stage, string profilingName,
        out (Order? Order, int Version, int Count, double Value, bool Fixed) __state)
    {
        __state = (null, 0, 0, double.NaN, false);
        if (!On(Renderers) || handler is null || _renderVersion is not { } version || !Main ||
            StageList(__instance, stage) is not { } list || Proof(list, stage) is not { } order) return true;
        var isFixed = Fixed(handler);
        var value = isFixed ? handler.RenderOrder : double.NaN; // only a fixed order is read here: it reads the same again
        __state = (order, version(list), list.Count, value, isFixed);
        if (!isFixed || !order.Proven || (list.Count > 0 && !(value > list[^1].Renderer.RenderOrder)) ||
            Conflicts(__instance.renderOrderReservationsByStage, stage, handler, value)) return true;
        list.Add(new RenderHandler { Renderer = handler, ProfilingName = stage.ToString() + "-" + profilingName });
        (order.Version, __state.Order) = (version(list), null);
        _ = Assert(list.Count > 0) && Assert(ReferenceEquals(list[^1].Renderer, handler));
        return false;
    }

    // Whether the engine would throw for a reservation, or one cannot be read; no side effects
    private static bool Conflicts(List<RenderOrderReservation>[]? reservations, EnumRenderStage stage, IRenderer handler,
        double value)
    {
        var at = (int)stage;
        if (reservations is null || at < 0 || at >= reservations.Length || reservations[at] is not { } held ||
            held.Count > MaxReservations) return true;
        var all = held.Bounded(MaxReservations);
        for (var i = 0; i < Math.Min(all.Length, MaxReservations); i++)
            if (all[i] is null || all[i].Conflicts(handler, value))
                return true;
        return !Assert(all.Length == held.Count);
    }

    // Postfix: one renderer inserted. A fixed one keeps the proof unless NaN (goes first); an unfixed one is counted.
    private static void Rendered(ClientEventManager __instance, EnumRenderStage stage,
        (Order? Order, int Version, int Count, double Value, bool Fixed) __state)
    {
        if (__state.Order is not { } order || _renderVersion is not { } version || StageList(__instance, stage) is not { } list ||
            !ReferenceEquals(order.List, list) || order.Version != __state.Version ||
            version(list) != __state.Version + 1 || list.Count != __state.Count + 1) return;
        order.Version = version(list);
        if (__state.Fixed) order.Proven &= !double.IsNaN(__state.Value);
        else (order.Unfixed, order.Proven) = (order.Unfixed + 1, false);
        _ = Assert(order.Unfixed >= 0) && Assert(order.Unfixed <= list.Count);
    }

    // Unregistering keeps a stage's proof; the last unfixed one out has it proven again
    private static void Unrendering(ClientEventManager __instance, EnumRenderStage stage,
        out (bool Seen, int Version, int Count) __state)
    {
        __state = (false, 0, 0);
        if (_renderVersion is not { } version || !Main || StageList(__instance, stage) is not { } list) return;
        __state = (true, version(list), list.Count);
        _ = Assert(list.Count <= MaxRenderers);
    }

    private static void Unrendered(ClientEventManager __instance, IRenderer handler, EnumRenderStage stage,
        (bool Seen, int Version, int Count) __state)
    {
        if (!__state.Seen || _renderVersion is not { } version || StageList(__instance, stage) is not { } list ||
            !Index((int)stage, Orders.Length) || Orders[(int)stage] is not { } order || !ReferenceEquals(order.List, list) ||
            order.Version != __state.Version || version(list) != __state.Version + 1 || list.Count != __state.Count - 1) return;
        order.Version = version(list);
        if ((handler is null || !Fixed(handler)) && --order.Unfixed <= 0) order.List = null;
        _ = Assert(order.Unfixed >= 0 || order.List is null);
    }

    // RenderOrder is fixed: a constant or static readonly getter, or DummyRenderer's (Moved watches it)
    private static bool Fixed(IRenderer renderer)
    {
        if (!NotNull(renderer)) return false;
        var type = renderer.GetType();
        if (Types.TryGetValue(type, out var known)) return known;
        var getter = Implementation(type);
        var result = getter is not null &&
                     ((_dummyGetter is { } dummy && getter.MethodHandle == dummy.MethodHandle) || Returns(getter));
        if (Assert(Types.Count < MaxTypes)) Types[type] = result;
        return result;
    }

    // The RenderOrder getter the type dispatches to, for a virtual one its final override
    private static MethodInfo? Implementation(Type type)
    {
        if (_orderGetter is not { } wanted || !NotNull(type)) return null;
        InterfaceMapping map;
        try
        {
            map = type.GetInterfaceMap(typeof(IRenderer));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }

        if (!Assert(map.InterfaceMethods.Length == map.TargetMethods.Length)) return null;
        for (var i = 0; i < Math.Min(map.InterfaceMethods.Length, MaxMembers); i++)
        {
            if (map.InterfaceMethods[i].MethodHandle != wanted.MethodHandle) continue;
            var target = map.TargetMethods[i];
            return target.IsVirtual && !target.IsFinal ? Override(type, target.GetBaseDefinition()) : target;
        }

        return null;
    }

    private static MethodInfo? Override(Type type, MethodInfo root)
    {
        var at = type;
        for (var depth = 0; depth < MaxDepth && at is not null; depth++)
        {
            var declared = AccessTools.GetDeclaredMethods(at);
            if (!Assert(declared.Count <= MaxMembers)) return null;
            foreach (var method in declared.Bounded(MaxMembers))
                if (method.GetBaseDefinition().MethodHandle == root.MethodHandle)
                    return method;
            at = at.BaseType;
        }

        return null;
    }

    // ldc.r8 <constant>; ret, or ldsfld <static readonly field>; ret
    private static bool Returns(MethodInfo getter)
    {
        if (!NotNull(getter)) return false;
        try
        {
            var il = getter.GetMethodBody()?.GetILAsByteArray();
            if (il is not { Length: > 0 } || il[^1] != (byte)OpCodes.Ret.Value) return false;
            if (il.Length == ConstantGetter && il[0] == (byte)OpCodes.Ldc_R8.Value) return true;
            return il.Length == FieldGetter && il[0] == (byte)OpCodes.Ldsfld.Value &&
                   getter.Module.ResolveField(BitConverter.ToInt32(il, 1), getter.DeclaringType?.GetGenericArguments(), null)
                       is { IsStatic: true, IsInitOnly: true };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or BadImageFormatException
                                      or MissingMemberException or TypeLoadException)
        {
            return false;
        }
    }

    internal static MethodBase?[] BusSeams()
    {
        var api = typeof(ClientEventAPI);
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.RegisterEventBusListener),
                [typeof(EventBusListenerDelegate), typeof(double), typeof(string)]),
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.UnregisterEventBusListener), [typeof(EventBusListenerDelegate)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    private static bool PatchBus(Harmony harmony)
    {
        _busVersion = Versioned<EventBusListener>();
        if (!NotNull(harmony) || Seams[Bus] is not [{ } register, { } unregister] || !NotNull(_busVersion) ||
            !Binds(register, nameof(Listening), nameof(Listened)) || !Binds(unregister, nameof(Unlistening), nameof(Unlistened)))
            return false;
        _ = NotNull(harmony.Patch(register, Own(nameof(Listening)), Own(nameof(Listened))));
        _ = NotNull(harmony.Patch(unregister, Own(nameof(Unlistening)), Own(nameof(Unlistened))));
        return true;
    }

    // Reproves the list sorted by falling priority when it changed under another hand
    private static void Look(List<EventBusListener> list, AccessTools.FieldRef<List<EventBusListener>, int> version)
    {
        if (!NotNull(list) || !NotNull(version) || (ReferenceEquals(list, _bus) && _busSeen == version(list))) return;
        var (sorted, previous) = (list.Count <= MaxListeners, double.PositiveInfinity);
        for (var i = 0; i < Math.Min(list.Count, MaxListeners) && sorted; i++)
        {
            sorted = list[i] is { } listener && previous >= listener.priority; // a NaN never is
            previous = list[i]?.priority ?? double.NaN;
        }

        (_bus, _busSeen, _busProven) = (list, version(list), sorted);
        _ = Assert(ReferenceEquals(_bus, list));
    }

    // Prefix on RegisterEventBusListener: on a proven list, a priority at most the last one's goes on the end
    private static bool Listening(ClientMain ___game, EventBusListenerDelegate OnEvent, double priority, string filterByEventName,
        out (bool Seen, int Version, int Count) __state)
    {
        __state = (false, 0, 0);
        if (!On(Bus) || _busVersion is not { } version || ___game?.eventManager?.EventBusListeners is not { } list || !Main)
            return true;
        Look(list, version);
        __state = (true, version(list), list.Count);
        if (!_busProven || (list.Count > 0 && !(list[^1].priority >= priority))) return true;
        list.Add(new EventBusListener { handler = OnEvent, priority = priority, filterByName = filterByEventName });
        (_busSeen, __state) = (version(list), (false, 0, 0));
        _ = Assert(list.Count > 0) && Assert(ReferenceEquals(list[^1].handler, OnEvent));
        return false;
    }

    // Postfix: one listener inserted; the proof holds unless the priority is NaN (goes first)
    private static void Listened(ClientMain ___game, double priority, (bool Seen, int Version, int Count) __state)
    {
        if (!__state.Seen || _busVersion is not { } version || ___game?.eventManager?.EventBusListeners is not { } list ||
            !ReferenceEquals(list, _bus) || _busSeen != __state.Version || version(list) != __state.Version + 1 ||
            list.Count != __state.Count + 1) return;
        (_busSeen, _busProven) = (version(list), _busProven && !double.IsNaN(priority));
        _ = Assert(list.Count > __state.Count);
    }

    // Removal keeps the proof; from an unproven list it reproves
    private static void Unlistening(ClientMain ___game, out (bool Seen, int Version, int Count) __state)
    {
        __state = (false, 0, 0);
        if (_busVersion is not { } version || !Main || ___game?.eventManager?.EventBusListeners is not { } list) return;
        __state = (true, version(list), list.Count);
        _ = Assert(list.Count <= MaxListeners);
    }

    private static void Unlistened(ClientMain ___game, (bool Seen, int Version, int Count) __state)
    {
        if (!__state.Seen || _busVersion is not { } version || ___game?.eventManager?.EventBusListeners is not { } list ||
            !ReferenceEquals(list, _bus) || _busSeen != __state.Version || version(list) != __state.Version + 1 ||
            list.Count != __state.Count - 1) return;
        if (_busProven) _busSeen = version(list);
        else _bus = null;
        _ = Assert(list.Count < __state.Count);
    }
}
