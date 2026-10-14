using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Frame decides per call, so each idle entry still costs two or three dependent cache misses, ~12,900 times a frame in
// the user's base, all but ~90 idle. Here ShadowFar, ShadowNear, Opaque and OIT are polled once at stage start: parked
// objects lie in dense prefetched arrays, answering into one byte per list entry the transpiled loops read before the
// handler (Skip). Arrays rebuild when the list or its _version changed, at most once per stage and frame. Parked: only
// BlockEntityAnimationUtil (exact type) and the AnimatableRenderers it owns; vanilla never wakes them mid-stage
// (MarkBlockDirty callbacks run in Before; StartAnimation hooked by Woken), whereas GearRenderer toggles its tripod in
// Opaque. Plain AnimationUtils and mod renderers stay with Frame. A list changed mid-stage makes Skip answer no for the
// rest. A util holds its BlockEntity, so an unpolled parking (knob off, stage no longer triggered) would keep entities
// past their chunks: Release drops it, the owned set with Opaque's.
internal static partial class IdleAnimators
{
    private const int Stages = 4, OpaqueSlot = 2, MaxParked = 1 << 16;

    // Prefetch distances (rebuild, poll): object Lead entries ahead, its referents Parts (unmeasured; beyond Ahead's)
    private const int Lead = 24, Parts = 12;

    private static readonly Parking[] Parkings = [new(), new(), new(), new()]; // by slot, main thread only

    // Renderers of Opaque's parked utils at its last rebuild; _owners counts changes
    private static HashSet<AnimatableRenderer> _owned = new(ReferenceEqualityComparer.Instance),
        _spare = new(ReferenceEqualityComparer.Instance);

    private static Type? _utilType;
    private static MethodBase? _trigger; // ClientEventManager.TriggerRenderStage
    private static Parking? _current; // the stage being dispatched, when it parks anything
    private static List<RenderHandler>? _list; // its list, its bytes and the list's _version they were polled for
    private static byte[] _bits = [];
    private static int _edits, _owners, _frame;
    private static bool _parks, _crossed; // the last: another mod's transpiler on the loop (Recheck)

    internal static bool Parks => _parks; // both loops of TriggerRenderStage ask Skip

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    private static extern ref int Edits(List<RenderHandler> list);

    // First question of the transpiled loops and ModTimes' replay: entry found idle at stage start. Another list (a
    // nested stage polled its own) answers no.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Skip(List<RenderHandler> list, int i)
    {
        var bits = _bits;
        if ((uint)i >= (uint)bits.Length || bits[i] == 0 || !ReferenceEquals(list, _list) || Edits(list) != _edits)
            return false;
        if (Counting.Hud) Skipped++;
        return true;
    }

