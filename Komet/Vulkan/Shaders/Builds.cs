using System.Diagnostics;

namespace Komet.Vulkan;

// Ports and pipelines build off the engine's thread (each up to tens of ms); draws stay OpenGL's until done. At most
// Workers at once. Gate guards Running and totals, each Build its result.
internal static class Builds
{
    public enum Kind
    {
        Port,
        Pipeline
    }

    private const int Workers = 2, Kinds = 2, MaxRunning = 1 << 16;

    // A driver taking longer is hung
    public const int LongestSeconds = 30;
    private static readonly TaskScheduler Scheduler =
        new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Workers).ConcurrentScheduler;
    private static readonly Lock Gate = new();
    private static readonly HashSet<Task> Running = [];
    private static readonly Tally Tallied = new(["ports", "pipelines"]);

    // Tests: each build is waited for, so a frame draws as if made in place
    public static bool Waited { get; set; }

    private static long _started;

    public static long Started
    {
        get
        {
            lock (Gate) return _started;
        }
    }

    // Listed before it starts, so its end never precedes the listing
    public static Task Run(Action build)
    {
        var task = new Task(build);
        lock (Gate)
        {
            _ = NotNull(build) && Assert(Running.Count < MaxRunning);
            _ = Running.Add(task);
            _started++;
        }

        _ = task.ContinueWith(static done =>
        {
            lock (Gate) _ = Running.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        task.Start(Scheduler);
        return task;
    }

    // All builds done (or timeout): the device goes only after them, tests draw after them
    public static bool Wait(TimeSpan timeout)
    {
        Task[] running;
        lock (Gate) running = [.. Running];
        _ = Assert(running.Length < MaxRunning) && Assert(timeout >= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan);
        return Task.WaitAll(running, timeout);
    }

    public static int Pending
    {
        get
        {
            lock (Gate) return Running.Count;
        }
    }

    public static void Count(Kind kind, long ticks)
    {
        var i = (int)kind;
        if (!Index(i, Kinds) || !Assert(ticks >= 0)) return;
        lock (Gate) Tallied.Add(i, ticks);
    }

    public static string Report()
    {
        lock (Gate) return Tallied.Report("made off the engine's thread");
    }
}

// One result, handed over once under _gate. Dropped before taken: freed by whichever comes second (worker or Drop),
// never twice. Dropped before start: nothing made. make reads only captured copies or immutable objects.
internal sealed class Build<T> where T : class
{
    private readonly Lock _gate = new();
    private readonly Action<T>? _free;
    private readonly Task _task;
    private T? _made;
    private string _why = "";
    private bool _dropped, _taken;

    public Build(Builds.Kind kind, Func<(T? Made, string Why)> make, Action<T>? free = null)
    {
        _ = NotNull(make) && Assert(kind <= Builds.Kind.Pipeline);
        _free = free;
        _task = Builds.Run(() => Made(kind, make));
        if (Builds.Waited) _task.Wait();
    }

    public bool Done => _task.IsCompleted;

    public long Ticks { get; private set; }

    private void Made(Builds.Kind kind, Func<(T? Made, string Why)> make)
    {
        lock (_gate)
            if (_dropped)
                return;
        var start = Stopwatch.GetTimestamp();
        T? made = null;
        string why;
        try
        {
            (made, why) = make();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            why = $"{e.GetType().Name}: {e.Message}";
        }

        var ticks = Stopwatch.GetTimestamp() - start;
        Builds.Count(kind, ticks);
        lock (_gate)
            if (!_dropped)
            {
                (_made, _why, Ticks) = (made, why, ticks);
                return;
            }

        if (made is not null) _free?.Invoke(made);
        _ = Assert(ticks >= 0);
    }

    // Once Done: the result, now the caller's; null with why
    public T? Take(out string why)
    {
        lock (_gate)
        {
            _ = Assert(_task.IsCompleted) && Assert(!_taken && !_dropped);
            var made = _made;
            (why, _made, _taken) = (_made is null && _why.Length == 0 ? "nothing made" : _why, null, true);
            return made;
        }
    }

    // Not begun: never begins; made and not taken: freed
    public void Drop()
    {
        T? made;
        lock (_gate)
        {
            (made, _made, _dropped) = (_made, null, true);
        }

        if (made is not null) _free?.Invoke(made);
        _ = Assert(_made is null);
    }

    // For where leaving the draws to OpenGL is no option
    public bool Wait(TimeSpan timeout) => Assert(timeout > TimeSpan.Zero) && NotNull(_task) && _task.Wait(timeout);

    // Waits as long as a build may take, booked as a stutter of its kind
    public bool Awaited(Hitches.Kind kind)
    {
        if (Done || !Assert(kind is Hitches.Kind.Port or Hitches.Kind.Pipeline)) return Done;
        var at = Hitches.Now;
        _ = Wait(TimeSpan.FromSeconds(Builds.LongestSeconds));
        Hitches.Since(kind, at);
        return Assert(at > 0) && Done;
    }
}
