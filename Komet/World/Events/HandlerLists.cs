using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Walk = System.Action<Vintagestory.API.Common.Block, int, int, int>;
using WalkFactory = System.Func<Vintagestory.API.Client.ParticlePhysics, System.Action<Vintagestory.API.Common.Block, int, int, int>>;

namespace Komet.World;

// TriggerOnGetClimate (34k cloud tiles per 40 ms) and TriggerOnGetWindSpeed copy handlers (GetInvocationList,
// 0.7 MB/s); delegates are immutable, so the array (only read, IL-checked) is kept per delegate. Also (2026-10-08):
// - ParticlePhysics.UpdateMotion: one block walk delegate per physics object, not per tick (captures only the object).
// - RoomRegistry.Event_ChunkDirty: the thread's FastSetOfLongs, not a new one per changed block.
internal static class HandlerLists
{
    private const int MaxInstructions = 256, MaxBody = 2048, SlotCount = 16;
    private const int ClimateBit = 1, WindBit = 2, ParticleBit = 4, RoomBit = 8, AllBits = 15;
    private const string Rooms = "Vintagestory.GameContent.RoomRegistry"; // VSEssentials

    private static readonly Entry?[] Slots = new Entry?[SlotCount];
    private static readonly ConditionalWeakTable<ParticlePhysics, Walk> Walks = [];
    private static readonly ConditionalWeakTable<ParticlePhysics, Walk>.CreateValueCallback MakeWalk = static physics => _walk!(physics);
    private static WalkFactory? _walk; // the engine's ldftn and newobj, from the rewrite
    [ThreadStatic] private static FastSetOfLongs? _rooms;
    private static int _rewritten;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits;

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;
        _ = Clear();
        var manager = typeof(EventManager);
        var climate = AccessTools.DeclaredMethod(manager, nameof(EventManager.TriggerOnGetClimate),
            [typeof(ClimateCondition).MakeByRefType(), typeof(BlockPos), typeof(EnumGetClimateMode), typeof(double)]);
        var wind = AccessTools.DeclaredMethod(manager, nameof(EventManager.TriggerOnGetWindSpeed),
            [typeof(Vec3d), typeof(Vec3d).MakeByRefType()]);
        var motion = AccessTools.DeclaredMethod(typeof(ParticlePhysics), nameof(ParticlePhysics.UpdateMotion),
            [typeof(Vec3d), typeof(Vec3f), typeof(float)]);
        var rooms = AccessTools.TypeByName(Rooms) is { } registry ? AccessTools.DeclaredMethod(registry, "Event_ChunkDirty") : null;
        if (!NotNull(harmony) || !NotNull(climate) || !NotNull(wind)) return;
        _ = NotNull(harmony.Patch(climate, transpiler: new HarmonyMethod(Rewrite)));
        _ = NotNull(harmony.Patch(wind, transpiler: new HarmonyMethod(Rewrite)));
        if (NotNull(motion)) _ = NotNull(harmony.Patch(motion, transpiler: new HarmonyMethod(RewriteWalk)));
        if (rooms is not null) _ = NotNull(harmony.Patch(rooms, transpiler: new HarmonyMethod(RewriteRooms)));
    }

    // Leaving the world: no handler stays reachable
    internal static bool Clear()
    {
        Array.Clear(Slots);
        return Assert(Slots.Length == SlotCount);
    }

    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var bit = original?.Name == nameof(EventManager.TriggerOnGetWindSpeed) ? WindBit : ClimateBit;
        _rewritten &= ~bit;
        var (list, of) = (AccessTools.Method(typeof(Delegate), nameof(Delegate.GetInvocationList)),
            AccessTools.Method(typeof(HandlerLists), nameof(Of)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(list) || !NotNull(of))
            return code;
        var site = Il.Single(code, c => c.Calls(list));
        if (!Index(site + 1, code.Count) || !code[site + 1].IsStloc() ||
            !Il.Confined(code, site + 1, (use, operand) => operand == 0 && NotNull(use) &&
                (use.opcode == OpCodes.Ldlen || use.opcode == OpCodes.Ldelem_Ref))) return code;
        if (Il.Substitute(code, site, of)) _rewritten |= bit;
        return code;
    }

    internal static Delegate[] Of(Delegate handlers)
    {
        if (!Enabled) return handlers.GetInvocationList(); // a null delegate throws as the engine's callvirt did
        var slot = (int)((uint)RuntimeHelpers.GetHashCode(handlers) % SlotCount);
        if (Volatile.Read(ref Slots[slot]) is { } kept && ReferenceEquals(kept.Handlers, handlers)) return kept.List;
        var made = handlers.GetInvocationList();
        Volatile.Write(ref Slots[slot], new Entry(handlers, made));
        _ = Assert(made.Length > 0);
        return made;
    }

    // ldarg.0; ldftn <lambda>; newobj Action, the lambda an instance method of ParticlePhysics capturing only `this`
    internal static List<CodeInstruction> RewriteWalk(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~ParticleBit;
        var walker = AccessTools.Method(typeof(HandlerLists), nameof(Walker));
        if (!Assert(Il.Take(instructions, MaxBody, out var code)) || !NotNull(walker)) return code;
        var at = Il.Single(code, static c => c.opcode == OpCodes.Ldftn);
        if (at < 1 || !Index(at + 1, code.Count) || !code[at - 1].IsLdarg(0) ||
            code[at].operand is not MethodInfo { IsStatic: false } lambda || lambda.DeclaringType != typeof(ParticlePhysics) ||
            code[at + 1].opcode != OpCodes.Newobj || code[at + 1].operand is not ConstructorInfo { DeclaringType: var made } ||
            made != typeof(Walk) || code[at].labels.Count + code[at + 1].labels.Count > 0) return code;
        _walk = Factory(lambda);
        (code[at].opcode, code[at].operand) = (OpCodes.Nop, null);
        (code[at + 1].opcode, code[at + 1].operand) = (OpCodes.Call, walker);
        _rewritten |= ParticleBit;
        return code;
    }

    // What the engine's two instructions do: physics => new Walk(physics.<lambda>)
    private static WalkFactory Factory(MethodInfo lambda)
    {
        var ctor = AccessTools.DeclaredConstructor(typeof(Walk), [typeof(object), typeof(IntPtr)]);
        _ = NotNull(lambda) && NotNull(ctor);
        var method = new DynamicMethod("KometParticleWalk", typeof(Walk), [typeof(ParticlePhysics)], typeof(ParticlePhysics), true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldftn, lambda);
        il.Emit(OpCodes.Newobj, ctor!);
        il.Emit(OpCodes.Ret);
        return (WalkFactory)method.CreateDelegate(typeof(WalkFactory));
    }

    internal static Walk Walker(ParticlePhysics physics)
    {
        var make = _walk; // set by the rewrite before the call it puts here can run
        if (!NotNull(make)) throw new InvalidOperationException("particle walk rewritten without its factory");
        return Enabled ? Walks.GetValue(physics, MakeWalk) : make(physics);
    }

    // The one newobj FastSetOfLongs(), its local only added to and enumerated
    internal static List<CodeInstruction> RewriteRooms(IEnumerable<CodeInstruction> instructions)
    {
        _rewritten &= ~RoomBit;
        var set = AccessTools.Method(typeof(HandlerLists), nameof(RoomSet));
        if (!Assert(Il.Take(instructions, MaxBody, out var code)) || !NotNull(set)) return code;
        var made = Il.Single(code, static c => c.opcode == OpCodes.Newobj &&
                                               c.operand is ConstructorInfo { DeclaringType: var t } ctor &&
                                               t == typeof(FastSetOfLongs) && ctor.GetParameters().Length == 0);
        if (made < 0 || !Index(made + 1, code.Count) || !code[made + 1].IsStloc() ||
            !Il.Confined(code, made + 1, static (use, operand) => operand == 0 && use.operand is MethodInfo
                { Name: nameof(FastSetOfLongs.Add) or nameof(FastSetOfLongs.GetEnumerator), DeclaringType: var t } &&
                t == typeof(FastSetOfLongs)))
            return code;
        if (Il.Substitute(code, made, set)) _rewritten |= RoomBit;
        return code;
    }

    internal static FastSetOfLongs RoomSet()
    {
        if (!Enabled) return [];
        var set = _rooms ??= [];
        set.Clear();
        _ = Assert(set.Count == 0);
        return set;
    }

    private sealed record Entry(Delegate Handlers, Delegate[] List);
}
