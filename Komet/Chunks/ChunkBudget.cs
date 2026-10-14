using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// OnBeforeFrame uploads up to num * (3 + queued >> limiter) vertices a frame: several ms after a teleport or retess
// burst. The normal queue also stops after CapMillis (first chunk always goes); 3 ms is ~2x the worst steady frame
// (rend3D-ret-lide 1.35 ms). Mesh backlog past BoostAbove (join/teleport 2000, flight 160-260) boosts to BoostMillis
// until under BoostBelow. The priority queue goes first and whole up to CeilingMillis (>= cap); the rest waits for next
// frame via the engine's flag. A frame already YieldMillis past its Typical time takes one normal chunk (Yield), never
// twice in a row. Spent adds later pool work (Vulkan adopting a pool), so Over counts frames whose Before-stage chunk
// work passed the cap. Marks split rendOpaque-12before.
internal static class ChunkBudget
{
    // EngineShape of OnBeforeFrame in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xC843DFD7A971F8EBUL;
    public const int Uncapped = 0, MaxCapMillis = 20, DefaultCapMillis = 3, CeilingMillis = 6;
    private const int BoostMillis = 8, BoostAbove = 400, BoostBelow = 100, MaxLookahead = 16, YieldMillis = 6, TypicalShift = 4;
    private const string PreMark = "komet-upload-pre", UploadMark = "komet-upload";
    private static long _deadline, _ceiling, _began, _spent, _allowed, _typical;
    private static bool _admitted, _fired, _asked, _cut, _yielded;

    public static int CapMillis { get; set; } = DefaultCapMillis;
    public static bool Capped { get; private set; }
    public static bool Ceiled { get; private set; } // the priority loop's gate is in
    public static bool Boosted { get; private set; }
    public static bool Yield { get; set; } = true;

