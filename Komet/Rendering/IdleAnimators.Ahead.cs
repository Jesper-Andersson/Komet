using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Asking a renderer if idle costs a chain of dependent cache misses (handler, renderer, util's animator, renderer,
// dictionary). Frame runs in list order, so a cursor prefetches: handler Far ahead, renderer Near, util parts Next. A
// mismatched cursor stops. Parked stages prefetch in their poll instead (what reaches Frame there is mostly busy).
internal static partial class IdleAnimators
{
    private const int Far = 16, Near = 8, Next = 4, Line = 64, MaxOffset = 1024;

    private static List<RenderHandler>? _walked;
    private static int _cursor;

    // Offsets of the poll's fields from the object address (main thread, Install): ShouldRender and activeAnimCount, a
    // dozen references deep, mostly on the object's second or third line
    private static nint _shownAt, _countAt;

    private const string CountName = "activeAnimCount";

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = CountName)]
    private static extern ref int ActiveCount(AnimatorBase animator);

    // Unpolled stages release parked objects: every stage while nothing parks, at Before those skipped last frame
    internal static void Staged(List<RenderHandler>[] ___renderersByStage, EnumRenderStage stage)
    {
        (_walked, _list, _current) = (null, null, null);
        _bits = [];
        var polls = Enabled && !Blocked && _parks && !_crossed;
        if (stage == EnumRenderStage.Before) _frame++;
        if (!polls || stage == EnumRenderStage.Before) Release(!polls);
        if (!Enabled || Blocked || !NotNull(___renderersByStage) || !Index((int)stage, ___renderersByStage.Length)) return;
        var list = ___renderersByStage[(int)stage];
        if (!NotNull(list)) return;
        if (!(polls && Poll(list, stage)) && Sse.IsSupported) (_walked, _cursor) = (list, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Ahead(IRenderer? renderer)
    {
        if (_walked is not { } list) return;
        var items = CollectionsMarshal.AsSpan(list);
        var at = _cursor;
        if ((uint)at >= (uint)items.Length || !ReferenceEquals(items[at]?.Renderer, renderer))
        {
            _walked = null;
            return;
        }

        _cursor = at + 1;
        if (at + Far < items.Length) Fetch(items[at + Far]);
        if (at + Near < items.Length && items[at + Near] is { } near) Fetch(near.Renderer, 2);
        if (at + Next < items.Length && items[at + Next]?.Renderer is AnimationUtil util)
        {
            Fetch(util.animator);
            Fetch(util.renderer, 2);
            Fetch(util.activeAnimationsByAnimCode);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Fetch(object? item, int lines = 1)
    {
        if (item is null || !Sse.IsSupported) return;
        var address = (byte*)Unsafe.As<object, nint>(ref item);
        Sse.Prefetch0(address);
        if (lines > 1) Sse.Prefetch0(address + Line);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void FetchAt(object? item, nint at)
    {
        if (item is null || !Sse.IsSupported) return;
        Sse.Prefetch0((byte*)Unsafe.As<object, nint>(ref item) + at);
    }

    // Field offset from the object address: from the first field (StrongBox's one field in any class) plus the method
    // table pointer. Uninitialized probes: no constructor runs.
    private static void Measure()
    {
        var shown = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
        _shownAt = Unsafe.ByteOffset(ref Unsafe.As<StrongBox<byte>>(shown).Value,
            ref Unsafe.As<bool, byte>(ref shown.ShouldRender)) + IntPtr.Size;
        if (!Assert(_shownAt > 0 && _shownAt < MaxOffset)) _shownAt = 0;
        // only guard for the protected field (engine reads the property): a renamed field would throw out of Install
        _countAt = Assert(AccessTools.DeclaredField(typeof(AnimatorBase), CountName)?.FieldType == typeof(int))
            ? CountAt((AnimatorBase)RuntimeHelpers.GetUninitializedObject(typeof(ClientAnimator)))
            : 0;
    }

    // Apart from Measure: the accessor binds its field when compiled, which only a call does
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint CountAt(AnimatorBase probe)
    {
        if (!NotNull(probe)) return 0;
        var at = Unsafe.ByteOffset(ref Unsafe.As<StrongBox<byte>>(probe).Value,
            ref Unsafe.As<int, byte>(ref ActiveCount(probe))) + IntPtr.Size;
        return Assert(at > 0 && at < MaxOffset) ? at : 0;
    }
}
