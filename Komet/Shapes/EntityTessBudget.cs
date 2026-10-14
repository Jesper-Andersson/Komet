using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Shapes;

// BeforeRender tesselates every stale visible entity in one frame, and they come in bursts (chunk loads, herds, gear).
// A deferred one keeps shapeFresh false and asks again, in the state vanilla gives culled entities (no mesh, Animator
// null). Known by id and code only, so no world is kept alive.
internal static class EntityTessBudget
{
    public const int Engine = 0, MaxMillis = 50, DefaultMillis = 4, MaxWaitFrames = 30;
    private const int MaxWaiting = 4096;

    private const string MarkName = "esr-tesseleateshape", PreMark = "esr-pre";
    private const string Renderer = "Vintagestory.GameContent.EntityShapeRenderer";

    private const long NoEntity = long.MinValue;
    private static readonly Dictionary<long, int> Waiting = []; // entity id → how often it has been put off
    private static long _frame, _spent, _budget, _began, _self = NoEntity; // _self: the local player's entity id

    // Of the entity whose tesselation Begin and End time: its code, never the entity
    private static AssetLocation? _code;

    public static int Millis { get; set; } = DefaultMillis;
    public static bool Substituted { get; private set; } // the ShapeFresh read was found and replaced
    public static long Tesselations { get; private set; } // totals while Counting.Hud, main thread
    public static long Deferred { get; private set; } // entity-frames put off
    public static long Ticks { get; private set; } // in TesselateShape from BeforeRender

    // Frames put off, of an entity that has since tesselated; peaks since ResetPeaks
    public static long MostWaited { get; private set; }

    public static double WorstFrameMs { get; private set; } // of those in one frame
    public static double SlowestMs { get; private set; } // one entity's tesselation
    public static string SlowestCode { get; private set; } = ""; // and whose it was: nine deer or one player

    public static void ResetPeaks()
    {
        _ = Assert(MostWaited >= 0) && Assert(WorstFrameMs >= 0);
        (MostWaited, WorstFrameMs, SlowestMs, SlowestCode) = (0, 0, 0, "");
    }

    internal static void Reset()
    {
        ResetPeaks();
        (Tesselations, Deferred, Ticks) = (0, 0, 0);
        (_frame, _spent, _budget, _began, _self, _code) = (0, 0, 0, 0, NoEntity, null);
        Waiting.Clear();
        _ = Assert(Waiting.Count == 0);
    }

    public static void Install(Harmony harmony, ILogger? logger)
    {
        Reset();
        Substituted = false;
        if (!NotNull(harmony)) return;
        var frame = AccessTools.Method(typeof(SystemRenderEntities), "OnBeforeRender", [typeof(float)]);
        var prefix = new HarmonyMethod(Frame);
        if (NotNull(frame) && Assert(Il.Binds(frame, prefix.method))) _ = NotNull(harmony.Patch(frame, prefix));
        if (Before() is { } before) _ = NotNull(harmony.Patch(before, transpiler: new HarmonyMethod(Substitute)));
        if (!Substituted)
            logger?.Warning(
                "Komet EntityTessBudget: EntityShapeRenderer.BeforeRender is not the method it was written for; " +
                "every entity tessellates at once, as in the engine");
    }

    // EntityShapeRenderer is in VSEssentials (unreferenced); declared there, not inherited
    internal static MethodInfo? Before() => AccessTools.TypeByName(Renderer) is { } type
        ? AccessTools.DeclaredMethod(type, nameof(EntityRenderer.BeforeRender), [typeof(float)]) : null;

    // Harmony injects ClientSystem.game by name
    internal static void Frame(ClientMain ___game)
    {
        ScreenManager.FrameProfiler?.Mark(PreMark); // what the renderers before this one took, not the first entity
        Start(___game?.EntityPlayer);
    }

