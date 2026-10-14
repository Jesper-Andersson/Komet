using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Common;

namespace Komet.Diagnostics;

// /komet profile <modid>: times a mod's entry points (overrides, interface methods, lambdas, its patches) per
// main-thread frame, then unpatches. Patching is batched over frames; nothing is booked until all are in.
internal sealed partial class ModProfiler
{
    public const int MaxMethods = 1500, PatchPerFrame = 16, MaxTypes = 20000, MinIlBytes = 8, MaxSeries = 36000;
    public const string HarmonyId = "komet-profile";

    internal enum Phase { Patching, Measuring, Unpatching, Done }

    private static ModProfiler? _active; // the one the patches book to
    [ThreadStatic] private static long _child, _childAlloc;
    [ThreadStatic] private static int _childDraws;
    private readonly Harmony _harmony = new(HarmonyId);
    private readonly MethodInfo[] _methods;
    private readonly Dictionary<MethodBase, int> _slots;
    private readonly long[] _ticks, _self, _calls, _max, _mainCalls, _alloc, _draws, _peak, _frameSelf;
    private readonly int[] _touched;
    private readonly bool[] _touchedNow;
    private readonly float[] _series = new float[MaxSeries]; // the mod's main-thread ms in each measured frame
    private int _touchedCount;
    private long _allocStart, _allocEnd, _pauseStart, _pauseEnd;
    private (int Gen0, int Gen1, int Gen2) _gcStart, _gcEnd;
    private readonly int _mainThread = Environment.CurrentManagedThreadId;
    private readonly float _seconds;
    private int _next, _failed, _frames;
    private long _started, _ended;

    internal ModProfiler(string modId, string version, MethodInfo[] methods, float seconds)
    {
        (ModId, Version, _methods, _seconds) = (modId, version, methods, seconds);
        _slots = new Dictionary<MethodBase, int>(methods.Length);
        for (var i = 0; i < Math.Min(methods.Length, MaxMethods); i++) _slots[methods[i]] = i;
        (_ticks, _self, _calls, _max, _mainCalls) = (new long[methods.Length], new long[methods.Length], new long[methods.Length],
            new long[methods.Length], new long[methods.Length]);
        (_alloc, _draws, _peak, _frameSelf) = (new long[methods.Length], new long[methods.Length], new long[methods.Length],
            new long[methods.Length]);
        (_touched, _touchedNow) = (new int[methods.Length], new bool[methods.Length]);
        _ = Assert(_slots.Count == methods.Length) && Assert(seconds > 0);
    }

    public string ModId { get; }
    public string Version { get; }
    public Phase State { get; private set; }
    public int Methods => _methods.Length;
    public static bool Running => _active is not null;

    public static ModProfiler? Start(IModLoader loader, string modId, float seconds, out string error)
    {
        error = "";
        if (!NotNull(loader) || !Assert(seconds > 0)) return null;
        if (_active is not null) error = "running";
        else if (modId == KometModSystem.ModId) error = "self";
        else if (loader.GetMod(modId) is not ModContainer { Assembly: { } assembly } mod) error = "nocode";
        else
        {
            var methods = Candidates(assembly);
            if (methods.Length == 0) error = "nomethods";
            else return Begin(modId, mod.Info.Version, methods, seconds);
        }

        return null;
    }

    internal static ModProfiler? Begin(string modId, string version, MethodInfo[] methods, float seconds)
    {
        if (_active is not null || !Assert(methods.Length is > 0 and <= MaxMethods) || !Assert(seconds > 0)) return null;
        return _active = new ModProfiler(modId, version, methods, seconds);
    }

