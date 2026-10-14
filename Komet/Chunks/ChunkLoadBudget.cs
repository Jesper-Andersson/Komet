using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// Each packet is a main-thread task in arrival order ("loadchunk" inserts a chunk and inits its block entities), and
// ExecuteMainThreadTasks runs them all each frame: a chunk burst (and its GC) lands in one frame. After Millis the
// queue stops before the next task that may wait (far chunk load past NearChunks, or unload); the rest stays queued in
// order, so no packet overtakes another. The first task always runs; a near load further back is run through to.
// Backlog past BoostAbove (join, teleport) boosts to BoostMillis. Off until the player spawned.
internal static class ChunkLoadBudget
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x222221D9946996AEUL;
    public const int Engine = 0, MaxMillis = 20, DefaultMillis = 3;
    private const int BoostMillis = 8, BoostAbove = 256, NearChunks = 3, MaxScan = 1024, ChunkShift = 5;
    private const int DimensionHeight = 1024, UnloadPacket = 11; // dim 1 chunk Y; Packet_ServerIdEnum.UnloadServerChunk

    private static MethodBase?[] _seams = [];
    private static Type? _closure; // LoadChunkFromPacket's closure, the target of every "loadchunk" task
    private static AccessTools.FieldRef<object, Packet_ServerChunk>? _packet;
    private static long _start;
    private static int _forced; // tasks to run whatever the clock says: up to a near chunk load
    private static bool _first; // the frame's first look at the queue is still to come

    public static int Millis { get; set; } = DefaultMillis;
    public static bool Rewritten { get; private set; }

    // Loop gated with a budget: far chunks' block entities may spread over frames (BlockEntityBudget)
    internal static bool Budgeting => Rewritten && Millis != Engine;

    // Totals while Counting.Hud: frames the budget ended the queue, and the tasks it left for later
    public static long Stops { get; private set; }
    public static long Held { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "reversedQueue")]
    private static extern ref Queue<ClientTask> Pending(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packet")]
    private static extern ref Packet_Server Packet(ProcessPacketTask task);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (Rewritten, _seams, _closure, _packet, _start, _forced, _first, Stops, Held) =
            (false, Seams(), null, null, 0, 0, false, 0, 0);
        if (!NotNull(harmony) || !Assert(_seams.Length == 3) || !Assert(!Array.Exists(_seams, s => s is null))) return;
        if (!EngineShape.Matches(_seams, fingerprint, nameof(ChunkLoadBudget), logger)) return;
        _closure = _seams[2]!.DeclaringType;
        if (_closure is null || AccessTools.DeclaredField(_closure, "p")?.FieldType != typeof(Packet_ServerChunk) ||
            !Assert(AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue")?.FieldType == typeof(Queue<ClientTask>)) ||
            !Assert(AccessTools.DeclaredField(typeof(ProcessPacketTask), "packet")?.FieldType == typeof(Packet_Server)))
            return;
        _packet = AccessTools.FieldRefAccess<Packet_ServerChunk>(_closure, "p");
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Begin), transpiler: new HarmonyMethod(Rewrite)));
        if (!Rewritten)
            logger?.Warning("Komet ChunkLoadBudget: ClientMain.ExecuteMainThreadTasks is not the loop it was written for; " +
                            "every queued chunk loads at once, as in the engine");
    }

    // Task loop, LoadChunkFromPacket ("loadchunk"), and that task's body (reads the chunk from the closure's p)
    internal static MethodBase?[] Seams()
    {
        var load = AccessTools.DeclaredMethod(typeof(ClientWorldMap), "LoadChunkFromPacket");
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.ExecuteMainThreadTasks)), load,
            load is null ? null : Task(load)
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // The one method LoadChunkFromPacket turns into the task's delegate
    private static MethodInfo? Task(MethodBase load)
    {
        if (!Il.Take(PatchProcessor.GetOriginalInstructions(load), Il.MaxInstructions, out var code)) return null;
        var at = Il.Single(code, c => c.opcode == OpCodes.Ldftn);
        return at >= 0 && Assert(code[at].operand is MethodInfo) ? code[at].operand as MethodInfo : null;
    }

    internal static void Begin()
    {
        (_start, _forced, _first) = (Stopwatch.GetTimestamp(), 0, true);
        _ = Assert(_start > 0);
    }

    // Replaces the loop's reversedQueue.Count: the count, or 0 to end the frame. First asked when no launch task is due
    // and tasks aren't suspended, so it resumes a part-initialised far chunk; while one remains the queue waits.
    internal static int Gate(int count, ClientMain game)
    {
        _ = Assert(count >= 0);
        if (_first)
        {
            _first = false;
            if (BlockEntityBudget.Pending) BlockEntityBudget.Resume(game);
        }

        if (BlockEntityBudget.Pending)
        {
            if (Counting.Hud && count > 0) (Stops, Held) = (Stops + 1, Held + count);
            return 0;
        }

        return count <= 0 || Millis == Engine || game is not { Spawned: true }
            ? count
            : Decide(Pending(game), count, game.EntityPlayer?.Pos);
    }

    // By Begin's clock, boosted as Decide boosts it
    internal static bool Spent(ClientMain game)
    {
        var queue = NotNull(game) ? Pending(game) : null;
        var millis = Budget(queue?.Count ?? 0);
        return Assert(millis is >= Engine and <= MaxMillis) && millis > Engine && Elapsed(millis);
    }

    private static int Budget(int queued) =>
        Assert(queued >= 0) && Assert(Millis >= Engine) && queued > BoostAbove ? Math.Max(Millis, BoostMillis) : Millis;

    private static bool Elapsed(int millis) =>
        Assert(millis >= 0) && Assert(_start >= 0) && Stopwatch.GetTimestamp() - _start >= millis * Stopwatch.Frequency / 1000;

    // Chunk load may stop half way: budget on, chunk far, no near load being run to or waiting behind
    internal static bool MayHold(ClientMain game, Packet_ServerChunk? chunk)
    {
        if (!Budgeting || _forced > 0 || game is not { Spawned: true } ||
            game.EntityPlayer?.Pos is not { } pos || !Far(chunk, pos)) return false;
        var queue = Pending(game);
        return NotNull(queue) && Assert(_start > 0) && LastNear(queue, pos) < 0; // Begin started this frame's clock
    }

    internal static int Decide(Queue<ClientTask>? queue, int count, EntityPos? pos)
    {
        if (_forced > 0)
        {
            _ = Assert(_forced <= MaxScan);
            _forced--;
            return count;
        }

        var millis = Budget(count);
        if (!Assert(millis is > Engine and <= MaxMillis) || !Elapsed(millis)) return count;
        if (!NotNull(queue) || pos is null || !queue.TryPeek(out var next) || !Waits(next, pos)) return count;
        _forced = LastNear(queue, pos);
        if (_forced >= 0) return count;
        _forced = 0;
        if (Counting.Hud) (Stops, Held) = (Stops + 1, Held + count);
        return 0;
    }

    // A chunk unload, or the load of a chunk of the world (not a moving dimension's) more than NearChunks columns away
    internal static bool Waits(ClientTask? task, EntityPos pos) => task?.Action?.Target switch
    {
        ProcessPacketTask packet => Packet(packet) is { Id: UnloadPacket },
        { } target when target.GetType() == _closure && _packet is not null => Far(_packet(target), pos),
        _ => false
    };

    internal static bool Far(Packet_ServerChunk? chunk, EntityPos pos) =>
        chunk is { Y: >= 0 and < DimensionHeight } && NotNull(pos) &&
        Math.Max(Math.Abs(chunk.X - ((int)pos.X >> ChunkShift)), Math.Abs(chunk.Z - ((int)pos.Z >> ChunkShift))) > NearChunks;

    // The index of the last chunk load among the first MaxScan tasks that may not wait, -1 for none
    private static int LastNear(Queue<ClientTask> queue, EntityPos pos)
    {
        var (last, tasks) = (-1, queue.GetEnumerator());
        for (var i = 0; i < Math.Min(queue.Count, MaxScan) && tasks.MoveNext(); i++)
            if (tasks.Current?.Action?.Target is { } target && target.GetType() == _closure && _packet is not null &&
                !Far(_packet(target), pos))
                last = i;
        return Assert(last < MaxScan) ? last : -1;
    }

    // Loop test: the one reversedQueue get_Count, ldc.i4.0, bgt back; Gate goes between, else engine IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var queue = AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue");
        var count = AccessTools.PropertyGetter(typeof(Queue<ClientTask>), nameof(Queue<>.Count));
        var gate = AccessTools.DeclaredMethod(typeof(ChunkLoadBudget), nameof(Gate));
        if (!Il.Take(instructions, Il.MaxInstructions, out var code) || !NotNull(queue) || !NotNull(count) ||
            !NotNull(gate)) return code;
        var site = Il.CountLoop(code, queue, count);
        if (site < 0) return code;
        code.InsertRange(site + 1, [new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, gate)]);
        Rewritten = true;
        return code;
    }
}