    // StartAnimation: a util started mid-stage after the poll runs from here, its renderer asked again (draws after
    // MarkBlockDirty's callback in a later Before). The next stage polls anew.
    internal static void Woken(AnimationUtil __instance, bool __result)
    {
        if (!__result || _current is not { } parking || !NotNull(__instance) || __instance.GetType() != _utilType ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;
        _ = Assert(parking.Unpark(__instance, __instance.renderer) <= 2); // each registers once per stage
    }

    // Leaves no engine object behind (Install; Stop, before the world goes)
    internal static void Clear()
    {
        (_walked, _list, _current, _owners, _frame) = (null, null, null, 0, 0);
        _bits = [];
        _owned.Clear();
        _spare.Clear();
        for (var i = 0; i < Stages; i++) Parkings[i] = new();
        _ = Assert(Parkings.Length == Stages) && Assert(_owned.Count == 0);
    }

    // Stage start: rebuild unless of this list as is (outside Opaque, also the owned set), poll, hand bytes to Skip
    private static bool Poll(List<RenderHandler> list, EnumRenderStage stage)
    {
        var slot = stage switch
        {
            EnumRenderStage.ShadowFar => 0,
            EnumRenderStage.ShadowNear => 1,
            EnumRenderStage.Opaque => OpaqueSlot,
            EnumRenderStage.OIT => 3,
            _ => -1
        };
        if (slot < 0 || !Index(slot, Parkings.Length) || !NotNull(list)) return false;
        var parking = Parkings[slot];
        parking.Polled = _frame;
        if (!ReferenceEquals(parking.Handlers, list) || parking.Stamp != Edits(list) ||
            (slot != OpaqueSlot && parking.Owners != _owners))
        {
            if (parking.Built == _frame) return false; // changed again within the frame: Frame decides, call by call
            Rebuild(parking, list, slot == OpaqueSlot);
        }

        if (parking.UtilCount + parking.RendererCount == 0 || !PollRenderers(parking) || !PollUtils(parking)) return false;
        (_list, _edits, _current) = (list, parking.Stamp, parking);
        _bits = parking.Bits;
        return true;
    }

    private static void Rebuild(Parking parking, List<RenderHandler> list, bool opaque)
    {
        if (!NotNull(parking) || !NotNull(list)) return;
        var items = CollectionsMarshal.AsSpan(list);
        var count = Math.Min(items.Length, MaxParked);
        parking.Reset(count);
        var (renderers, utils) = (RendererKind == Kind.Renderer, _utilKind == Kind.Util && _utilType is not null);
        for (var i = 0; i < Math.Min(count, MaxParked); i++)
        {
            if (i + Lead < count) Fetch(items[i + Lead]);
            if (i + Parts < count && items[i + Parts] is { } ahead) Fetch(ahead.Renderer);
            if (items[i]?.Renderer is not { } renderer) continue;
            var type = renderer.GetType();
            if (renderers && type == typeof(AnimatableRenderer)) parking.Add(Unsafe.As<AnimatableRenderer>(renderer), i);
            else if (utils && type == _utilType) parking.Add(Unsafe.As<AnimationUtil>(renderer), i);
        }

        if (opaque) Own(parking);
        parking.KeepOwned(_owned);
        (parking.Handlers, parking.Stamp, parking.Owners, parking.Built) = (list, Edits(list), _owners, _frame);
        _ = Assert(parking.UtilCount + parking.RendererCount <= count);
    }

    // Counted as a change only when the set differs
    private static void Own(Parking parking)
    {
        var (next, changed) = (_spare, false);
        next.Clear();
        for (var k = 0; k < Math.Min(parking.UtilCount, MaxParked); k++)
            if (parking.Utils[k]?.renderer is { } shown && shown.GetType() == typeof(AnimatableRenderer) && next.Add(shown))
                changed |= !_owned.Contains(shown);
        changed |= next.Count != _owned.Count;
        (_owned, _spare) = (next, _owned);
        _spare.Clear(); // the old set would hold renderers of block entities unloaded since
        if (changed) _owners++;
        _ = Assert(_owned.Count <= parking.UtilCount) && Assert(!ReferenceEquals(_owned, _spare));
    }

    // All parkings, or those unpolled last frame, release objects; the owned set goes with Opaque's, as a change.
    private static void Release(bool all)
    {
        var stale = _frame - 1;
        for (var slot = 0; slot < Parkings.Length; slot++)
        {
            var parking = Parkings[slot];
            if (!parking.Holds || (!all && parking.Polled >= stale)) continue;
            parking.Reset(0);
            _ = Assert(!parking.Holds);
        }

        if (Parkings[OpaqueSlot].Holds || _owned.Count + _spare.Count == 0) return;
        _owned.Clear();
        _spare.Clear();
        _owners++;
        _ = Assert(_owned.Count == 0);
    }

    // Idle while not rendering: one field, on its own line
    private static bool PollRenderers(Parking parking)
    {
        if (!NotNull(parking)) return false;
        var (renderers, at, bits) = (parking.Renderers, parking.RendererAt, parking.Bits);
        var (count, shownAt) = (parking.RendererCount, _shownAt);
        if (!Assert(count <= renderers.Length && count <= at.Length)) return false;
        for (var k = 0; k < Math.Min(count, MaxParked); k++)
        {
            if (k + Lead < count) FetchAt(renderers[k + Lead], shownAt);
            bits[at[k]] = renderers[k].ShouldRender ? (byte)0 : (byte)1;
        }

        return true;
    }

    private static bool PollUtils(Parking parking)
    {
        if (!NotNull(parking)) return false;
        var (utils, at, bits) = (parking.Utils, parking.UtilAt, parking.Bits);
        var (count, shownAt, countAt) = (parking.UtilCount, _shownAt, _countAt);
        if (!Assert(count <= utils.Length && count <= at.Length)) return false;
        for (var k = 0; k < Math.Min(count, MaxParked); k++)
        {
            if (k + Lead < count) Fetch(utils[k + Lead], 2);
            // what Resting reads: animator count, renderer ShouldRender, dictionary count (often on its second line)
            if (k + Parts < count && utils[k + Parts] is { } next)
            {
                FetchAt(next.animator, countAt);
                FetchAt(next.renderer, shownAt);
                Fetch(next.activeAnimationsByAnimCode, 2);
            }

            bits[at[k]] = Resting(utils[k]) ? (byte)1 : (byte)0;
        }

        return true;
    }

    // A stage's parked objects, their list places and Skip's bytes (one per entry; 0 if not parked)
    private sealed class Parking
    {
        public List<RenderHandler>? Handlers;
        public int Stamp, Owners, Built = -1, Polled, UtilCount, RendererCount;
        public AnimationUtil[] Utils = [];
        public AnimatableRenderer[] Renderers = [];
        public int[] UtilAt = [], RendererAt = [];
        public byte[] Bits = [];

        public bool Holds => Handlers is not null || UtilCount + RendererCount > 0;

        // Room for `count` entries; last build's objects released
        public void Reset(int count)
        {
            Array.Clear(Utils, 0, Math.Min(UtilCount, Utils.Length));
            Array.Clear(Renderers, 0, Math.Min(RendererCount, Renderers.Length));
            Array.Clear(Bits);
            (UtilCount, RendererCount, Handlers) = (0, 0, null);
            if (Assert(count is >= 0 and <= MaxParked) && Bits.Length < count)
            {
                var size = Math.Min(MaxParked, count + count / 2); // headroom: block entities keep loading
                (Bits, Utils, Renderers, UtilAt, RendererAt) =
                    (new byte[size], new AnimationUtil[size], new AnimatableRenderer[size], new int[size], new int[size]);
            }

            _ = Assert(Bits.Length >= Math.Min(count, MaxParked) && Utils.Length == Bits.Length);
        }

        public void Add(AnimatableRenderer renderer, int at)
        {
            if (!Index(RendererCount, Renderers.Length) || !Index(at, Bits.Length)) return;
            (Renderers[RendererCount], RendererAt[RendererCount]) = (renderer, at);
            RendererCount++;
        }

        public void Add(AnimationUtil util, int at)
        {
            if (!Index(UtilCount, Utils.Length) || !Index(at, Bits.Length)) return;
            (Utils[UtilCount], UtilAt[UtilCount]) = (util, at);
            UtilCount++;
        }

        public void KeepOwned(HashSet<AnimatableRenderer> owned)
        {
            var (kept, known) = (0, NotNull(owned));
            for (var k = 0; k < Math.Min(RendererCount, MaxParked) && known; k++)
            {
                if (!owned.Contains(Renderers[k])) continue;
                (Renderers[kept], RendererAt[kept]) = (Renderers[k], RendererAt[k]);
                kept++;
            }

            Array.Clear(Renderers, kept, RendererCount - kept);
            RendererCount = kept;
            _ = Assert(kept <= Renderers.Length);
        }

        // Util and renderer leave this stage's bytes; returns entries cleared
        public int Unpark(AnimationUtil util, AnimatableRenderer? shown)
        {
            var found = 0;
            for (var k = 0; k < Math.Min(UtilCount, MaxParked); k++)
            {
                if (!ReferenceEquals(Utils[k], util) || !Index(UtilAt[k], Bits.Length)) continue;
                Bits[UtilAt[k]] = 0;
                found++;
            }

            for (var k = 0; k < Math.Min(RendererCount, MaxParked); k++)
            {
                if (shown is null || !ReferenceEquals(Renderers[k], shown) || !Index(RendererAt[k], Bits.Length)) continue;
                Bits[RendererAt[k]] = 0;
                found++;
            }

            return found;
        }
    }
}