    // The local player's tesselation is never put off: its gear must change on the frame it is put on
    internal static void Start(Entity? self)
    {
        if (Counting.Hud && _spent > 0) WorstFrameMs = Math.Max(WorstFrameMs, FrameClock.ToMs(_spent));
        (_frame, _spent, _self, _code) = (_frame + 1, 0, self?.EntityId ?? NoEntity, null);
        _budget = Assert(Millis is >= Engine and <= MaxMillis) ? Millis * Stopwatch.Frequency / 1000 : 0;
        if (_budget == 0 && Waiting.Count > 0) Waiting.Clear();
    }

    // Replaces entity.ShapeFresh in BeforeRender: true skips this frame's tesselation; null throws as the engine's
    internal static bool Fresh(Entity entity)
    {
        if (entity.ShapeFresh) return true;
        _code = entity.Code;
        if (_budget <= 0 || !Assert(_spent >= 0)) return false;
        var id = entity.EntityId;
        var waited = Waiting.Count > 0 && Waiting.TryGetValue(id, out var times) ? times : 0;
        if (_spent < _budget || id == _self || waited >= MaxWaitFrames)
        {
            if (waited > 0 && Waiting.Remove(id) && Counting.Hud) MostWaited = Math.Max(MostWaited, waited);
            return false;
        }

        // Counted in frames asked and put off, not frames since: one that left the view has not waited
        if (waited == 0 && Waiting.Count >= MaxWaiting) Waiting.Clear(); // despawned waiters: start over, don't grow
        Waiting[id] = waited + 1;
        _code = null; // nothing of it to time
        if (Counting.Hud) Deferred++;
        return Assert(_frame > 0) && Assert(Waiting.Count <= MaxWaiting);
    }

    // Around TesselateShape() in BeforeRender. One that throws leaves _began set; the next Begin overwrites it.
    internal static void Begin()
    {
        _began = Stopwatch.GetTimestamp();
        _ = Assert(_began > 0);
    }

    internal static void End()
    {
        if (_began == 0) return;
        var spent = Stopwatch.GetTimestamp() - _began;
        _began = 0;
        if (!Assert(spent >= 0)) return;
        _spent += spent;
        var code = _code;
        _code = null;
        if (!Counting.Hud) return;
        (Tesselations, Ticks) = (Tesselations + 1, Ticks + spent);
        var ms = FrameClock.ToMs(spent);
        if (ms > SlowestMs) (SlowestMs, SlowestCode) = (ms, code?.ToShortString() ?? "");
    }

    // ldstr "esr-tesseleateshape", ShapeFresh read, TesselateShape() once each in order: read → Fresh, call → Begin/End
    internal static List<CodeInstruction> Substitute(IEnumerable<CodeInstruction> instructions)
    {
        Substituted = false;
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;
        var fresh = AccessTools.PropertyGetter(typeof(Entity), nameof(Entity.ShapeFresh));
        var tesselate = AccessTools.TypeByName(Renderer) is { } type
            ? AccessTools.DeclaredMethod(type, "TesselateShape", Type.EmptyTypes) : null;
        var self = typeof(EntityTessBudget);
        var (mine, begin, end) = (AccessTools.DeclaredMethod(self, nameof(Fresh)),
            AccessTools.DeclaredMethod(self, nameof(Begin)), AccessTools.DeclaredMethod(self, nameof(End)));
        if (fresh is null || tesselate is null || !NotNull(mine) || !NotNull(begin) || !NotNull(end)) return code;
        var mark = Il.Single(code, c => c.opcode == OpCodes.Ldstr && c.operand is MarkName);
        var read = Il.Single(code, c => c.Calls(fresh));
        var call = Il.Single(code, c => c.Calls(tesselate));
        if (read < 0 || call <= read || mark <= call || code[call].labels.Count > 0 || !Il.Substitute(code, read, mine))
            return code;
        code.Insert(call + 1, new CodeInstruction(OpCodes.Call, end));
        // after the ldarg.0 the call consumes: stack neutral
        code.Insert(call, new CodeInstruction(OpCodes.Call, begin));
        Substituted = true;
        return code;
    }
}
