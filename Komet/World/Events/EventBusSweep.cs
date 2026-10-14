using System.Reflection;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.World;

// Client display block entities (BlockEntityDisplay, racks, firepit, BEBehaviorDisplay) never unregister their event
// bus listener, so EventBusListeners keeps all of the session; each insert walks the list (0.2 ms at 100k) and every
// PushEvent calls all. After the frame's tasks one pass drops listeners of block entities whose base OnBlockUnloaded or
// OnBlockRemoved ran (never initialised again; their handlers only redid the live one's work). It runs only in frames
// where a registering one died.
internal static class EventBusSweep
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x444714F8DA4CFC84UL;
    private const int MaxDead = 1 << 16, MaxListening = 1 << 20;

    // Main thread: block entities that registered a listener, and those gone since the last pass
    private static readonly HashSet<BlockEntity> Listening = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<BlockEntity> Dead = new(ReferenceEqualityComparer.Instance);
    private static readonly Predicate<EventBusListener> Gone = static listener => listener?.handler?.Target switch
    {
        BlockEntity entity => Dead.Contains(entity),
        BlockEntityBehavior { Blockentity: { } entity } => Dead.Contains(entity),
        _ => false
    };

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    public static long Swept { get; private set; }
    public static int Listeners { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (Installed, Swept, Listeners) = (false, 0, 0);
        Clear();
        var seams = Seams();
        if (!NotNull(harmony) || !Assert(seams.Length == 3) || !Assert(!Array.Exists(seams, s => s is null)) ||
            !EngineShape.Matches(seams, fingerprint, nameof(EventBusSweep), logger)) return;
        var type = typeof(BlockEntity);
        MethodBase?[] targets =
        [
            AccessTools.DeclaredMethod(type, nameof(BlockEntity.OnBlockUnloaded)),
            AccessTools.DeclaredMethod(type, nameof(BlockEntity.OnBlockRemoved)),
            AccessTools.DeclaredMethod(type, nameof(BlockEntity.Initialize)),
            AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.ExecuteMainThreadTasks))
        ];
        if (!Assert(Array.TrueForAll(targets, t => t is not null)) ||
            !Assert(AccessTools.DeclaredField(typeof(ClientEventManager), nameof(ClientEventManager.EventBusListeners))
                ?.FieldType == typeof(List<EventBusListener>))) return;
        _ = NotNull(harmony.Patch(targets[0], postfix: new HarmonyMethod(Died)));
        _ = NotNull(harmony.Patch(targets[1], postfix: new HarmonyMethod(Died)));
        _ = NotNull(harmony.Patch(targets[2], new HarmonyMethod(Revived)));
        _ = NotNull(harmony.Patch(targets[3], postfix: new HarmonyMethod(Sweep)));
        _ = NotNull(harmony.Patch(seams[0], postfix: new HarmonyMethod(Registered)));
        Installed = true;
    }

    internal static MethodBase?[] Seams()
    {
        var api = typeof(ClientEventAPI);
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.RegisterEventBusListener)),
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.UnregisterEventBusListener)),
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.PushEvent))
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    public static void Clear()
    {
        Dead.Clear();
        Listening.Clear();
        _ = Assert(Dead.Count == 0) && Assert(Listening.Count == 0);
    }

    // Postfix on RegisterEventBusListener; Harmony binds the parameter by name
    internal static void Registered(EventBusListenerDelegate OnEvent)
    {
        var owner = OnEvent?.Target switch
        {
            BlockEntity entity => entity,
            BlockEntityBehavior behavior => behavior.Blockentity,
            _ => null
        };
        if (owner is not null && Assert(Listening.Count < MaxListening) &&
            Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) _ = Listening.Add(owner);
    }

    // Postfix on the base bodies every proper override calls; the server's (singleplayer) are not the client's
    internal static void Died(BlockEntity __instance)
    {
        if (!Enabled || !Installed || __instance?.Api is not ICoreClientAPI ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;
        if (Listening.Remove(__instance) && Assert(Dead.Count < MaxDead)) _ = Dead.Add(__instance);
    }

    internal static void Revived(BlockEntity __instance)
    {
        if (Dead.Count > 0 && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) _ = Dead.Remove(__instance);
        _ = Assert(Dead.Count <= MaxDead);
    }

    internal static void Sweep(ClientMain __instance)
    {
        if (Dead.Count == 0) return;
        if (Enabled && __instance?.eventManager?.EventBusListeners is { } listeners &&
            Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId))
        {
            var removed = listeners.RemoveAll(Gone);
            if (Counting.Hud) (Swept, Listeners) = (Swept + removed, listeners.Count);
        }

        _ = Assert(Dead.Count <= MaxDead);
        Dead.Clear();
    }
}
