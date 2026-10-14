using System.Collections.Concurrent;
using System.Diagnostics;
using Komet.Diagnostics;

namespace Komet.Api;

// Other mods' code in /komet debug; call only when IsModEnabled("komet"). Measure times a stretch on any thread (a
// field read outside a capture); dropped when the world closes.
public static class KometDebug
{
    public const int MaxTimings = 1024;
    private static readonly ConcurrentDictionary<string, Timing> Timings = new(StringComparer.Ordinal);

    // A capture runs: anything worth measuring only for the protocol can test this first
    public static bool Capturing { get; internal set; }

    // Books the stretch to name ("modid:what") on dispose; no-op outside a capture
    public static KometTiming Measure(string name) =>
        Capturing && !string.IsNullOrEmpty(name) && Assert(name.Length <= 128) ? new KometTiming(name, Stopwatch.GetTimestamp()) : default;

    internal static void Book(string name, long ticks)
    {
        if (!Assert(ticks >= 0)) return;
        if (!Timings.TryGetValue(name, out var timing))
        {
            if (Timings.Count >= MaxTimings) return; // Count takes every lock: asked only for a name not seen yet
            timing = Timings.GetOrAdd(name, static _ => new Timing());
        }

        _ = Interlocked.Add(ref timing.Ticks, ticks);
        _ = Assert(Interlocked.Increment(ref timing.Calls) > 0);
    }

    // Total ms and calls since the capture began, dearest first
    internal static (string Name, double Ms, long Calls)[] TakeTimings()
    {
        var taken = Timings.Select(static t => (t.Key, FrameClock.ToMs(Volatile.Read(ref t.Value.Ticks)), Volatile.Read(ref t.Value.Calls)))
            .OrderByDescending(static t => t.Item2).ToArray();
        Timings.Clear();
        return Assert(taken.Length <= MaxTimings + 64) && NotNull(taken) ? taken : [];
    }

    internal static void Forget()
    {
        Timings.Clear();
        Capturing = false;
        _ = Assert(Timings.IsEmpty) && Assert(!Capturing);
    }

    private sealed class Timing
    {
        public long Ticks, Calls;
    }
}

public readonly record struct KometTiming : IDisposable
{
    private readonly string? _name;
    private readonly long _start;

    internal KometTiming(string name, long start) => (_name, _start) = (name, start);

    public void Dispose()
    {
        if (_name is null || !Assert(_start > 0)) return;
        KometDebug.Book(_name, Stopwatch.GetTimestamp() - _start);
    }
}
