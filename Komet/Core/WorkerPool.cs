using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Core;

// Workers for frame jobs (FrustumSweep culls) and background jobs (TessWorkers, below BackgroundLimit); frame jobs win.
// The cursor packs count (high) and next index (low): one Interlocked increment claims an index of a known batch, and
// the caller waits only for claimed items. Full fences on Idle and publish: a worker is woken or sees the batch.
// Threads belong to a world (DestroyGameSession joins them); surplus ones park, keeping their ChunkTesselator.
internal static class WorkerPool
{
    public enum FrameResult
    {
        Declined,
        Done,
        Failed
    }

    public const int MaxThreads = 8, MaxItems = 1 << 16, MaxFrameFailures = 2;

    private const int IdleMs = 50, Spins = 32, SignalSpins = 8, JoinMs = 2000, FrameCounter = 0, BackgroundCounter = 1,
        MaxRetired = 64, MaxWaits = 1 << 24;

    private const long MaxRounds = long.MaxValue; // runs until its world or generation ends
    private const long Low = 0xFFFFFFFFL;
    private static readonly int DefaultThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, MaxThreads);

    private static readonly bool Accessible = Seams();
    private static readonly Lock Gate = new();
    private static readonly ManualResetEventSlim Finished = new(false);
    private static int _relay; // resting workers still to be woken by those already woken

    private static readonly Tally Busy = new(BackgroundCounter + 1);

    private static readonly List<Worker> Retired = []; // workers of an earlier world or generation, until they ended
    private static Worker[] _workers = [];
    private static object? _world; // ClientMain the threads belong to; null without a world (tests)
    private static int _generation, _done, _frameFailures, _backgroundLimit, _inBackground;
    private static long _cursor; // count << 32 | next index; count 0 while no batch is open
    private static Action<int>? _job;
    private static Exception? _error;
    [ThreadStatic] private static int _number; // this thread's worker number + 1; 0 on every other thread

    // 0: culling and tessellation stay on the game's threads
    public static int Wanted { get; set; } = DefaultThreads;

    public static Func<bool>? Background { get; set; } // one background job, true when it ran one; must not throw

    public static bool FrameOff => Volatile.Read(ref _frameFailures) >= MaxFrameFailures;

    public static bool BackgroundOff { get; private set; } // a background job threw past its own handler
    public static Exception? LastError { get; private set; }
    public static int Current => _number - 1;
    public static long FrameTicks => Busy.Total(FrameCounter);
    public static long BackgroundTicks => Busy.Total(BackgroundCounter);
    public static int InBackground => Volatile.Read(ref _inBackground);

    // Set by the job's owner (TessWorkers)
    public static int BackgroundLimit
    {
        get => Volatile.Read(ref _backgroundLimit);
        set => Volatile.Write(ref _backgroundLimit, Assert(value is >= 0 and <= MaxThreads) ? value : 0);
    }

    // FrustumSweep's stage batch stays open across a stage's calls
    public static bool Free => _job is null && !FrameOff && _number == 0 && Running > 0;

    public static int Running
    {
        get
        {
            var n = 0;
            foreach (var worker in Volatile.Read(ref _workers).Bounded(MaxThreads))
                if (!worker.Parked)
                    n++;
            return Assert(n <= MaxThreads) ? n : 0;
        }
    }

    // Main thread, every few hundred ms
    public static void Steer(ClientMain? game)
    {
        if (game is null || game.threadsShouldExit ||
            !Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)) return;
        Resize(game, Math.Clamp(Wanted, 0, MaxThreads));
    }

    // Another world retires every thread first
    internal static void Resize(object? world, int wanted)
    {
        if (!Assert(wanted is >= 0 and <= MaxThreads) || (world is not null && !Assert(Accessible))) return;
        lock (Gate)
        {
            if (!ReferenceEquals(world, _world)) Retire();
            _world = world;
            var workers = _workers;
            for (var i = 0; i < Math.Min(workers.Length, MaxThreads); i++) workers[i].Parked = i >= wanted;
            if (wanted <= workers.Length)
            {
                _ = Assert(Running == wanted);
                return;
            }

            var grown = new Worker[wanted];
            workers.CopyTo(grown, 0);
            for (var i = workers.Length; i < Math.Min(wanted, MaxThreads); i++)
            {
                var worker = grown[i] = new Worker(world as ClientMain, i, Volatile.Read(ref _generation));
                if (world is ClientMain game)
                    Threads(game).Add(worker.Thread); // on the main thread, which DestroyGameSession iterates on
                _ = Assert(!worker.Thread.IsAlive);
                worker.Thread.Start();
            }

            Volatile.Write(ref _workers, grown);
            _ = Assert(grown.Length <= MaxThreads);
        }
    }

    // True when all threads ended within JoinMs
    public static bool Stop()
    {
        Worker[] ending;
        lock (Gate)
        {
            Retire();
            _world = null;
            ending = [.. Retired];
            Retired.Clear();
            _ = Assert(_workers.Length == 0);
            // a batch left open by a stage that threw past CloseFrame would block the next world's pool forever
            _ = Interlocked.Exchange(ref _cursor, 0);
            (_job, _frameFailures, BackgroundOff, LastError) = (null, 0, false, null);
        }

        var (joined, deadline) = (true, Environment.TickCount64 + JoinMs);
        foreach (var worker in ending.Bounded(MaxRetired))
        {
            var ended = worker.Thread.Join((int)Math.Max(0, deadline - Environment.TickCount64));
            if (ended) worker.Dispose();
            joined &= ended;
        }

        return joined;
    }

    private static void Retire()
    {
        _ = Interlocked.Increment(ref _generation);
        var alive = 0;
        for (var i = 0; i < Math.Min(Retired.Count, MaxRetired); i++)
            if (Retired[i].Thread.IsAlive) Retired[alive++] = Retired[i];
            else Retired[i].Dispose();
        Retired.RemoveRange(alive, Retired.Count - alive);
        foreach (var worker in _workers.Bounded(MaxThreads))
        {
            worker.Parked = true;
            worker.Signal.Set();
            Retired.Add(worker);
        }

        Volatile.Write(ref _workers, []);
        _ = Assert(Retired.Count <= MaxRetired);
    }

    // Declined without a pool, Failed when an item threw (LastError)
    public static FrameResult RunFrame(Action<int> job, int count, int helpers)
    {
        if (!NotNull(job) || count < 2 || !OpenFrame(job, count, Math.Min(helpers, count - 1)))
            return FrameResult.Declined;
        var mine = Claim();
        _ = Assert(mine <= count);
        var spin = new SpinWait();
        for (var i = 0; i < Spins && Volatile.Read(ref _done) < count; i++) spin.SpinOnce(-1);
        if (Volatile.Read(ref _done) < count) Finished.Wait();
        _ = Assert(Volatile.Read(ref _done) == count); // every item ran exactly once
        _ = Interlocked.Exchange(ref _cursor, 0);
        _ = Assert(ReferenceEquals(_job, job)); // no second batch ran meanwhile
        return Ended();
    }

    // No waiting: the caller claims via ClaimBelow and must CloseFrame before anything else opens a batch.
    public static bool OpenFrame(Action<int> job, int count, int helpers)
    {
        if (!NotNull(job) || !Assert(count <= MaxItems) || count < 1 || Running == 0 || FrameOff ||
            !Assert(_number == 0) || !Assert(_job is null))
            return false;
        (_job, _done, _error) = (job, 0, null);
        Finished.Reset();
        _ = Interlocked.Exchange(ref _cursor, (long)count << 32); // publishes the batch
        _ = Assert(Wake(Math.Min(helpers, count)) <= MaxThreads);
        return true;
    }

    // CAS-claimed while the index is below `below`; returns how many.
    public static int ClaimBelow(int below)
    {
        var ran = 0;
        for (var i = 0; i < MaxItems && _job is not null; i++)
        {
            var seen = Volatile.Read(ref _cursor);
            var (count, at) = ((int)(seen >>> 32), (int)(seen & Low));
            if (at >= Math.Min(count, below)) break;
            if (Interlocked.CompareExchange(ref _cursor, seen + 1, seen) != seen) continue;
            Item(at, count);
            ran++;
        }

        return Assert(ran <= MaxItems) ? ran : 0;
    }

    // Waits for every claimed item; unclaimed ones never run.
    public static FrameResult CloseFrame()
    {
        if (_job is null || !Assert(_number == 0)) return FrameResult.Declined;
        var seen = Interlocked.Exchange(ref _cursor, 0);
        var claimed = (int)Math.Min(seen >>> 32, seen & Low); // an increment past the count claimed nothing
        var spin = new SpinWait();
        for (var i = 0; i < MaxWaits && Volatile.Read(ref _done) < claimed; i++) spin.SpinOnce(-1);
        _ = Assert(Volatile.Read(ref _done) == claimed); // every claimed item ended, no other ran
        return Ended();
    }

    private static FrameResult Ended()
    {
        _job = null;
        if (Volatile.Read(ref _error) is not { } error) return FrameResult.Done;
        LastError = error;
        _ = Assert(Interlocked.Increment(ref _frameFailures) > 0);
        return FrameResult.Failed;
    }

    // Caller wakes one, the rest relay, so the caller pays one wake-up. Returns the caller's count.
    private static int Wake(int n)
    {
        _ = Assert(n <= MaxItems);
        if (n <= 0) return 0;
        Volatile.Write(ref _relay, Math.Min(n, MaxThreads) - 1);
        var woken = WakeOne() ? 1 : 0;
        _ = Assert(woken <= n) && Assert(Volatile.Read(ref _relay) < MaxThreads);
        return woken;
    }

    private static bool WakeOne()
    {
        _ = Assert(_workers.Length <= MaxThreads);
        foreach (var worker in Volatile.Read(ref _workers).Bounded(MaxThreads))
        {
            if (worker.Parked || Interlocked.CompareExchange(ref worker.Idle, 0, 1) != 1) continue;
            worker.Signal.Set();
            return true;
        }

        return false;
    }

    public static void WakeBackground()
    {
        var room = BackgroundLimit - InBackground;
        if (room > 0 && Background is not null && !BackgroundOff) _ = Assert(Wake(room) <= room);
    }

    private static int Claim()
    {
        var ran = 0;
        for (var i = 0; i < MaxItems; i++)
        {
            var claim = Interlocked.Increment(ref _cursor) - 1;
            var (count, at) = ((int)(claim >>> 32), (int)(claim & Low));
            if (at >= count) break;
            Item(at, count);
            ran++;
        }

        return Assert(ran <= MaxItems) ? ran : 0;
    }

    private static void Item(int at, int count)
    {
        try
        {
            _job!(at);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = Interlocked.CompareExchange(ref _error, e, null);
        }

        if (Interlocked.Increment(ref _done) == count) Finished.Set();
        _ = Assert(at < count);
    }

    // A missing one would throw on a game thread
    private static bool Seams()
    {
        var main = typeof(ClientMain);
        return Assert(AccessTools.DeclaredField(main, "clientThreads")?.FieldType == typeof(List<Thread>)) &&
               Assert(AccessTools.DeclaredField(main, "_clientThreadsCts")?.FieldType ==
                      typeof(CancellationTokenSource));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "clientThreads")]
    private static extern ref List<Thread> Threads(ClientMain game);

    private sealed class Worker : IDisposable
    {
        public readonly ManualResetEventSlim Signal = new(false, SignalSpins);
        private readonly ClientMain? _game;
        private readonly int _slot, _era;
        public int Idle; // 1 while resting on Signal
        public volatile bool Parked;

        public Worker(ClientMain? game, int number, int generation)
        {
            (_game, _slot, _era) = (game, number, generation);
            Thread = new Thread(Loop) { IsBackground = true, Name = "komet-worker-" + (number + 1) };
            _ = Assert(number is >= 0 and < MaxThreads);
        }

        public Thread Thread { get; }

        public void Dispose()
        {
            _ = Assert(!Thread.IsAlive);
            Signal.Dispose();
        }

        private static bool Open()
        {
            var seen = Volatile.Read(ref _cursor);
            return Assert(seen >= 0) && (seen & Low) < seen >>> 32; // count <= MaxItems: the sign bit stays clear
        }

        private void Loop()
        {
            Number(_slot + 1);
            var token = _game is null ? CancellationToken.None : Cancel(_game)?.Token ?? CancellationToken.None;
            for (long round = 0; round < MaxRounds; round++)
            {
                if (Volatile.Read(ref _generation) != _era || token.IsCancellationRequested ||
                    _game?.threadsShouldExit == true) break;
                if (Parked || (!Help() && !Admit())) Rest();
            }

            Number(0);
            _ = Assert(Thread == Thread.CurrentThread) && Assert(Volatile.Read(ref Idle) == 0);
        }

        private void Rest()
        {
            _ = Interlocked.Exchange(ref Idle, 1);
            var woken = (Parked || !Open()) && Signal.Wait(IdleMs);
            _ = Interlocked.Exchange(ref Idle, 0);
            Signal.Reset(); // a late wake is not lost: the loop checks the cursor before resting again
            if (woken && !Parked) Relay();
            _ = Assert(Volatile.Read(ref Idle) == 0);
        }

        private static void Relay()
        {
            _ = Assert(_number > 0) && Assert(Volatile.Read(ref _relay) <= MaxThreads);
            for (var i = 0; i < MaxThreads; i++)
            {
                var left = Volatile.Read(ref _relay);
                if (left <= 0) return;
                if (Interlocked.CompareExchange(ref _relay, left - 1, left) != left) continue;
                if (WakeOne()) return; // the one woken relays on
                Volatile.Write(ref _relay, 0); // nobody rests any more
                return;
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clientThreadsCts")]
        private static extern ref CancellationTokenSource? Cancel(ClientMain game);

        private static void Number(int number)
        {
            _ = Assert(number is >= 0 and <= MaxThreads);
            _number = number;
        }

        private static bool Help()
        {
            if (!Assert(_number > 0) || !Open()) return false;
            var start = Counting.On ? Stopwatch.GetTimestamp() : 0;
            var ran = Claim();
            if (start != 0) Busy.Add(FrameCounter, Stopwatch.GetTimestamp() - start);
            return ran > 0;
        }

        private static bool Admit()
        {
            if (BackgroundOff || Background is not { } job) return false;
            if (Interlocked.Increment(ref _inBackground) > BackgroundLimit)
            {
                _ = Interlocked.Decrement(ref _inBackground);
                return false;
            }

            _ = Assert(InBackground <= MaxThreads);
            _ = Assert(_number > 0); // a pool thread's own
            var (start, ran) = (Counting.On ? Stopwatch.GetTimestamp() : 0, false);
            try
            {
                ran = job();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                (LastError, BackgroundOff) = (e, true);
            }
            finally
            {
                if (start != 0 && ran) Busy.Add(BackgroundCounter, Stopwatch.GetTimestamp() - start);
                _ = Assert(Interlocked.Decrement(ref _inBackground) >= 0);
            }

            return ran;
        }
    }
}
