using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.Chunks;

// The singleplayer server packs its chunk copies the same way (ServerSystemCompressChunks,
// MagicNum.UncompressedChunkTTL 15 s), but its scan spares |dx| < 2 OR |dz| < 2 of a player, a cross through the whole
// loaded area (meant AND): 28000 server vs 17000 client chunk datas at view distance 1536. FindFreeableMemory is
// replaced with AND and the level's time.
internal static partial class ChunkPacking
{
    private const string ServerCompress = "Vintagestory.Server.ServerSystemCompressChunks"; // internal
    private static Action<object>? _serverTick;
    private static AccessTools.FieldRef<object, Queue<long>>? _queue;
    private static AccessTools.FieldRef<object, object>? _queueLock;

    [ThreadStatic] private static bool _serverInside;

    public static long ServerQueued { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "server")]
    private static extern ref ServerMain? ServerOf(ServerSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "loadedChunks")]
    private static extern ref Dictionary<long, ServerChunk>? Loaded(ServerMain server);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "loadedChunksLock")]
    private static extern ref FastRWLock LoadedLock(ServerMain server);

    private static void InstallServer(Harmony harmony)
    {
        var type = AccessTools.TypeByName(ServerCompress);
        var scan = type is null ? null : AccessTools.DeclaredMethod(type, "FindFreeableMemory");
        var tick = type is null ? null : AccessTools.DeclaredMethod(type, "OnSeparateThreadTick", []);
        var (queue, gate) = type is null ? (null, null) :
            (AccessTools.DeclaredField(type, "compactableChunks"), AccessTools.DeclaredField(type, "compactableChunksLock"));
        if (!NotNull(harmony) || type is null || scan is null || tick is null || queue is null || gate is null ||
            !typeof(ServerSystem).IsAssignableFrom(type)) return;
        (_queue, _queueLock) = (AccessTools.FieldRefAccess<object, Queue<long>>(queue), AccessTools.FieldRefAccess<object, object>(gate));
        var system = Expression.Parameter(typeof(object));
        _serverTick = Expression.Lambda<Action<object>>(
            Expression.Call(Expression.Convert(system, type), tick), system).Compile();
        _ = NotNull(harmony.Patch(scan, new HarmonyMethod(typeof(ChunkPacking), nameof(Scanning))));
        _ = NotNull(harmony.Patch(tick, postfix: new HarmonyMethod(typeof(ChunkPacking), nameof(ServerTicked))));
    }

    // FindFreeableMemory: chunks untouched for the level's time, except the 3x3 columns around a playing client
    private static bool Scanning(object __instance)
    {
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (level == 0 || __instance is not ServerSystem own || ServerOf(own) is not { } server || Loaded(server) is not { } loaded ||
            _queue?.Invoke(__instance) is not { } queue || _queueLock?.Invoke(__instance) is not { } gate) return true;
        var near = new List<(int X, int Z)>();
        using (var clients = server.Clients.Values.GetEnumerator())
            for (var i = 0; i < 1024 && clients.MoveNext(); i++)
                if (clients.Current is { State: EnumClientState.Playing, ChunkPos: { } at }) near.Add((at.X, at.Z));
        var ttl = Math.Max(1000, MagicNum.UncompressedChunkTTL / Divisor[level]);
        var now = Environment.TickCount;
        lock (gate)
        {
            ref var chunksLock = ref LoadedLock(server);
            chunksLock.AcquireReadLock();
            try
            {
                using var chunks = loaded.GetEnumerator(); // no copy of 50000 entries every scan
                for (var i = 0; i < 1 << 22 && chunks.MoveNext(); i++)
                {
                    var (key, chunk) = chunks.Current;
                    if (chunk is null || chunk.IsPacked() || now - chunk.lastReadOrWrite <= ttl) continue;
                    var pos = server.WorldMap.ChunkPosFromChunkIndex3D(key);
                    if (!chunk.Empty && pos.Dimension == 0 && Near(near, pos.X, pos.Z))
                        continue;
                    queue.Enqueue(key);
                    ServerQueued++;
                }
            }
            finally
            {
                chunksLock.ReleaseReadLock();
            }
        }

        return !Assert(ServerQueued >= 0);
    }

    // A player's chunk column or a neighbour (no closure: the scan sees every loaded chunk)
    private static bool Near(List<(int X, int Z)> near, int x, int z)
    {
        _ = NotNull(near);
        for (var j = 0; j < Math.Min(near.Count, 1024); j++)
            if (Math.Abs(near[j].X - x) < 2 && Math.Abs(near[j].Z - z) < 2) return true;
        return !Assert(near.Count <= 1024);
    }

    private static void ServerTicked(object __instance)
    {
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (_serverInside || level == 0 || _serverTick is not { } tick || !NotNull(__instance)) return;
        _serverInside = true;
        _ = Assert(PerTick.Length == MaxLevel + 1);
        try
        {
            for (var i = 1; i < Math.Min(PerTick[level], 64); i++) tick(__instance);
        }
        finally
        {
            _serverInside = false;
        }
    }
}