    public void Step()
    {
        if (!Assert(ReferenceEquals(_active, this)) || !Assert(_next <= _methods.Length)) return;
        if (State == Phase.Measuring)
        {
            Frame();
            if (FrameClock.ToMs(Stopwatch.GetTimestamp() - _started) < _seconds * 1000) return;
            (State, _ended, _next) = (Phase.Unpatching, Stopwatch.GetTimestamp(), 0);
            Totals(false);
            return;
        }

        var end = Math.Min(_next + PatchPerFrame, _methods.Length);
        for (var i = _next; i < Math.Min(end, MaxMethods); i++) Apply(_methods[i], State == Phase.Patching);
        _next = end;
        if (_next < _methods.Length) return;
        if (State != Phase.Patching) Finish();
        else
        {
            Totals(true);
            (State, _started) = (Phase.Measuring, Stopwatch.GetTimestamp());
        }
    }

    // Unpatches all this frame: the world may be closing.
    public void Stop()
    {
        if (!Assert(ReferenceEquals(_active, this)) || State == Phase.Done) return;
        if (State == Phase.Measuring)
        {
            _ended = Stopwatch.GetTimestamp();
            Totals(false);
        }

        // both at once: a postfix with __state but no prefix is invalid IL
        var (from, to) = State switch
        {
            Phase.Patching => (0, _next), // patched so far
            Phase.Unpatching => (_next, _methods.Length), // not unpatched yet
            _ => (0, _methods.Length)
        };
        for (var i = from; i < Math.Min(to, MaxMethods); i++) Apply(_methods[i], false);
        Finish();
        _ = Assert(State == Phase.Done);
    }

    private void Finish()
    {
        if (_ended == 0) _ended = Stopwatch.GetTimestamp();
        State = Phase.Done;
        Release(this);
        _ = Assert(_ended >= _started) && Assert(!Running);
    }

    private void Frame()
    {
        long sum = 0;
        for (var i = 0; i < Math.Min(_touchedCount, MaxMethods); i++)
        {
            var slot = _touched[i];
            sum += _frameSelf[slot];
            if (_frameSelf[slot] > _peak[slot]) _peak[slot] = _frameSelf[slot];
            (_frameSelf[slot], _touchedNow[slot]) = (0, false);
        }

        if (_frames < MaxSeries) _series[_frames] = (float)FrameClock.ToMs(sum);
        (_frames, _touchedCount) = (_frames + 1, 0);
        _ = Assert(sum >= 0);
    }

    private void Totals(bool start)
    {
        var (bytes, pause) = (GC.GetTotalAllocatedBytes(false), (long)GC.GetTotalPauseDuration().TotalMicroseconds);
        var counts = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        if (start) (_allocStart, _pauseStart, _gcStart) = (bytes, pause, counts);
        else (_allocEnd, _pauseEnd, _gcEnd) = (bytes, pause, counts);
        _ = Assert(bytes >= 0) && Assert(pause >= 0);
    }

    private static void Release(ModProfiler profile)
    {
        if (!NotNull(profile) || !Assert(ReferenceEquals(_active, profile))) return;
        _active = null;
    }

    private void Apply(MethodInfo method, bool patch)
    {
        if (!NotNull(method) || !Assert(_slots.ContainsKey(method))) return;
        try
        {
            if (patch && HookOf(_slots[method]) is var (prefix, postfix))
                _ = _harmony.Patch(method, new HarmonyMethod(prefix), new HarmonyMethod(postfix));
            else _harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _failed++; // a method Harmony cannot patch (extern, too small to detour) is left out
        }
    }

    private static readonly Type[] Digits = [typeof(D0), typeof(D1), typeof(D2), typeof(D3), typeof(D4), typeof(D5), typeof(D6), typeof(D7),
        typeof(D8), typeof(D9)];

