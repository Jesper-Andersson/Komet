using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Shapes;

// GenerateAllFrames compiles lazily on the render thread whenever PrevNextKeyFrameByFrame is null; clones lack it and
// OnTesselation nulls it, so geared entities recompile on every re-tesselation (the engine's AnimationCache skips
// cloned shapes). This cache keys on what the compile reads: element names in visit order with depth, code,
// QuantityFrames, keyframes; identity-dependent shapes run uncached. Geometry stays out, so one outfit shares one set,
// nothing writes into a set. Hits compare the whole descriptor.
internal static partial class AnimationFrames
{
    internal const int MaxEntries = 512;
    private const int MaxKeyElements = 4096, MaxKeyFrames = 4096, MaxDescriptor = 1 << 22, MaxBucket = 64;
    private static readonly Lock Gate = new();
    private static readonly Dictionary<ulong, List<Entry>> Cache = [];

    private static readonly HashSet<object> Seen = new(ReferenceEqualityComparer.Instance);

    private static byte[] _buffer = new byte[4096];
    private static string[] _names = new string[64];
    private static int _at, _entries;
    private static readonly SeamGuard Guard = new(nameof(AnimationFrames), PatchKinds.All, [typeof(AnimationFrames)],
        unblocked: true);
    private static bool _installed;

    public static bool Enabled { get; set; } = true;

    // Another mod patches the compile, or the engine's body differs: the engine compiles everything
    public static bool Blocked => Guard.Blocked;

    public static bool Matched => Guard.Matched; // the engine's bodies are the ones reproduced

    // Only a rewritten body counts: a later skipping prefix, or a patch on a callee, is skipped whenever Komet answers
    internal static Action Recheck { get; } = static () => Guard.Report(Guard.Matched,
        Guard.Seams.Length == SeamCount && (EngineShape.Foreign(Guard.Seams.AsSpan(0, 1), PatchKinds.Body, null) ||
                                            EngineShape.Foreign(Guard.Seams.AsSpan(1), PatchKinds.All, null)));

    public static int Count => Assert(_entries <= MaxEntries) ? _entries : MaxEntries; // compiled sets kept

    public static long Hits { get; private set; } // totals while Counting.Hud, under Gate
    public static long Misses { get; private set; }
    public static double WorstMs { get; private set; } // the slowest compile on the render thread since ResetPeaks
    public static string WorstCode { get; private set; } = "";

    public static void ResetPeaks()
    {
        _ = Assert(Hits >= 0 && Misses >= 0) && Assert(WorstMs >= 0);
        (WorstMs, WorstCode) = (0, "");
    }

