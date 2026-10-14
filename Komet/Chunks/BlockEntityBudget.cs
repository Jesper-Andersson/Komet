using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Chunks;

// A far chunk's block entities initialise one at a time on ChunkLoadBudget's clock instead of all at once in
// loadChunkMT (a base chunk: ms). Once spent the chunk stays with loadedFromServer false (as between insert and init,
// which tessellation and the minimap wait out) and loadChunkMT returns before its tail; next frame it reruns and
// resumes in engine order while ChunkLoadBudget holds the queue. A main-thread read before an entity's turn initialises
// it (Accessed). Near, moving dimension, overloaded and near-load-behind chunks stay whole; MaxFrames caps the spread.
internal static class BlockEntityBudget
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x5A091E345DCCC308UL;
    private const int MaxEntities = 32 * 32 * 32; // one per block of the chunk
    private const int MaxFrames = 16; // frames a chunk may hold the queue before the rest of it goes at once
    private const string InitMark = "initbe-"; // the engine's profiler mark after each one

    // Main thread: the pending chunk's block entities initialised so far, by the loop or on access
    private static readonly HashSet<BlockEntity> Done = new(ReferenceEqualityComparer.Instance);
    private static readonly SeamGuard Guard = new(nameof(BlockEntityBudget), PatchKinds.All, [typeof(BlockEntityBudget)],
        watched: 2);

    // Main thread: the part-initialised far chunk, its packet and game
    private static ClientChunk? _chunk;
    private static Packet_ServerChunk? _packet;
    private static ClientMain? _game;
    private static bool _inside; // Komet is initialising one of them
    private static int _frames; // frames the pending chunk has gone on in

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    // Body not verified or rewritten, or another mod patches what the split skips or repeats: every chunk loads whole
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    // Patches on the loop or loadChunkMT would skip or repeat per frame; rechecked on LevelFinalize after all mods
    internal static Action Recheck { get; } = static () => Guard.Recheck(Rewritten);
    internal static bool Pending => _chunk is not null;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "InitBlockEntitiesFromPacket")]
    private static extern void InitAll(ClientChunk chunk, ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "loadChunkMT")]
    private static extern void Load(ClientWorldMap map, Packet_ServerChunk p, ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetClientChunk")]
    private static extern ClientChunk Held(ClientWorldMap map, int chunkX, int chunkY, int chunkZ);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        Rewritten = false;
        var seams = Guard.Reset(Seams(), logger);
        Clear();
        if (!NotNull(harmony) || !Assert(seams.Length == 3) || !Assert(Array.TrueForAll(seams, s => s is not null))) return;
        var shaped = Guard.Match(seams, fingerprint);
        var accessed = AccessTools.DeclaredMethod(typeof(BlockEntityBudget), nameof(Accessed));
        if (!shaped || !NotNull(accessed) || !Assert(Il.Binds(seams[2]!, accessed))) return;
        _ = NotNull(harmony.Patch(seams[1], transpiler: new HarmonyMethod(Rewrite)));
        if (!Rewritten)
        {
            logger?.Warning("Komet BlockEntityBudget: ClientWorldMap.loadChunkMT is not the body it was written for; every " +
                            "chunk's block entities initialise at once, as in the engine");
            return;
        }

        _ = NotNull(harmony.Patch(seams[2], postfix: new HarmonyMethod(accessed)));
        Recheck();
    }

    // The loop the split reproduces, the method it rewrites and reruns, and the read it initialises on
    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(ClientChunk), "InitBlockEntitiesFromPacket", [typeof(ClientMain)]),
            AccessTools.DeclaredMethod(typeof(ClientWorldMap), "loadChunkMT", [typeof(Packet_ServerChunk), typeof(ClientChunk)]),
            AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.GetLocalBlockEntityAtBlockPos), [typeof(BlockPos)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // Chunk done or gone from the map: the uninitialised ones go with it
    public static void Clear()
    {
        (_chunk, _packet, _game, _inside, _frames) = (null, null, null, false, 0);
        Done.Clear();
        _ = Assert(Done.Count == 0) && Assert(Done.Comparer == ReferenceEqualityComparer.Instance);
    }

    // At world leave (Komet's Dispose, before SystemUnloadChunks): unloading an uninitialised block entity breaks
    // OnBlockUnloaded overrides (windmill rotor reads Api), and they registered nothing, so they are removed first.
    public static void Leave()
    {
        if (_chunk is { } chunk)
        {
            var fresh = new List<BlockPos>();
            foreach (var (pos, entity) in chunk.BlockEntities.Bounded(MaxEntities))
                if (!Done.Contains(entity) && entity?.Api is null) fresh.Add(pos);
            foreach (var pos in fresh.Bounded(MaxEntities)) _ = chunk.RemoveBlockEntity(pos);
            _ = Assert(Done.Count > 0) && Assert(fresh.Count < MaxEntities); // one went before the stop
        }

        Clear();
    }

    // loadChunkMT's InitBlockEntitiesFromPacket call (outside its lock) -> Init(chunk, game, p), ret when false
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator,
        MethodBase original)
    {
        Rewritten = false;
        var (loop, init) = (Guard.Seams is [MethodInfo first, ..] ? first : null,
            AccessTools.DeclaredMethod(typeof(BlockEntityBudget), nameof(Init)));
        if (!Il.Take(instructions, Il.MaxInstructions, out var code) || !NotNull(generator) || !NotNull(loop) ||
            !NotNull(init) || original?.GetParameters() is not [{ ParameterType: var packet }, ..] ||
            packet != typeof(Packet_ServerChunk)) return code;
        var at = Il.Single(code, c => c.Calls(loop));
        if (at < 0 || !Assert(at + 1 < code.Count) || !Outside(code, at) || code[at].labels.Count > 0) return code;
        var next = generator.DefineLabel();
        code[at + 1].labels.Add(next);
        (code[at].opcode, code[at].operand) = (OpCodes.Call, init);
        code.InsertRange(at + 1, [new CodeInstruction(OpCodes.Brtrue, next), new CodeInstruction(OpCodes.Ret)]);
        code.Insert(at, new CodeInstruction(OpCodes.Ldarg_1));
        Rewritten = Assert(code[at + 1].Calls(init));
        return code;
    }

    // A ret may leave the method only outside every exception block
    private static bool Outside(List<CodeInstruction> code, int at)
    {
        if (!NotNull(code) || !Index(at, code.Count)) return false;
        var depth = 0;
        for (var i = 0; i <= Math.Min(at, Il.MaxInstructions); i++)
            foreach (var block in code[i].blocks.Bounded(Il.MaxInstructions))
                depth += block.blockType switch
                {
                    ExceptionBlockType.BeginExceptionBlock => 1,
                    ExceptionBlockType.EndExceptionBlock => -1,
                    _ => 0
                };
        return Assert(depth >= 0) && depth == 0 && code[at].blocks.Count == 0;
    }

    // False while some block entities are not initialised: loadChunkMT then returns before its tail
    internal static bool Init(ClientChunk chunk, ClientMain game, Packet_ServerChunk p)
    {
        _ = NotNull(chunk) && NotNull(game); // the engine's call throws on either
        var resuming = _chunk is not null && ReferenceEquals(chunk, _chunk);
        if (!resuming && !Splits(chunk, game, p))
        {
            InitAll(chunk, game);
            return true;
        }

        var complete = Run(chunk, game, p, resuming);
        if (complete) Clear();
        _ = Assert(complete || ReferenceEquals(_chunk, chunk));
        return complete;
    }

    // Splittable: far, 2+ block entities, none null (engine loop would throw), none pending, budget on
    private static bool Splits(ClientChunk? chunk, ClientMain? game, Packet_ServerChunk? p) =>
        Enabled && !Blocked && _chunk is null && ChunkLoadBudget.Budgeting && game is { Spawned: true } &&
        game.EntityPlayer?.Pos is { } pos && ChunkLoadBudget.Far(p, pos) &&
        chunk?.BlockEntities is { Count: > 1 and <= MaxEntities } entities && !entities.ContainsValue(null!) &&
        Assert(Done.Count == 0);

    // Engine loop from where it stopped: true when all done, false when out of budget; one always runs
    private static bool Run(ClientChunk chunk, ClientMain game, Packet_ServerChunk p, bool resuming)
    {
        var (entities, ran, mayStop) = (chunk.BlockEntities.Values.GetEnumerator(), 0, true);
        for (var i = 0; i < MaxEntities && entities.MoveNext(); i++)
        {
            var entity = entities.Current;
            if (resuming && (Done.Contains(entity) || entity?.Api is not null)) continue;
            if (ran > 0 && mayStop && ChunkLoadBudget.Spent(game))
            {
                if (Enabled && !Blocked && _frames < MaxFrames && ChunkLoadBudget.MayHold(game, p))
                {
                    Stop(chunk, game, p, entity);
                    return false;
                }

                mayStop = false; // near now, something near waits or it held long enough: the rest goes at once
            }

            // Recorded as it goes: an Initialize removing a passed entry (a mod's) would shift a later recount
            _ = Done.Add(entity!);
            Initialize(entity!, game, true);
            ran++;
        }

        return Assert(chunk.BlockEntities.Count <= MaxEntities) && Assert(ran > 0 || resuming);
    }

    private static void Stop(ClientChunk chunk, ClientMain game, Packet_ServerChunk p, BlockEntity? next)
    {
        (_chunk, _packet, _game) = (chunk, p, game);
        _ = Assert(Done.Count > 0) && Assert(!Done.Contains(next!));
    }

    // Engine's loop body for one: Initialize, logged as the engine logs, then the profiler mark
    private static void Initialize(BlockEntity entity, ClientMain game, bool mark)
    {
        if (!NotNull(entity) || !NotNull(game)) return;
        _inside = true;
        try
        {
            entity.Initialize(game.api);
        }
        catch (Exception e)
        {
            Failed(entity, game, e);
        }
        finally
        {
            _inside = false;
        }

        if (mark && ScreenManager.FrameProfiler.Enabled)
            ScreenManager.FrameProfiler.Mark(InitMark, game.ClassRegistryInt.blockEntityTypeToClassnameMapping[entity.GetType()]);
    }

    private static void Failed(BlockEntity entity, ClientMain game, Exception e)
    {
        if (!NotNull(entity) || !NotNull(game)) return;
        if (game.ClassRegistryInt is { } registry)
            game.Logger.Error("Exception thrown when initializing a block entity with classname {0}:",
                registry.blockEntityTypeToClassnameMapping[entity.GetType()]);
        else game.Logger.Error("Exception thrown when initializing a block entity {0}:", entity.GetType());
        game.Logger.Error(e);
    }

    // Frame's first queue look (ChunkLoadBudget.Gate): reruns loadChunkMT for the pending chunk ahead of the queue
    internal static void Resume(ClientMain game)
    {
        var (chunk, packet) = (_chunk, _packet);
        if (chunk is null) return;
        var map = NotNull(game) ? game.WorldMap : null;
        if (packet is null || map is null || !ReferenceEquals(game, _game) ||
            !ReferenceEquals(Held(map, packet.X, packet.Y, packet.Z), chunk))
        {
            Clear();
            return;
        }

        _frames++;
        Load(map, packet, chunk);
        _ = Assert(_chunk is null || ReferenceEquals(_chunk, chunk));
    }

    // Postfix on GetLocalBlockEntityAtBlockPos: a pending chunk's block entity read on the main thread is initialised
    internal static void Accessed(WorldChunk __instance, BlockEntity? __result)
    {
        if (_chunk is not { } chunk || !ReferenceEquals(__instance, chunk) || __result is not { Api: null } entity || _inside ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId || _game is not { } game) return;
        if (!Assert(Done.Count < MaxEntities) || !Done.Add(entity)) return;
        Initialize(entity, game, false);
    }
}
