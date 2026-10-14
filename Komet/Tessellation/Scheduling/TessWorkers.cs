using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Chunk tessellation as WorkerPool's background job: the engine's one tessellation thread is 96-99 % busy while terrain
// loads. Each pool thread gets its own ChunkTesselator, made on the main thread like the engine's (constructor and
// Start subscribe it to settings, atlas and shape reloads) and pinned to that thread (BeginProcessChunk reloads the
// running thread's shape tesselator). A job is one normal pass via the engine's TesselateChunk, patches included,
// TessSafety must be fully installed. Retries and passes that threw go to the engine's thread (atlas and meal mesh code
// queue their fix only when thrown on game.TesselationThreadId); runtime atlases are forwarded to every instance. The
// engine's thread switches Parallel at tick start, off only with no pool thread in a pass: pool threads count in
// (_passing) before reading Parallel, the engine's thread switches off before reading _passing, both fenced, so no pass
// runs unguarded. A pass exception stops jobs for the world and logs once instead of ending the game.
internal static class TessWorkers
{
    private const int TesselatorSites = 1, DefaultPriority = 25, MaxPriority = 100;

    // Backlog past BoostOn (join or teleport: 3000-4700 marks; flight with the engine's thread alone peaked at 2600)
    // raises the ceiling until under BoostOff; else Priority sets it (eight passes at once took 3.9 instead of 1.5 ms
    // each and halved the 1 % low in flight). TessGovernor decides below it.
    private const int BoostOn = 3000, BoostOff = 500;

    // By pool thread, main thread writes
    private static readonly ChunkTesselator?[] Made = new ChunkTesselator?[WorkerPool.MaxThreads];

    // 1 while a pool thread's instance is being made
    private static readonly int[] Asked = new int[WorkerPool.MaxThreads];

    private static readonly Func<bool> Job = Pass;
    private static volatile ClientMain? _game;
    private static volatile ChunkTesselatorManager? _manager;

    // A pool thread's instance on its thread; null on every other thread
    [ThreadStatic] private static ChunkTesselator? _own;

    private static int _passing, _sites;

    // What comes first, 0-100: 0 smooth frames (engine's thread alone), 100 loading on every pool thread
    public static int Priority { get; set; } = DefaultPriority;

    private static bool Boosted { get; set; }

    public static bool Installed { get; private set; }
    public static bool Failed { get; private set; } // an exception stopped the jobs for this world
    public static bool Passing => Volatile.Read(ref _passing) > 0;