    // A compiled set belongs to one world's shapes; the descriptor holds names that world interned
    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
            Seen.Clear();
            (_at, _entries, _names) = (0, 0, new string[64]);
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jointsDone")]
    private static extern ref HashSet<int> JointsDone(Animation animation);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        Clear();
        ResetPeaks();
        _installed = false;
        var seams = Guard.Reset(Seams(), logger);
        var clone = AccessTools.Method(typeof(Animation), nameof(Animation.Clone), []);
        if (!NotNull(harmony) || !NotNull(clone) || !Assert(seams.Length == SeamCount)) return;
        // JointsDone throws otherwise
        if (Assert(AccessTools.Field(typeof(Animation), "jointsDone")?.FieldType == typeof(HashSet<int>)))
            _ = NotNull(harmony.Patch(clone, postfix: new HarmonyMethod(Cloned)));
        // the compile's accessors name fields of those bodies; on a changed engine they could throw
        if (!Guard.Match(Shaped(), fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Generate), new HarmonyMethod(Generated)));
        Recheck();
        _installed = WarmUp();
    }

    // Clones share jointsDone, which GenerateAllFrames clears per keyframe: a race once two threads compile
    private static void Cloned(Animation __result)
    {
        if (!NotNull(__result)) return;
        ref var done = ref JointsDone(__result);
        if (done is not null) done = [];
    }

    private static bool Generate(Animation __instance, ShapeElement[] rootElements,
        Dictionary<int, AnimationJoint> jointsById, bool recursive, out object? __state)
    {
        __state = null;
        var start = Counting.Hud && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId
            ? Stopwatch.GetTimestamp() : 0;
        Snapshot? key = null;
        if (Enabled && !Blocked && _installed && Compilable(__instance, rootElements, jointsById))
        {
            if (Hit(__instance, rootElements, recursive, ref key)) return false;
            if (Compile(__instance, rootElements, jointsById, recursive))
            {
                Keep(key, __instance.PrevNextKeyFrameByFrame);
                Record(start, __instance.Code);
                return false;
            }
        }

        if (key is not null || start > 0) __state = new Pending(key, start); // boxed only when the engine compiles
        return true;
    }

    private static void Generated(Animation __instance, object? __state)
    {
        if (__state is not Pending pending || !NotNull(__instance)) return;
        Keep(pending.Key, __instance.PrevNextKeyFrameByFrame);
        Record(pending.Start, __instance.Code);
    }

    // What the engine throws on (no keyframes, QuantityFrames, joint cap) it throws itself
    private static bool Compilable(Animation? animation, ShapeElement[]? roots, Dictionary<int, AnimationJoint>? joints)
    {
        if (animation?.KeyFrames is not { Length: > 0 } || animation.QuantityFrames <= 0 || roots is null ||
            joints is null) return false;
        return roots.Length <= ElementWalk.MaxElements && joints.Count < GlobalConstants.MaxAnimatedElements;
    }

    // On a miss key is the descriptor to store under, null if too large. A hit also runs the engine's lazy idempotent
    // CacheInverseTransformMatrixRecursive.
    private static bool Hit(Animation animation, ShapeElement[] roots, bool recursive, ref Snapshot? key)
    {
        if (!NotNull(animation) || !Assert(roots.Length <= ElementWalk.MaxElements)) return false;
        AnimationFrame[][]? found;
        lock (Gate)
        {
            if (!Describe(animation, roots, recursive)) return false;
            var hash = Hash();
            found = Find(hash, _buffer.AsSpan(0, _at));
            if (found is null) key = Take(hash);
            if (Counting.Hud) (Hits, Misses) = found is null ? (Hits, Misses + 1) : (Hits + 1, Misses);
        }

        if (found is null) return false;
        for (var i = 0; i < Math.Min(roots.Length, ElementWalk.MaxElements); i++)
            roots[i].CacheInverseTransformMatrixRecursive();
        animation.PrevNextKeyFrameByFrame = found;
        return true;
    }

    private static void Record(long start, string? code)
    {
        if (start <= 0) return;
        var ms = FrameClock.ToMs(Stopwatch.GetTimestamp() - start);
        if (Finite(ms) && ms > WorstMs) (WorstMs, WorstCode) = (ms, code ?? "");
    }

    private static void Keep(Snapshot? key, AnimationFrame[][]? frames)
    {
        if (key is not { } fresh || !NotNull(frames) || !Assert(frames.Length > 0)) return;
        lock (Gate) Store(fresh, frames);
    }

    private static bool Describe(Animation animation, ShapeElement[] roots, bool recursive)
    {
        _at = 0;
        Seen.Clear();
        if (!NotNull(animation) || !NotNull(roots)) return false;
        return Int(recursive ? 1 : 0) && Text(animation.Code) && Frames(animation) && Tree(roots, recursive);
    }

    private static bool Frames(Animation animation)
    {
        var keys = animation.KeyFrames;
        if (!NotNull(keys) || keys.Length > MaxKeyFrames) return false;
        if (!Int(animation.QuantityFrames) || !Int(keys.Length)) return false;
        for (var i = 0; i < Math.Min(keys.Length, MaxKeyFrames); i++)
            if (!Frame(keys[i])) return false;
        return true;
    }

    // Dictionary order is not content, so element names are sorted first
    private static bool Frame(AnimationKeyFrame? key)
    {
        if (!NotNull(key) || !Int(key.Frame)) return false;
        var elements = key.Elements;
        if (elements is null) return Int(-1);
        var count = elements.Count;
        if (!Int(count) || count > MaxKeyElements) return false;
        if (_names.Length < count) _names = new string[Math.Min(MaxKeyElements, Math.Max(count, 2 * _names.Length))];
        if (!Assert(_names.Length >= count)) return false;
        elements.Keys.CopyTo(_names, 0);
        Array.Sort(_names, 0, count, StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(count, MaxKeyElements); i++)
            if (!Text(_names[i]) || !elements.TryGetValue(_names[i], out var element) || !Element(element, key.Frame))
                return false;
        return true;
    }

    // Over-keying only costs a miss. The object is read too: left == right, and t from its Resolve-stamped Frame.
    private static bool Element(AnimationKeyFrameElement? element, int frame)
    {
        if (!NotNull(element) || !Seen.Add(element) || At(element) != frame) return false;
        if (!Num(element.OffsetX) || !Num(element.OffsetY) || !Num(element.OffsetZ)) return false;
        if (!Num(element.RotationX) || !Num(element.RotationY) || !Num(element.RotationZ)) return false;
        if (!Num(element.StretchX) || !Num(element.StretchY) || !Num(element.StretchZ)) return false;
        if (!Num(element.OriginX) || !Num(element.OriginY) || !Num(element.OriginZ)) return false;
        return Int(element.RotShortestDistanceX ? 1 : 0) && Int(element.RotShortestDistanceY ? 1 : 0) &&
               Int(element.RotShortestDistanceZ ? 1 : 0);
    }

    // Visit order with depth, so child and sibling differ; a twice-held element or subclass Equals is declined
    private static bool Tree(ShapeElement[] roots, bool recursive)
    {
        var walk = new ElementWalk(roots);
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element)) return !walk.Failed;
            if (element is null || element.GetType() != typeof(ShapeElement) || !Seen.Add(element)) return false;
            if (!Int(walk.Depth) || !Text(element.Name)) return false;
            if (!recursive) walk.Skip();
        }

        return !Assert(walk.Failed); // Next stops after MaxElements
    }

    private static bool Room(int bytes)
    {
        if (!Assert(bytes >= 0) || !Assert(_at >= 0)) return false;
        if (_at + bytes > MaxDescriptor) return false;
        if (_at + bytes <= _buffer.Length) return true;
        var room = Math.Min(MaxDescriptor, Math.Max(_at + bytes, 2 * _buffer.Length));
        if (!Assert(room >= _at + bytes)) return false;
        Array.Resize(ref _buffer, room);
        return true;
    }

    private static bool Int(int value)
    {
        if (!Room(sizeof(int)) || !Assert(_at + sizeof(int) <= _buffer.Length)) return false;
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_at), value);
        _at += sizeof(int);
        return true;
    }

    // Raw bits, so a NaN keyframe still equals itself
    private static bool Num(double? value)
    {
        if (!Int(value.HasValue ? 1 : 0) || !Assert(_at >= 0)) return false;
        if (!value.HasValue) return true;
        if (!Room(sizeof(long)) || !Assert(_at + sizeof(long) <= _buffer.Length)) return false;
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_at), BitConverter.DoubleToInt64Bits(value.Value));
        _at += sizeof(long);
        return true;
    }

    private static bool Text(string? value)
    {
        if (!Int(value?.Length ?? -1) || !Assert(_at >= 0)) return false;
        if (value is null || value.Length == 0) return true;
        var bytes = sizeof(char) * value.Length;
        if (!Room(bytes) || !Assert(_at + bytes <= _buffer.Length)) return false;
        MemoryMarshal.AsBytes(value.AsSpan()).CopyTo(_buffer.AsSpan(_at));
        _at += bytes;
        return true;
    }

    private static ulong Hash()
    {
        var hash = 14695981039346656037UL;
        if (!Assert(_at >= 0) || !Assert(_at <= _buffer.Length)) return hash;
        for (var i = 0; i < Math.Min(_at, MaxDescriptor); i++) hash = (hash ^ _buffer[i]) * 1099511628211UL;
        return hash;
    }

    // The compiled set stored under exactly these descriptor bytes, under Gate
    private static AnimationFrame[][]? Find(ulong hash, ReadOnlySpan<byte> bytes)
    {
        if (!Assert(bytes.Length <= MaxDescriptor) || !Assert(_entries <= MaxEntries)) return null;
        if (!Cache.TryGetValue(hash, out var bucket) || !NotNull(bucket)) return null;
        foreach (var entry in bucket.Bounded(MaxBucket))
            if (entry.Bytes.AsSpan().SequenceEqual(bytes)) return entry.Compiled;
        return null;
    }

    private static Snapshot Take(ulong hash) =>
        new(hash, Assert(_at >= 0) && Assert(_at <= _buffer.Length) ? _buffer.AsSpan(0, _at).ToArray() : []);

    // Past MaxEntries (each up to ~180 KiB) the cache starts over rather than grow
    private static void Store(Snapshot key, AnimationFrame[][] frames)
    {
        if (!NotNull(frames) || !NotNull(key.Bytes)) return;
        if (_entries >= MaxEntries)
        {
            Cache.Clear();
            _entries = 0;
        }

        // two threads missed on the same descriptor and both compiled
        if (Find(key.Code, key.Bytes) is not null) return;
        if (!Cache.TryGetValue(key.Code, out var bucket)) Cache[key.Code] = bucket = [];
        if (!Assert(bucket.Count < MaxBucket) || !Assert(_entries < MaxEntries)) return; // 64 under one 64-bit hash
        bucket.Add(new Entry(key.Bytes, frames));
        _entries++;
    }

    private readonly record struct Snapshot(ulong Code, byte[] Bytes);

    private readonly record struct Entry(byte[] Bytes, AnimationFrame[][] Compiled);

    // What a miss hands the postfix when the engine compiles: the descriptor and the start time
    private readonly record struct Pending(Snapshot? Key, long Start);
}