    // Totals while Counting.Hud: cap stops, priority cuts, frames over the cap and by how much (Stopwatch ticks)
    public static long CapHits { get; private set; }
    public static long PriorityCuts { get; private set; }
    public static long Over { get; private set; }
    public static long OverTicks { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "processPrioQueue")]
    private static extern ref bool PriorityAsked(ChunkTesselatorManager manager);

    internal static MethodBase?[] Seams() =>
        [AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame))];

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_deadline, _ceiling, _began, _spent, _allowed, _typical) = (0, 0, 0, 0, 0, 0);
        (_admitted, _fired, _asked, _cut, Boosted, Capped, Ceiled, _yielded) =
            (false, false, false, false, false, false, false, false);
        var frame = Seams()[0];
        if (!NotNull(harmony) || !NotNull(frame) || !EngineShape.Matches([frame], fingerprint, nameof(ChunkBudget), logger))
            return;
        _ = NotNull(harmony.Patch(frame, new HarmonyMethod(Begin), new HarmonyMethod(End), new HarmonyMethod(Rewrite)));
    }

    private static long Ticks(int millis) =>
        Assert(millis is >= Uncapped and <= MaxCapMillis) ? millis * Stopwatch.Frequency / 1000 : 0;

    // Priority queue counts against the deadline; the previous frame is booked first (its pool work came after it)
    internal static void Begin()
    {
        ScreenManager.FrameProfiler?.Mark(PreMark);
        Book();
        var waiting = RuntimeStats.chunksAwaitingPooling;
        _ = Assert(waiting >= 0);
        Boosted = Boosted ? waiting >= BoostBelow : waiting > BoostAbove;
        var millis = CapMillis == Uncapped || !Boosted ? CapMillis : Math.Max(CapMillis, BoostMillis);
        var now = Stopwatch.GetTimestamp();
        _deadline = millis != Uncapped && Assert(millis is > Uncapped and <= MaxCapMillis) ? now + Ticks(millis) : 0;
        var ceiling = Math.Max(CeilingMillis, millis);
        _ceiling = Assert(ceiling <= MaxCapMillis) ? now + Ticks(ceiling) : 0;
        (_admitted, _fired, _asked, _cut, _began, _spent, _allowed) = (false, false, false, false, now, 0, Ticks(millis));
        if (Yielding(now, FrameClock.FrameStart) && _deadline != 0) _deadline = now + 1; // first chunk, then the cap
        _ = Assert(_deadline == 0 || _deadline > now) && Assert(_ceiling == 0 || _deadline == 0 || _ceiling >= _deadline);
    }

    internal static bool Yielding(long now, long start)
    {
        if (!Yield || start <= 0 || now < start)
        {
            _yielded = false;
            return false;
        }

        var (into, slack) = (now - start, Ticks(YieldMillis));
        if (_typical == 0) _typical = Math.Min(into, slack);
        var slow = !_yielded && _typical > 0 && into > _typical + slack;
        _typical += (Math.Min(into, _typical + slack) - _typical) >> TypicalShift; // a slow frame moves it by <= slack
        _yielded = slow;
        return Assert(_typical >= 0) && Assert(!slow || into > slack) && slow;
    }

    // Harmony injects __instance by name
    internal static void End(ChunkTesselatorManager __instance)
    {
        if (_cut && NotNull(__instance)) PriorityAsked(__instance) = true; // engine cleared it: rest goes next frame
        if (_began > 0) Spent(Stopwatch.GetTimestamp() - _began);
        _began = 0;
        _ = Assert(!_cut || _asked);
        ScreenManager.FrameProfiler?.Mark(UploadMark);
    }

    // Before-stage chunk work outside OnBeforeFrame, main thread
    public static void Spent(long ticks)
    {
        if (Assert(ticks >= 0) && Assert(_spent >= 0)) _spent += ticks;
    }

    private static void Book()
    {
        _ = Assert(_spent >= 0) && Assert(_allowed >= 0);
        if (!Counting.Hud || _allowed == 0 || _spent <= _allowed) return;
        (Over, OverTicks) = (Over + 1, OverTicks + _spent - _allowed);
    }

    // Replaces tessChunksQueuePriority.Count in the loop test: the first ask passes, 0 past the ceiling
    internal static int Priority(int count)
    {
        if (count <= 0 || _ceiling == 0 || !_asked)
        {
            _asked = true;
            return count;
        }

        if (Stopwatch.GetTimestamp() < _ceiling) return count;
        _cut = true;
        if (Counting.Hud) PriorityCuts++;
        return Assert(_asked) && Assert(count > 0) ? 0 : count;
    }

    // Normal queue loops while uploaded < Cap(budget): first test passes, int.MinValue past the deadline. Asked again
    // after firing, the loop didn't end as meant and the engine budget stands. A cut priority queue owns the frame.
    internal static int Cap(int budget)
    {
        if (!Assert(!_fired))
        {
            _admitted = true;
            return budget;
        }

        if (_cut)
        {
            _fired = true;
            return int.MinValue;
        }

        if (_deadline == 0 || !_admitted)
        {
            _admitted = true;
            return budget;
        }

        if (Stopwatch.GetTimestamp() < _deadline) return budget;
        (_deadline, _fired) = (0, true);
        _ = Assert(_admitted);
        if (Counting.Hud) CapHits++;
        return int.MinValue;
    }

    // Cap into the normal loop's condition, gate into the priority loop's; each only on the expected shape
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        (Capped, Ceiled) = (false, false);
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code))) return code;
        var (cap, gate) = (CapSite(code), GateSite(code));
        var (capping, gating) = (AccessTools.Method(typeof(ChunkBudget), nameof(Cap)),
            AccessTools.Method(typeof(ChunkBudget), nameof(Priority)));
        if (!NotNull(capping) || !NotNull(gating)) return code;
        if (cap >= 0) code.Insert(cap + 1, new CodeInstruction(OpCodes.Call, capping)); // consumes num3, leaves the cap
        var gated = gate >= 0 && (cap < 0 || gate < cap); // before the cap: inserting it moved nothing earlier
        if (gated) code.Insert(gate + 1, new CodeInstruction(OpCodes.Call, gating)); // takes the count, leaves it or 0
        (Capped, Ceiled) = (cap >= 0, gated);
        return code;
    }

    // Loop bottom: ldloc num3 (budget), blt back; the cap goes between. num3: local stored after the only limiter read,
    // site: only backward blt on it after Sort() (the early return's read branches forward); else -1
    private static int CapSite(List<CodeInstruction> code)
    {
        var limiter = AccessTools.PropertyGetter(typeof(ClientSettings),
            nameof(ClientSettings.ChunkVerticesUploadRateLimiter));
        var sort = AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<>.Sort));
        if (!NotNull(limiter) || !NotNull(sort)) return -1;
        int read = Il.Single(code, c => c.Calls(limiter)), sorted = Il.Single(code, c => c.Calls(sort));
        if (!Assert(read >= 0) || !Assert(sorted > read)) return -1;
        var site = Site(code, sorted, Budget(code, read));
        return Assert(site > sorted) ? site : -1;
    }

    private static int Budget(List<CodeInstruction> code, int read)
    {
        if (!Index(read, code.Count)) return -1;
        for (var step = 1; step < Math.Min(code.Count - read, MaxLookahead); step++)
            if (Il.Local(code[read + step], Il.Uses.Store) is var local and >= 0) return local;
        return -1;
    }

    // The only ldloc budget, blt after Sort() branching back between Sort() and itself; -1 for none or several
    private static int Site(List<CodeInstruction> code, int sorted, int budget)
    {
        if (budget < 0 || !Index(sorted, code.Count) || !Assert(code.Count <= Il.MaxInstructions)) return -1;
        var site = -1;
        for (var i = sorted + 1; i < Math.Min(code.Count - 1, Il.MaxInstructions); i++)
        {
            var branch = code[i + 1];
            if (Il.Local(code[i], Il.Uses.Load) != budget ||
                (branch.opcode != OpCodes.Blt && branch.opcode != OpCodes.Blt_S) ||
                branch.operand is not Label target ||
                code.FindIndex(sorted, i - sorted, c => c.labels.Contains(target)) < 0) continue;
            if (site >= 0) return -1;
            site = i;
        }

        return site;
    }

    // Priority loop test: ldfld tessChunksQueuePriority, Count, ldc.i4.0, bgt back; the only one, End's flag a bool
    private static int GateSite(List<CodeInstruction> code)
    {
        var queue = AccessTools.DeclaredField(typeof(ChunkTesselatorManager), "tessChunksQueuePriority");
        var count = AccessTools.PropertyGetter(typeof(Queue<TesselatedChunk>), nameof(Queue<>.Count));
        var asked = AccessTools.DeclaredField(typeof(ChunkTesselatorManager), "processPrioQueue");
        if (queue is null || !NotNull(count) || asked?.FieldType != typeof(bool) || asked.IsStatic ||
            !Assert(code.Count <= Il.MaxInstructions)) return -1;
        var site = Il.CountLoop(code, queue, count);
        return Assert(site < code.Count) ? site : -1;
    }
}
