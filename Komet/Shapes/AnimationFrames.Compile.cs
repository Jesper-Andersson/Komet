using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using ResolvedTable = Vintagestory.API.Datastructures.FastSmallDictionary<Vintagestory.API.Common.ShapeElement,
    Vintagestory.API.Common.AnimationKeyFrameElement>;

namespace Komet.Shapes;

// The compile the cache cannot answer: GenerateAllFrames is O(K² · E · m) (keyframe seeks with linear element scans),
// this builds the same PrevNextKeyFrameByFrame in O(K · E), bit for bit (golden tests). Unverified or throwing inputs
// are declined to the engine. Writes only the result (one store) and inverse transforms; no statics, as the
// singleplayer server compiles too.
internal static partial class AnimationFrames
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xB8270BD36EBB9B8EUL;
    private const int MaxKeys = 4096, MaxPairs = 4096, MaxFrames = 1 << 16, Flags = 3, WarmUps = 3, SeamCount = 10;

    // A keyframe's resolved table and the Frame Resolve stamps on an element; ShapeInitMemo reads them too
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ElementsByShapeElement")]
    internal static extern ref IDictionary<ShapeElement, AnimationKeyFrameElement> Resolved(AnimationKeyFrame key);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Frame")]
    internal static extern ref int At(AnimationKeyFrameElement element);

    // Animation.GenerateAllFrames, or false where the engine must; a shape past a limit is declined, not cut short
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Compile(Animation animation, ShapeElement[]? roots,
        Dictionary<int, AnimationJoint>? jointsById, bool recursive = true)
    {
        if (!NotNull(animation) || roots is null || jointsById is null ||
            roots.Length > ElementWalk.MaxElements) return false;
        var keys = animation.KeyFrames;
        var q = animation.QuantityFrames;
        if (keys is null || keys.Length is 0 or > MaxKeys || q is <= 0 or > MaxFrames) return false;
        if (jointsById.Count >= GlobalConstants.MaxAnimatedElements) return false; // the engine throws with advice
        if (JointsDone(animation) is null) return false; // GenerateAllFrames clears it once per keyframe
        var tables = Tables(keys, q);
        if (tables is null || !Flatten(roots, recursive, out var order, out var parent)) return false;
        if (!Rows(tables, order, out var slotAt, out var rows) || !Sets(rows, keys.Length, out var sets)) return false;
        // Elements are known non-null here, so this throws only where the engine's first statement would
        for (var i = 0; i < Math.Min(roots.Length, ElementWalk.MaxElements); i++)
            roots[i].CacheInverseTransformMatrixRecursive();
        var frames = Poses(keys, order, parent, slotAt, rows, sets, q);
        if (!NotNull(frames) || !Assert(frames.Length == keys.Length)) return false;
        Volatile.Write(ref animation.PrevNextKeyFrameByFrame, Pairs(frames, q)); // published whole, unlike the engine's
        return true;
    }

    // Each keyframe's resolved table; null declines (frame past QuantityFrames, unresolved, not Resolve's table)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ResolvedTable[]? Tables(AnimationKeyFrame[] keys, int q)
    {
        if (!NotNull(keys) || !Assert(keys.Length <= MaxKeys)) return null;
        var tables = new ResolvedTable[keys.Length];
        for (var ki = 0; ki < Math.Min(keys.Length, MaxKeys); ki++)
        {
            var key = keys[ki];
            if (key is null || key.Frame >= q) return null;
            if (Resolved(key) is not ResolvedTable table || table.Count > MaxPairs) return null;
            tables[ki] = table;
        }

        return tables;
    }

    // GenerateFrame's visit order: parent[i] receives pose i, -1 for a root; the whole tree, as the engine's walk
    // throws on a null in it
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Flatten(ShapeElement[] roots, bool recursive, out ShapeElement[] order, out int[] parent)
    {
        (order, parent) = ([], []);
        if (!NotNull(roots) || !Assert(roots.Length <= ElementWalk.MaxElements)) return false;
        List<ShapeElement> elements = new(Math.Min(4 * roots.Length + 16, ElementWalk.MaxElements));
        List<int> owners = new(elements.Capacity);
        var walk = new ElementWalk(roots);
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element))
            {
                (order, parent) = ([.. elements], [.. owners]);
                return !walk.Failed && Assert(order.Length == parent.Length);
            }

            // FastSmallDictionary looks elements up by Equals, which a subclass could override
            if (element is null || element.GetType() != typeof(ShapeElement)) return false;
            // the unread model-matrix walk still runs for every parent GenerateFrame visits, and throws on these
            if (recursive && element.Children is not null &&
                (element.From is not { Length: >= 3 } || element.RotationOrigin is { Length: < 3 }))
                return false;
            if (!recursive && walk.Depth > 0) continue;
            elements.Add(element);
            owners.Add(walk.Parent);
        }

        return !Assert(walk.Failed); // Next stops after MaxElements
    }

    // Element → dense slot by reference; twice-held elements share one. rows[slot][keyframe] is GetKeyFrameElement's
    // first entry.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Rows(ResolvedTable[] tables, ShapeElement[] order, out int[] slotAt,
        out AnimationKeyFrameElement?[]?[] rows)
    {
        (slotAt, rows) = ([], []);
        if (!NotNull(tables) || !NotNull(order) || !Assert(order.Length <= ElementWalk.MaxElements)) return false;
        var slotOf = new Dictionary<ShapeElement, int>(order.Length, ReferenceEqualityComparer.Instance);
        slotAt = new int[order.Length];
        for (var i = 0; i < Math.Min(order.Length, ElementWalk.MaxElements); i++)
        {
            if (!slotOf.TryGetValue(order[i], out var slot)) slotOf[order[i]] = slot = slotOf.Count;
            slotAt[i] = slot;
        }

        var k = tables.Length;
        rows = new AnimationKeyFrameElement?[slotOf.Count][];
        for (var ki = 0; ki < Math.Min(k, MaxKeys); ki++)
            foreach (var (element, value) in tables[ki].Bounded(MaxPairs))
            {
                if (element is null || !slotOf.TryGetValue(element, out var slot)) continue;
                if (value is null) return false;
                var row = rows[slot] ??= new AnimationKeyFrameElement?[k];
                row[ki] ??= value;
            }

        return Assert(rows.Length <= order.Length);
    }

    // sets[slot * Flags + flag]: ascending keyframes with the flag set, null for none; a one-axis flag declines
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Sets(AnimationKeyFrameElement?[]?[] rows, int k, out int[]?[] sets)
    {
        sets = [];
        if (!NotNull(rows) || !Assert(k is > 0 and <= MaxKeys) || !Assert(rows.Length <= ElementWalk.MaxElements))
            return false;
        sets = new int[]?[rows.Length * Flags];
        var scratch = new int[k];
        for (var slot = 0; slot < Math.Min(rows.Length, ElementWalk.MaxElements); slot++)
        {
            var row = rows[slot];
            if (row is null) continue;
            for (var flag = 0; flag < Flags; flag++)
            {
                var n = 0;
                for (var ki = 0; ki < Math.Min(k, MaxKeys); ki++)
                {
                    var element = row[ki];
                    if (element is null || !IsSet(element, flag)) continue;
                    if (!Complete(element, flag)) return false;
                    scratch[n++] = ki;
                }

                sets[slot * Flags + flag] = n == 0 ? null : scratch.AsSpan(0, n).ToArray();
            }
        }

        return true;
    }

    // AnimationKeyFrameElement.IsSet for the three flags GenerateFrameForElement asks about
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSet(AnimationKeyFrameElement element, int flag) => NotNull(element) && Index(flag, Flags) &&
        flag switch { 0 => element.PositionSet, 1 => element.RotationSet, _ => element.StretchSet };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Complete(AnimationKeyFrameElement element, int flag) =>
        NotNull(element) && Index(flag, Flags) && flag switch
        {
            0 => element.OffsetX.HasValue && element.OffsetY.HasValue && element.OffsetZ.HasValue,
            1 => element.RotationX.HasValue && element.RotationY.HasValue && element.RotationZ.HasValue,
            _ => element.StretchX.HasValue && element.StretchY.HasValue && element.StretchZ.HasValue
        };

    // GenerateFrame per keyframe, flattened; pre-order adds each pose to its parent in the engine's Add order
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static AnimationFrame[] Poses(AnimationKeyFrame[] keys, ShapeElement[] order, int[] parent, int[] slotAt,
        AnimationKeyFrameElement?[]?[] rows, int[]?[] sets, int q)
    {
        if (!Assert(parent.Length == order.Length) || !Assert(slotAt.Length == order.Length)) return [];
        var frames = new AnimationFrame[keys.Length];
        var poses = new ElementPose[order.Length];
        for (var ki = 0; ki < Math.Min(keys.Length, MaxKeys); ki++)
        {
            var frame = frames[ki] = new AnimationFrame { FrameNumber = keys[ki].Frame };
            for (var i = 0; i < Math.Min(order.Length, ElementWalk.MaxElements); i++)
            {
                var pose = poses[i] = new ElementPose { ForElement = order[i] };
                var slot = slotAt[i];
                if (rows[slot] is { } row) Pose(pose, row, sets, slot * Flags, keys, frame.FrameNumber, q);
                (parent[i] < 0 ? frame.RootElementTransforms : poses[parent[i]].ChildElementPoses).Add(pose);
            }
        }

        return frames;
    }

    // GenerateFrameForElement for one element and keyframe, seeks answered from the set
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Pose(ElementPose pose, AnimationKeyFrameElement?[] row, int[]?[] sets, int at,
        AnimationKeyFrame[] keys, int frameNumber, int q)
    {
        if (!Assert(row.Length == keys.Length) || !Index(at + Flags - 1, sets.Length)) return;
        for (var flag = 0; flag < Flags; flag++)
        {
            var set = sets[at + flag];
            if (set is null) continue;
            // seekRightKeyFrame: the first set keyframe past the frame, else the first set keyframe at all
            var right = 0;
            for (var j = 0; j < Math.Min(set.Length, MaxKeys); j++)
            {
                if (keys[set[j]].Frame <= frameNumber) continue;
                right = j;
                break;
            }

            // seekLeftKeyFrame: the set keyframe before it, cyclically (right itself when it is the only one)
            var next = row[set[right]]!;
            var prev = row[set[right == 0 ? set.Length - 1 : right - 1]]!;
            Lerp(pose, prev, next, flag, Progress(prev, next, frameNumber, q));
            pose.RotShortestDistanceX = prev.RotShortestDistanceX;
            pose.RotShortestDistanceY = prev.RotShortestDistanceY;
            pose.RotShortestDistanceZ = prev.RotShortestDistanceZ;
        }
    }

    // t from the frames Resolve stamped on the elements (not the keyframes'); division by zero included
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Progress(AnimationKeyFrameElement prev, AnimationKeyFrameElement next, int frameNumber, int q)
    {
        if (ReferenceEquals(prev, next)) return 0f;
        if (!Assert(q > 0) || !Assert(frameNumber < q)) return 0f; // Compile declines both
        int left = At(prev), right = At(next);
        if (right >= left) return (float)(frameNumber - left) / (right - left);
        return (float)GameMath.Mod(frameNumber - left, q) / (right + (q - left));
    }

    // lerpKeyFrameElement expression for expression: same casts, division by 16, GameMath.Lerp
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Lerp(ElementPose pose, AnimationKeyFrameElement prev, AnimationKeyFrameElement next, int flag,
        float t)
    {
        if (!Index(flag, Flags) || !NotNull(prev) || !NotNull(next)) return;
        switch (flag)
        {
            case 0:
                pose.translateX = GameMath.Lerp((float)prev.OffsetX!.Value / 16f, (float)next.OffsetX!.Value / 16f, t);
                pose.translateY = GameMath.Lerp((float)prev.OffsetY!.Value / 16f, (float)next.OffsetY!.Value / 16f, t);
                pose.translateZ = GameMath.Lerp((float)prev.OffsetZ!.Value / 16f, (float)next.OffsetZ!.Value / 16f, t);
                break;
            case 1:
                pose.degX = GameMath.Lerp((float)prev.RotationX!.Value, (float)next.RotationX!.Value, t);
                pose.degY = GameMath.Lerp((float)prev.RotationY!.Value, (float)next.RotationY!.Value, t);
                pose.degZ = GameMath.Lerp((float)prev.RotationZ!.Value, (float)next.RotationZ!.Value, t);
                break;
            default:
                pose.scaleX = GameMath.Lerp((float)prev.StretchX!.Value, (float)next.StretchX!.Value, t);
                pose.scaleY = GameMath.Lerp((float)prev.StretchY!.Value, (float)next.StretchY!.Value, t);
                pose.scaleZ = GameMath.Lerp((float)prev.StretchZ!.Value, (float)next.StretchZ!.Value, t);
                break;
        }
    }

    // getLeftRightResolvedFrame in closed form: left the last FrameNumber at most the frame, right the next cyclically
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static AnimationFrame[][] Pairs(AnimationFrame[] frames, int q)
    {
        var k = frames.Length;
        if (!Assert(k is > 0 and <= MaxKeys) || !Assert(q is > 0 and <= MaxFrames)) return [];
        var result = new AnimationFrame[q][];
        for (var frame = 0; frame < Math.Min(q, MaxFrames); frame++)
        {
            var left = -1;
            for (var i = 0; i < Math.Min(k, MaxKeys); i++)
                if (frames[i].FrameNumber <= frame) left = i;
            result[frame] = left < 0 ? [frames[k - 1], frames[0]] : [frames[left], frames[(left + 1) % k]];
        }

        return result;
    }

    // Everything the compile runs through, [0] patched; lookups name verified signatures, so a change is a missing seam
    private static MethodBase?[] Seams()
    {
        Type pose = typeof(ElementPose).MakeByRefType(), element = typeof(AnimationKeyFrameElement).MakeByRefType();
        var frame = typeof(AnimationFrame).MakeByRefType();
        Type[] seek = [typeof(int), typeof(ShapeElement), typeof(int)];
        MethodBase?[] seams =
        [
            AccessTools.Method(typeof(Animation), nameof(Animation.GenerateAllFrames),
                [typeof(ShapeElement[]), typeof(Dictionary<int, AnimationJoint>), typeof(bool)]),
            AccessTools.Method(typeof(Animation), "GenerateFrame",
            [
                typeof(int), typeof(AnimationFrame[]), typeof(ShapeElement[]), typeof(Dictionary<int, AnimationJoint>),
                typeof(float[]), typeof(List<ElementPose>), typeof(bool)
            ]),
            AccessTools.Method(typeof(Animation), "GenerateFrameForElement", [typeof(int), typeof(ShapeElement), pose]),
            AccessTools.Method(typeof(Animation), "lerpKeyFrameElement",
                [typeof(AnimationKeyFrameElement), typeof(AnimationKeyFrameElement), typeof(int), typeof(float), pose]),
            AccessTools.Method(typeof(Animation), "getTwoKeyFramesElementForFlag",
                [typeof(int), typeof(ShapeElement), typeof(int), element, element]),
            AccessTools.Method(typeof(Animation), "seekRightKeyFrame", seek),
            AccessTools.Method(typeof(Animation), "seekLeftKeyFrame", seek),
            AccessTools.Method(typeof(Animation), "getLeftRightResolvedFrame",
                [typeof(int), typeof(AnimationFrame[]), frame, frame]),
            AccessTools.Method(typeof(AnimationKeyFrame), "GetKeyFrameElement", [typeof(ShapeElement)]),
            AccessTools.Method(typeof(AnimationKeyFrameElement), "IsSet", [typeof(int)])
        ];
        return Assert(seams.Length == SeamCount) ? seams : [];
    }

    // The bodies reproduced: the seams and the table lookup GetKeyFrameElement delegates to
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped = [.. Seams(), AccessTools.Method(typeof(ResolvedTable), "TryGetValue",
            [typeof(ShapeElement), typeof(AnimationKeyFrameElement).MakeByRefType()])];
        return Assert(shaped.Length <= EngineShape.MaxMethods) ? shaped : [];
    }

    // Three public-API compiles JIT the [AggressiveOptimization] code before the first animating frame
    private static bool WarmUp()
    {
        var (parent, child) = WarmTree();
        var byName = new Dictionary<string, ShapeElement> { ["komet-parent"] = parent, ["komet-child"] = child };
        var animation = new Animation
        { Code = "komet-warmup", QuantityFrames = 4, KeyFrames = [WarmKey(0, 10), WarmKey(2, -10)] };
        foreach (var key in animation.KeyFrames.Bounded(MaxKeys)) key.Resolve(byName);
        var compiled = true;
        for (var i = 0; i < WarmUps; i++) compiled &= Compile(animation, [parent], []);
        return Assert(compiled) && NotNull(animation.PrevNextKeyFrameByFrame) &&
               Assert(animation.PrevNextKeyFrameByFrame.Length == 4);
    }

    // The two-element warm-up tree (ShapeInitMemo's too), linked as ResolveReferences links them
    internal static (ShapeElement Parent, ShapeElement Child) WarmTree()
    {
        var child = new ShapeElement
        { Name = "komet-child", From = [1, 0, 0], To = [2, 1, 1], RotationOrigin = [1, 0, 0] };
        var parent = new ShapeElement
        { Name = "komet-parent", From = [0, 0, 0], To = [1, 1, 1], RotationOrigin = [0, 0, 0], Children = [child] };
        child.ParentElement = parent;
        _ = Assert(parent.Children.Length == 1) && Assert(ReferenceEquals(child.ParentElement, parent));
        return (parent, child);
    }

    private static AnimationKeyFrame WarmKey(int frame, double degrees)
    {
        _ = Assert(frame >= 0) && Finite(degrees);
        return new AnimationKeyFrame
        {
            Frame = frame,
            Elements = new Dictionary<string, AnimationKeyFrameElement>
            { ["komet-parent"] = Moved(degrees), ["komet-child"] = Moved(-degrees) }
        };
    }

    private static AnimationKeyFrameElement Moved(double degrees)
    {
        _ = Assert(Math.Abs(degrees) <= 360) && Finite(degrees);
        return new AnimationKeyFrameElement
        {
            OffsetX = 0, OffsetY = degrees / 10, OffsetZ = 0, RotationX = degrees, RotationY = 0, RotationZ = -degrees,
            StretchX = 1, StretchY = 1, StretchZ = 1, RotShortestDistanceY = true
        };
    }
}