    // Harmony's original-MethodBase lookup allocates per call (50 B booked to the mod), so each method gets a closed
    // Hook<,,,> whose digits are its slot; prefix and postfix share a class for __state.
    private static (MethodInfo Prefix, MethodInfo Postfix) HookOf(int slot)
    {
        _ = Assert(Index(slot, MaxMethods)) && Assert(MaxMethods <= 9999);
        var hook = typeof(Hook<,,,>).MakeGenericType(Digits[slot / 1000], Digits[slot / 100 % 10], Digits[slot / 10 % 10], Digits[slot % 10]);
        return (AccessTools.Method(hook, nameof(Hook<,,,>.Prefix)), AccessTools.Method(hook, nameof(Hook<,,,>.Postfix)));
    }

    internal interface IDigit
    {
        static abstract int Value { get; }
    }

    internal sealed class D0 : IDigit { public static int Value => 0; }
    internal sealed class D1 : IDigit { public static int Value => 1; }
    internal sealed class D2 : IDigit { public static int Value => 2; }
    internal sealed class D3 : IDigit { public static int Value => 3; }
    internal sealed class D4 : IDigit { public static int Value => 4; }
    internal sealed class D5 : IDigit { public static int Value => 5; }
    internal sealed class D6 : IDigit { public static int Value => 6; }
    internal sealed class D7 : IDigit { public static int Value => 7; }
    internal sealed class D8 : IDigit { public static int Value => 8; }
    internal sealed class D9 : IDigit { public static int Value => 9; }

    internal static class Hook<TA, TB, TC, TD> where TA : IDigit where TB : IDigit where TC : IDigit where TD : IDigit
    {
        private static readonly int Slot = TA.Value * 1000 + TB.Value * 100 + TC.Value * 10 + TD.Value;

        public static void Prefix(out Mark __state) => Enter(out __state);

        public static void Postfix(Mark __state) => Leave(__state, Slot);
    }

    internal readonly record struct Mark(long At, long Child, long Alloc, long ChildAlloc, int Draws, int ChildDraws);

    private static void Enter(out Mark __state)
    {
        __state = new Mark(Stopwatch.GetTimestamp(), _child, GC.GetAllocatedBytesForCurrentThread(), _childAlloc,
            RuntimeStats.drawCallsCount, _childDraws);
        (_child, _childAlloc, _childDraws) = (0, 0, 0);
    }

    // Self: this call minus profiled callees; the caller's children grow by the whole
    private static void Leave(Mark __state, int slot)
    {
        var (elapsed, allocated) = (Stopwatch.GetTimestamp() - __state.At, GC.GetAllocatedBytesForCurrentThread() - __state.Alloc);
        var drawn = unchecked(RuntimeStats.drawCallsCount - __state.Draws);
        var (self, selfAlloc, selfDraws) = (elapsed - _child, allocated - _childAlloc, drawn - _childDraws);
        (_child, _childAlloc, _childDraws) = (__state.Child + elapsed, __state.ChildAlloc + allocated, __state.ChildDraws + drawn);
        if (_active is not { State: Phase.Measuring } profile || !Index(slot, profile._methods.Length)) return;
        _ = Interlocked.Add(ref profile._ticks[slot], elapsed);
        _ = Interlocked.Add(ref profile._self[slot], Math.Max(0, self));
        _ = Interlocked.Add(ref profile._alloc[slot], Math.Max(0, selfAlloc));
        _ = Interlocked.Increment(ref profile._calls[slot]);
        if (elapsed > Volatile.Read(ref profile._max[slot])) Volatile.Write(ref profile._max[slot], elapsed); // a lost race loses a max
        if (Environment.CurrentManagedThreadId == profile._mainThread) profile.Main(slot, Math.Max(0, self), Math.Max(0, selfDraws));
    }

    private void Main(int slot, long self, int draws)
    {
        (_mainCalls[slot], _draws[slot], _frameSelf[slot]) = (_mainCalls[slot] + 1, _draws[slot] + draws, _frameSelf[slot] + self);
        if (_touchedNow[slot] || !Index(_touchedCount, _touched.Length)) return;
        (_touchedNow[slot], _touched[_touchedCount++]) = (true, slot);
    }
}
