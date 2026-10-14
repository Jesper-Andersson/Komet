using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Komet.Diagnostics;

// The engine's frame profiler (on for Komet's HUD) makes a new marks dictionary, a ProfileEntry per mark and a child
// range with its own dictionary every frame: 376 marks a frame were 12 % of all allocations in a steady-state trace, so
// a share of the GC pauses. The root's dictionaries alternate between two kept sets, their entries zeroed as the frame
// begins; a mark allocates only the first time its code is seen. The previous frame's set stays whole until the frame
// after it (FrameClock reads it at the frame's end).
internal static class ProfilerPool
{
    private const int MaxCodes = 4096, MaxChildren = 64;

    private sealed class Kept
    {
        public Dictionary<string, ProfileEntry> Marks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ProfileEntryRange> Children { get; } = new(StringComparer.Ordinal);
    }

    private static readonly Kept[] Sets = [new(), new()];
    private static long _frames;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }
    public static long Reused { get; private set; } // frames that began with a kept set

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentEntry")]
    private static extern ref ProfileEntryRange? Current(FrameProfilerUtil profiler);

    public static void Install(Harmony harmony)
    {
        Patched = false;
        var begin = AccessTools.DeclaredMethod(typeof(FrameProfilerUtil), nameof(FrameProfilerUtil.Begin), [typeof(string), typeof(object[])]);
        if (!NotNull(harmony) || !NotNull(begin)) return;
        Patched = NotNull(harmony.Patch(begin, postfix: new HarmonyMethod(Began)));
    }

    // After Begin made the frame's root: the root takes this frame's set, zeroed
    private static void Began(FrameProfilerUtil __instance)
    {
        if (!Enabled || !Patched || __instance is null || !__instance.Enabled || Current(__instance) is not { } root) return;
        if (root.Marks is not null || root.ChildRanges is not null) return; // not the fresh root Begin makes
        var set = Sets[(int)(++_frames & 1)];
        if (set.Marks.Count > MaxCodes || set.Children.Count > MaxChildren) return; // a flood of codes: the engine's way
        Zeroed(set.Marks);
        foreach (var child in set.Children.Values.Bounded(MaxChildren)) Rewound(child, root);
        (root.Marks, root.ChildRanges) = (set.Marks, set.Children);
        Reused++;
        _ = Assert(ReferenceEquals(root.Marks, set.Marks));
    }

    private static void Zeroed(Dictionary<string, ProfileEntry> marks)
    {
        _ = NotNull(marks) && Assert(marks.Count <= MaxCodes);
        foreach (var entry in marks.Values.Bounded(MaxCodes)) (entry.ElapsedTicks, entry.CallCount) = (0, 0);
    }

    // A kept child range under this frame's root, its counts and marks zeroed, its own children likewise (two levels:
    // the engine nests "behaviors" under the root and entity ticks under that)
    private static void Rewound(ProfileEntryRange child, ProfileEntryRange parent)
    {
        if (!NotNull(child) || !NotNull(parent)) return;
        (child.ParentRange, child.ElapsedTicks, child.CallCount) = (parent, 0, 0);
        if (child.Marks is { } marks) Zeroed(marks);
        if (child.ChildRanges is not { } children) return;
        foreach (var grandchild in children.Values.Bounded(MaxChildren))
        {
            (grandchild.ParentRange, grandchild.ElapsedTicks, grandchild.CallCount) = (child, 0, 0);
            if (grandchild.Marks is { } inner) Zeroed(inner);
            grandchild.ChildRanges = null; // deeper nesting starts anew
        }
    }
}
