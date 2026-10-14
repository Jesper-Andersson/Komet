using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// Client chunks stay unpacked until SystemCompressChunks packs one per 20 ms (max 50/s) after 8 s untouched
// (optimizeRamMode 1; 4 s in 2): flying leaves thousands unpacked (100-250 KB vs a few KB). Levels pack PerTick a tick
// (1: 4 = 200/s; 2: 8, half the time; 3: 16, a quarter, data pool <= 500 spares vs 4000) with the engine's own Pack and
// TryCommitPackAndFree; chunks within two of the player stay unpacked. Reads unpack again: more CPU flying, less RAM.
internal static partial class ChunkPacking
{
    private const int MaxLevel = 3;
    private static readonly int[] PerTick = [1, 4, 8, 16];
    private static readonly int[] Divisor = [1, 1, 2, 4];

    [ThreadStatic] private static bool _inside;
    private const string Compress = "Vintagestory.Client.NoObf.SystemCompressChunks"; // internal
    private static Action<object, float>? _tick;
    private static AccessTools.FieldRef<object, int[]>? _ttls;
    private static int[]? _engineTtls;
    private static int _appliedLevel = -1;

    public static int Level { get; set; } = 1;
    public static int Max => MaxLevel;
    public static bool Installed { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkDataPool")]
    private static extern ref ClientChunkDataPool? DataPool(ClientWorldMap map);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var type = AccessTools.TypeByName(Compress);
        var tick = type is null ? null : AccessTools.DeclaredMethod(type, "OnSeperateThreadGameTick", [typeof(float)]);
        if (!NotNull(harmony) || type is null || tick is null || AccessTools.DeclaredField(type, "ttlsByRamMode") is null) return;
        // the patched method itself (its postfix sees _inside), compiled once: no allocation per call
        var (system, dt) = (Expression.Parameter(typeof(object)), Expression.Parameter(typeof(float)));
        _tick = Expression.Lambda<Action<object, float>>(
            Expression.Call(Expression.Convert(system, type), tick, dt), system, dt).Compile();
        _ttls = AccessTools.FieldRefAccess<object, int[]>(AccessTools.DeclaredField(type, "ttlsByRamMode"));
        _ = NotNull(harmony.Patch(tick, postfix: new HarmonyMethod(typeof(ChunkPacking), nameof(Ticked))));
        (_engineTtls, _appliedLevel, Installed) = (null, -1, true);
        InstallServer(harmony); // the in-process server's chunks too (singleplayer)
        // the commit rechecks the time (8 s client, 15 s server): same divisor, or early packs are discarded
        var commit = AccessTools.DeclaredMethod(typeof(Vintagestory.Common.WorldChunk), "TryCommitPackAndFree", [typeof(int)]);
        if (commit is not null) _ = NotNull(harmony.Patch(commit, new HarmonyMethod(typeof(ChunkPacking), nameof(Committing))));
    }

    public static void Stop() => (_tick, _ttls, _engineTtls, _appliedLevel, Installed) = (null, null, null, -1, false);

    // Separate-thread tick: the engine packed one chunk, this packs PerTick - 1 more the same way
    private static void Ticked(object __instance, float dt)
    {
        if (_inside || !Installed || !NotNull(__instance)) return;
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (level != _appliedLevel) Apply(__instance, level);
        if (_tick is not { } tick || level == 0) return;
        _inside = true;
        try
        {
            for (var i = 1; i < Math.Min(PerTick[level], 64); i++) tick(__instance, dt);
        }
        catch (Exception) when (Assert(Level >= 0))
        {
            Level = 0; // the engine's own tick threw: it packs at its own pace from now on
            throw;
        }
        finally
        {
            _inside = false;
        }
    }

    private static void Committing(ref int chunkTTL)
    {
        var level = Installed ? Math.Clamp(Level, 0, MaxLevel) : 0;
        if (Divisor[level] > 1) chunkTTL = Math.Max(1000, chunkTTL / Divisor[level]);
        _ = Assert(chunkTTL >= 0);
    }

    // Times untouched: the engine's divided by the level's divisor
    private static void Apply(object system, int level)
    {
        if (_ttls?.Invoke(system) is not { Length: 3 } ttls || !Index(level, Divisor.Length)) return;
        _engineTtls ??= [.. ttls];
        for (var i = 0; i < Math.Min(ttls.Length, 3); i++) ttls[i] = Math.Max(1000, _engineTtls[i] / Divisor[level]);
        _appliedLevel = level;
        _ = Assert(ttls[1] >= 1000);
    }

    // Level 3: cap the data pool's spare chunk datas, on the main thread
    public static void Spares(ClientMain game)
    {
        if (!Installed || Level < MaxLevel || game.WorldMap is not { } map || DataPool(map) is not { } pool || !Assert(pool.CacheSize >= 0)) return;
        (pool.CacheSize, pool.SlowDisposeThreshold) = (Math.Min(pool.CacheSize, 500), Math.Min(pool.SlowDisposeThreshold, 350));
    }
}
