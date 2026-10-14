using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Diagnostics;
using Vintagestory.Client.NoObf;

namespace Komet.Shapes;

// First-draw inventory meshes cost 5-15 ms each (a creative row: 150 ms). New items draw only while BudgetMs is left,
// the rest stay empty until their turn.
internal static class IconBudget
{
    public const double BudgetMs = 6;
    private const string Creative = "creative";

    private static readonly ConditionalWeakTable<ItemSlot, object> Drawn = [];
    private static readonly object Seen = new();
    private static long _frame = -1, _spent;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    internal static MethodInfo? Target() => !Assert(BudgetMs > 0) ? null : AccessTools.Method(typeof(InventoryItemRenderer), nameof(InventoryItemRenderer.RenderItemstackToGui),
        [typeof(ItemSlot), typeof(double), typeof(double), typeof(double), typeof(float), typeof(int), typeof(float), typeof(bool), typeof(bool),
            typeof(bool)]);

    public static void Install(Harmony harmony)
    {
        Patched = false;
        if (!NotNull(harmony) || !Assert(BudgetMs > 0) || Target() is not { } target) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Drawing), new HarmonyMethod(Done)));
    }

    // False skips the draw this frame: a creative slot drawn for the first time once the frame's budget is spent
    private static bool Drawing(ItemSlot inSlot, out long __state)
    {
        __state = 0;
        if (!Assert(_spent >= 0) || !Enabled || FrameClock.Frames == 0 || inSlot?.Inventory?.ClassName != Creative || Drawn.TryGetValue(inSlot, out _)) return true;
        if (FrameClock.Frames != _frame) (_frame, _spent) = (FrameClock.Frames, 0); // not Completed: it stands still without the HUD
        if (_spent >= BudgetMs * Stopwatch.Frequency / 1000 || !Assert(_spent >= 0)) return false;
        __state = Stopwatch.GetTimestamp();
        return true;
    }

    private static void Done(ItemSlot inSlot, long __state)
    {
        if (__state == 0 || !NotNull(inSlot) || !Assert(__state > 0)) return;
        _spent += Stopwatch.GetTimestamp() - __state;
        _ = Assert(_spent >= 0);
        Drawn.AddOrUpdate(inSlot, Seen);
    }
}
