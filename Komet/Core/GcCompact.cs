using System.Runtime;
using Vintagestory.Client;

namespace Komet.Core;

// SustainedLowLatency never compacts the LOH (dump: 2.9 GB free beside 4.8 GB live). A compaction stops all threads
// (2.75 s for 5.2 GB): only while paused or unfocused, over MinFragmentedMb, at most every CooldownMs; returns memory.
internal static class GcCompact
{
    private const long UnfocusedMs = 10_000, CooldownMs = 120_000, MinFragmentedMb = 1024;
    private const int CheckMs = 2000;

    private static ICoreClientAPI? _api;
    private static long _listener, _unfocusedSince = -1, _last = -CooldownMs;

    public static bool Enabled { get; set; } = true;

    public static void Install(ICoreClientAPI api)
    {
        if (!NotNull(api) || _listener != 0) return;
        _api = api;
        _listener = api.Event.RegisterGameTickListener(Check, CheckMs);
        _ = Assert(_listener != 0) && Assert(CheckMs > 0);
    }

    public static void Stop()
    {
        if (_api is { } api && _listener != 0) api.Event.UnregisterGameTickListener(_listener);
        (_api, _listener, _unfocusedSince) = (null, 0, -1);
        _ = Assert(_last >= -CooldownMs);
    }

    private static readonly Action<int, GCCollectionMode, bool, bool> Collect = GC.Collect;

    private static void Check(float dt)
    {
        if (!Enabled || _api is not { } api || !Assert(dt >= 0)) return;
        var now = Environment.TickCount64;
        var focused = ScreenManager.Platform?.IsFocused ?? true;
        if (focused) _unfocusedSince = -1;
        else if (_unfocusedSince < 0) _unfocusedSince = now;
        var idle = api.IsGamePaused || (_unfocusedSince >= 0 && now - _unfocusedSince >= UnfocusedMs);
        if (!idle || now - _last < CooldownMs) return;
        var info = GC.GetGCMemoryInfo();
        var fragmented = info.FragmentedBytes >> 20;
        if (fragmented < MinFragmentedMb) return;
        _last = now;
        _ = Assert(fragmented >= MinFragmentedMb) && Assert(_last > 0);
        var before = info.TotalCommittedBytes;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true); // the point here: nobody plays meanwhile
        var pauseMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var freed = Math.Max(0, before - GC.GetGCMemoryInfo().TotalCommittedBytes) >> 20;
        api.Logger.Notification("Komet: the managed heap compacted while {0}: {1} MB were fragmented, {2} MB given back, {3:0} ms",
            api.IsGamePaused ? "paused" : "out of focus", fragmented, freed, pauseMs);
    }
}
