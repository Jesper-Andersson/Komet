using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Common.Entities;

namespace Komet.Shapes;

// The local player's shape is inited twice per re-tesselation (Entity.OnTesselation's LoadAnimator, then EntityPlayer's
// OtherAnimManager.LoadAnimator on the same shape), with only PlayerHeadController's pose lookups between. The second
// recomputes what the first left, so it is skipped for the same shape, joints, disable list and codes (AnimatorBase
// lower-cases codes in between).
internal static class InitOnce
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x72F1B0A0AD48C62BUL;
    private const int MaxNames = 256, MaxAnimations = 4096, SeamCount = 18;

    [ThreadStatic] private static EntityPlayer? _player;
    [ThreadStatic] private static Shape? _shape;
    [ThreadStatic] private static string[]? _joints, _disabled;

    // The remembered init's animations and their codes as seen
    [ThreadStatic] private static Animation[]? _animations;
    [ThreadStatic] private static string?[]? _codes;
    [ThreadStatic] private static int _depth;

    // Base has returned: the next init on the remembered shape is the duplicate
    [ThreadStatic] private static bool _armed;

    private static readonly SeamGuard Guard = new(nameof(InitOnce), PatchKinds.All, [typeof(InitOnce), typeof(ShapeInitMemo)],
        unblocked: true);
    private static bool _installed;

    public static bool Enabled { get; set; } = true;

    // Another mod patches what runs between or inside the two inits, or the engine differs: both run
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched; // the engine's bodies are the ones verified

    // ShapeInitMemo owns the patch on [2], InitForAnimations; Features.Recheck asks again on LevelFinalize.
    internal static Action Recheck { get; } = static () => Guard.Recheck();

    public static long Skipped { get; private set; } // a total while Counting.Hud, main thread

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        _installed = false;
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == SeamCount) || !Guard.Match(Shaped(), fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Open), finalizer: new HarmonyMethod(Close)));
        _ = NotNull(harmony.Patch(seams[1], postfix: new HarmonyMethod(Based)));
        Recheck();
        _installed = true;
    }

    // [0]-[2] patched, [3]-[8] run between the inits, [9]-[17] inside the skipped one (Inside()); declared, named
    // lookups
    private static MethodBase?[] Seams()
    {
        var shapeRef = typeof(Shape).MakeByRefType();
        Type[] init = [typeof(ICoreAPI), typeof(Entity)];
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(EntityPlayer), nameof(EntityPlayer.OnTesselation),
                [shapeRef, typeof(string)]),
            AccessTools.DeclaredMethod(typeof(Entity), nameof(Entity.OnTesselation), [shapeRef, typeof(string)]),
            AccessTools.Method(typeof(Shape), nameof(Shape.InitForAnimations),
                [typeof(ILogger), typeof(string), typeof(string[]), typeof(string[])]),
            AccessTools.DeclaredMethod(typeof(Entity), "OnTesselation",
                [shapeRef, typeof(string), typeof(bool).MakeByRefType()]),
            AccessTools.DeclaredMethod(typeof(AnimationManager), nameof(AnimationManager.LoadAnimator),
            [
                typeof(ICoreAPI), typeof(Entity), typeof(Shape), typeof(RunningAnimation[]), typeof(bool),
                typeof(string[]), typeof(string[])
            ]),
            AccessTools.DeclaredMethod(typeof(AnimationManager), nameof(AnimationManager.Init), init),
            AccessTools.DeclaredMethod(typeof(PlayerAnimationManager), nameof(PlayerAnimationManager.Init), init),
            AccessTools.Constructor(typeof(PlayerHeadController),
                [typeof(IAnimationManager), typeof(EntityPlayer), typeof(Shape)]),
            AccessTools.Constructor(typeof(EntityHeadController),
                [typeof(IAnimationManager), typeof(EntityAgent), typeof(Shape)]),
            .. ShapeInitMemo.Inside()
        ];
        return Assert(seams.Length == SeamCount) &&
               Assert(seams[0] is null || seams[0]!.DeclaringType == typeof(EntityPlayer)) ? seams : [];
    }

    // The proof's bodies: the seams and AnimatorBase's constructor, which lower-cases codes in between
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped = [.. Seams(), AccessTools.Constructor(typeof(AnimatorBase),
            [typeof(WalkSpeedSupplierDelegate), typeof(Animation[]), typeof(Action<string>)])];
        return Assert(shaped.Length <= EngineShape.MaxMethods) ? shaped : [];
    }

    internal static void Open(EntityPlayer __instance)
    {
        if (!NotNull(__instance) || !Assert(_depth >= 0)) return;
        if (++_depth > 1)
        {
            // a nested tesselation is not the known sequence: nothing more is skipped until it closes
            (_player, _shape, _armed) = (null, null, false);
            return;
        }

        // Only EntityPlayer itself: a subclass's OnTesselation may change the shape after base's init
        var player = __instance.GetType() == typeof(EntityPlayer) ? __instance : null;
        (_player, _shape, _joints, _disabled, _armed, _animations, _codes) =
            (player, null, null, null, false, null, null);
    }

    // Harmony finalizer: runs however the method leaves; a void finalizer rethrows untouched
    internal static void Close()
    {
        if (!Assert(_depth > 0)) return; // Open ran first
        if (--_depth > 0) return;
        _ = Assert(!_armed || _shape is not null); // armed only ever with a remembered shape
        (_player, _shape, _joints, _disabled, _armed, _animations, _codes) =
            (null, null, null, null, false, null, null);
    }

    // Entity.OnTesselation returned into EntityPlayer's: the init it ran last is the one the second would repeat
    internal static void Based(Entity __instance)
    {
        if (_depth != 1 || !NotNull(__instance) || !ReferenceEquals(__instance, _player)) return;
        if (_armed) (_armed, _shape) = (false, null); // base ran twice: not the sequence this knows
        else _armed = _shape is not null && Assert(_joints is null || _joints.Length <= MaxNames);
    }

    // Asked first by ShapeInitMemo's prefix: true skips this init as the duplicate of base's
    internal static bool Duplicate(Shape shape, string[]? disable, string[]? joints)
    {
        if (!_armed)
        {
            // An init inside base begins: nothing is remembered until Completed, so a throw cannot leave an older one
            // to repeat
            if (_depth == 1) _shape = null;
            return false;
        }

        if (!Enabled || Blocked || !_installed || !NotNull(shape) || !Assert(_depth == 1)) return false;
        if (!ReferenceEquals(shape, _shape) || !Same(joints, _joints)) return false;
        if (disable is not null && !Same(disable, _disabled)) return false;
        if (!Unchanged(shape.Animations)) return false;
        if (Counting.Hud) Skipped++;
        return true;
    }

    // And told by its postfix about every init that completed, skipped ones included
    internal static void Completed(Shape shape, string[]? disable, string[]? joints)
    {
        if (_depth != 1 || _player is null || !NotNull(shape)) return;
        if (_armed)
        {
            (_armed, _shape) = (false, null); // one skip per window: a third init runs whatever it is
            return;
        }

        (_shape, _joints, _disabled) = (shape, Copy(joints), Copy(disable));
        (_animations, _codes) = Codes(shape.Animations);
        _ = Assert(_shape is not null);
    }

    // The animations and codes as the completed init saw them; (null, null) for none or too many
    private static (Animation[]?, string?[]?) Codes(Animation[]? animations)
    {
        if (animations is null || animations.Length > MaxAnimations) return (null, null);
        var (copy, codes) = (new Animation[animations.Length], new string?[animations.Length]);
        for (var i = 0; i < Math.Min(animations.Length, MaxAnimations); i++)
            (copy[i], codes[i]) = (animations[i], animations[i]?.Code);
        return Assert(copy.Length == codes.Length) && Assert(codes.Length == animations.Length)
            ? (copy, codes) : (null, null);
    }

    // Same animations and codes: CollectAndResolveReferences writes AnimationsByCrc32 as before
    private static bool Unchanged(Animation[]? animations)
    {
        if (animations is null || _animations is null || _codes is null) return false;
        if (!Assert(_codes.Length == _animations.Length) || animations.Length != _animations.Length) return false;
        if (!Assert(animations.Length <= MaxAnimations)) return false; // Codes remembers no more
        for (var i = 0; i < Math.Min(animations.Length, MaxAnimations); i++)
            if (!ReferenceEquals(animations[i], _animations[i]) ||
                !string.Equals(animations[i]?.Code, _codes[i], StringComparison.Ordinal)) return false;
        return true;
    }

    private static string[]? Copy(string[]? names)
    {
        if (names is null || !Assert(names.Length <= MaxNames)) return null;
        string[] copy = [.. names];
        return Assert(copy.Length == names.Length) ? copy : null;
    }

    private static bool Same(string[]? names, string[]? remembered)
    {
        if (names is null || remembered is null) return names is null && remembered is null;
        return Assert(remembered.Length <= MaxNames) && names.AsSpan().SequenceEqual(remembered);
    }
}