    public static void Install(Harmony harmony)
    {
        Stop();
        var (i, b) = (typeof(int), typeof(bool));
        var pass = TessSeams.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.TesselateChunk), i, i,
            i, b, b, b.MakeByRefType());
        var atlas = TessSeams.Method(typeof(ChunkTesselator), nameof(ChunkTesselator.RuntimeCreateNewBlockTextureAtlas),
            i);
        // Private engine fields, typed (missing: throws on a game thread): game here, started via TessSchedule
        var game = AccessTools.DeclaredField(typeof(ChunkTesselator), "game");
        if (!NotNull(harmony) || !NotNull(pass) || !NotNull(atlas) || !TessSafety.Installed ||
            !TessSchedule.Installed || !Assert(game?.FieldType == typeof(ClientMain))) return;
        _sites = 0;
        _ = NotNull(harmony.Patch(pass, transpiler: new HarmonyMethod(OwnTesselator)));
        if (!Assert(Volatile.Read(ref _sites) == TesselatorSites))
        {
            harmony.Unpatch(pass, AccessTools.Method(typeof(TessWorkers), nameof(OwnTesselator)));
            return;
        }

        _ = NotNull(harmony.Patch(atlas, postfix: new HarmonyMethod(Forward)));
        Installed = true;
        WorkerPool.Background = Job;
    }

    // Leaving the world or the mod (its patches go next): no more jobs
    public static void Stop()
    {
        if (ReferenceEquals(WorkerPool.Background, Job)) WorkerPool.Background = null;
        (Installed, _game, _manager, Failed, Boosted) = (false, null, null, false, false);
        WorkerPool.BackgroundLimit = 0;
        TessGovernor.Reset();
        Array.Clear(Made);
        Array.Clear(Asked);
        _ = Assert(!Installed);
    }

    // TesselateChunk's one TerrainChunkTesselator read becomes this thread's instance; another count: no install
    private static List<CodeInstruction> OwnTesselator(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.DeclaredField(typeof(ClientMain), nameof(ClientMain.TerrainChunkTesselator));
        var mine = AccessTools.Method(typeof(TessWorkers), nameof(Tesselator));
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code)) || !NotNull(field) || !NotNull(mine))
            return code;
        var substituted = Il.SubstituteSingle(code, c => c.opcode == OpCodes.Ldfld && Equals(c.operand, field), mine);
        Volatile.Write(ref _sites, substituted ? TesselatorSites : 0);
        return code;
    }

    private static ChunkTesselator Tesselator(ClientMain game)
    {
        var own = _own;
        _ = Assert(own is null || WorkerPool.Current >= 0); // only pool threads have their own
        return own is not null && ReferenceEquals(TessSeams.Game(own), game) ? own : game.TerrainChunkTesselator;
    }

    // Every pool thread's instance of this game that has started (for RecyclerTrim's scratch trim)
    internal static List<ChunkTesselator> Instances(ClientMain game)
    {
        var list = new List<ChunkTesselator>(WorkerPool.MaxThreads);
        foreach (var made in Made.Bounded(WorkerPool.MaxThreads))
            if (made is not null && ReferenceEquals(TessSeams.Game(made), game) && TessSeams.Started(made)) list.Add(made);
        return Assert(list.Count <= WorkerPool.MaxThreads) ? list : [];
    }

    // Atlases the engine's instance learns of reach every pool instance (main thread; each takes its own ReloadLock)
    private static void Forward(ChunkTesselator __instance, int textureId)
    {
        if (!NotNull(__instance) || !Assert(textureId >= 0)) return;
        var game = TessSeams.Game(__instance);
        // A pool thread's __instance: this loop's call
        if (game is null || !ReferenceEquals(game.TerrainChunkTesselator, __instance)) return;
        // BlockTextureAtlasManager.RuntimeCreateNewAtlas
        _ = Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId);
        foreach (var made in Made.Bounded(WorkerPool.MaxThreads))
            if (made is not null && ReferenceEquals(TessSeams.Game(made), game) && TessSeams.Started(made))
                _ = made.RuntimeCreateNewBlockTextureAtlas(textureId);
    }

    // Pool thread cap: one at priority 1 to all running at 100; a backlog lifts it to half, to all from priority 50
    internal static int Ceiling(int threads, int priority, bool boosted)
    {
        if (!Assert(threads is >= 0 and <= WorkerPool.MaxThreads) || threads == 0 || priority <= 0) return 0;
        var share = 1 + (int)Math.Round((threads - 1) * (double)Math.Min(priority, MaxPriority) / MaxPriority);
        var boost = priority >= MaxPriority / 2 ? threads : (threads + 1) / 2;
        if (!boosted) boost = 0;
        return Assert(share >= 1) ? Math.Clamp(Math.Max(share, boost), 1, threads) : 1;
    }

    // Engine's thread, each tick start: switches Parallel; this thread's passes lock exactly while it is on
    internal static void Steer(ClientMain game, ChunkTesselatorManager manager, bool active)
    {
        if (!Installed || !NotNull(game) || !NotNull(manager)) return;
        if (!ReferenceEquals(game, _game)) // a new world: the old one's instances are no one's any more
        {
            Array.Clear(Made);
            Array.Clear(Asked);
            (_game, _manager, Failed) = (game, manager, false);
        }

        var backlog = TessSchedule.Backlog;
        Boosted = backlog > BoostOn || (Boosted && backlog > BoostOff);
        var priority = Math.Clamp(Priority, 0, MaxPriority);
        var ceiling = Ceiling(WorkerPool.Running, priority, Boosted);
        var on = active && ceiling > 0 && !Failed && !TessSafety.Broken && !WorkerPool.BackgroundOff;
        (TessGovernor.Ceiling, TessGovernor.Scale) = (ceiling, 0.5 + 2.0 * priority / MaxPriority);
        WorkerPool.BackgroundLimit = on ? TessGovernor.Limit(ceiling) : 0;
        _ = Assert(WorkerPool.Current < 0); // the engine's thread, never a pool thread
        if (on) TessSafety.Open(true);
        else if (TessSafety.Parallel && !Passing)
        {
            TessSafety.Open(false);
            // A pool thread counted itself in first: off at a later tick
            if (Volatile.Read(ref _passing) > 0) TessSafety.Open(true);
        }

        TessSafety.Guard(TessSafety.Parallel);
        if (on && backlog > 0) WorkerPool.WakeBackground();
    }

    // The background job: one pass on this pool thread, true when it ran one
    private static bool Pass()
    {
        var (game, manager) = (_game, _manager);
        if (!Installed || Failed || game is null || manager is null || !game.ShouldTesselateTerrain ||
            Own(game) is null) return false;
        _ = Assert(_own is not null && WorkerPool.Current >= 0);
        _ = Interlocked.Increment(ref _passing); // counted in before Parallel is read (the class comment)
        try
        {
            return TessSafety.Parallel && TessSchedule.Work(manager, game);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or ThreadInterruptedException))
        {
            Fail(game, e);
            return false;
        }
        finally
        {
            _ = Assert(Interlocked.Decrement(ref _passing) >= 0);
        }
    }

    // This pool thread's instance for the world, or null while the main thread makes it
    private static ChunkTesselator? Own(ClientMain game)
    {
        var own = _own;
        if (own is not null && ReferenceEquals(TessSeams.Game(own), game)) return own;
        var slot = WorkerPool.Current;
        if (!Index(slot, Made.Length)) return null;
        if (Volatile.Read(ref Made[slot]) is { } made && ReferenceEquals(TessSeams.Game(made), game) &&
            TessSeams.Started(made))
        {
            TessSafety.Guard(true); // a pool thread passes only while Parallel is on
            return _own = made;
        }

        if (Interlocked.Exchange(ref Asked[slot], 1) == 0) Ask(game, slot);
        return null;
    }

    // Its own method: a closure over game and slot in Own would allocate on every pass
    private static void Ask(ClientMain game, int slot)
    {
        if (!NotNull(game) || !Index(slot, Made.Length)) return;
        game.EnqueueMainThreadTask(() => Make(game, slot), "komet-tesselator");
    }

    private static void Make(ClientMain game, int slot)
    {
        if (!Index(slot, Made.Length) || !ReferenceEquals(game, _game) || game.threadsShouldExit) return;
        _ = Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId);
        try
        {
            var tesselator = new ChunkTesselator(game);
            tesselator.LightlevelsReceived();
            tesselator.BlockTexturesLoaded(); // both in: Start()
            if (Assert(TessSeams.Started(tesselator))) Volatile.Write(ref Made[slot], tesselator);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(game, e);
        }
        finally
        {
            Volatile.Write(ref Asked[slot], 0);
        }
    }

    private static void Fail(ClientMain game, Exception e)
    {
        // An unclean exit, as the engine's own threads report it
        if (!NotNull(e) || game.threadsShouldExit || Failed) return;
        Failed = true;
        game.Logger.Error("Komet: a tessellation pass on a worker thread failed, the engine's thread goes on alone " +
                          "for this world: {0}", e);
    }
}
