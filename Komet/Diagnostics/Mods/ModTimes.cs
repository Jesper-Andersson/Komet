using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Komet.Hud;
using Komet.Rendering;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Diagnostics;

// Patches go in on the panel's first switch-on after level finalize
internal sealed class ModTimes
{
    public const int MaxMods = TopN.MaxRank, DetailCount = 3;
    internal const int MaxRenderers = 16384;

    internal const ulong Shape = 0x96AA3DA7397A9919UL;

    private const int MaxEntries = 4096, MaxNesting = 16, ReorderEvery = 8;

    // One frame in SampleEvery is timed and scaled: a timestamp pair per renderer cost ~1/4 of the main thread
    private const int SampleEvery = 8;
    private const double Smoothing = 0.25;

    private static readonly Dictionary<object, Entry> Entries = [];

    private static readonly Dictionary<Assembly, string> ModByAssembly = [];
    private static Harmony? _harmony;
    private static IModLoader? _loader;
    private static ILogger? _logger;
    private static MethodInfo? _stage, _entity, _block;
    private static bool _full, _crowded, _ready, _replay;

    private static bool _sampling = true; // True when RenderStage is not patched: listeners timed every frame

    private static int _frame, _sampled;

    private readonly Dictionary<string, double> _byMod = [];
    private readonly (string? Mod, double Ms)[] _mods = new (string?, double)[MaxMods];

    private readonly (Entry? Entry, double Ms)[][] _top = Array.ConvertAll(new int[MaxMods],
        _ => new (Entry?, double)[DetailCount]);

    private int _updates;

    public static bool Enabled
    {
        get;
        set
        {
            field = value;
            if (value && _ready) Patch();
        }
    }

    public static bool Patched { get; private set; } // the patches went in, or were tried, this session
    public static bool Replays => _replay; // RenderStage knows the engine's loop: the renderers are timed too

    // World change: patches and engine objects are gone, the session starts over
    public static void Install(Harmony harmony, IModLoader loader, ILogger logger)
    {
        Entries.Clear();
        ModByAssembly.Clear();
        (_harmony, _loader, _logger, Patched, _full, _crowded, _ready, _replay) =
            (null, null, null, false, false, false, false, false);
        (_sampling, _frame, _sampled) = (true, 0, 0);
        if (!NotNull(harmony) || !NotNull(loader) || !NotNull(logger)) return;
        _stage = AccessTools.Method(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage));
        _entity = AccessTools.Method(typeof(GameTickListener), nameof(GameTickListener.OnTriggered));
        _block = AccessTools.Method(typeof(GameTickListenerBlock), nameof(GameTickListenerBlock.OnTriggered));
        if (_entity is null || _block is null)
        {
            logger.Warning("Komet: the game tick listeners are not the engine's any more, the mod times stay empty");
            return;
        }

        _replay = EngineShape.Matches([_stage], Shape, nameof(ModTimes), logger) &&
                  Il.Binds(_stage, AccessTools.Method(typeof(ModTimes), nameof(RenderStage)));
        if (!_replay)
            logger.Warning(
                "Komet: TriggerRenderStage is not the loop the mod times replay, they leave the renderers out");
        (_harmony, _loader, _logger) = (harmony, loader, logger);
    }

    // All mods patched now, so Foreign sees their transpilers on TriggerRenderStage
    public static void Ready()
    {
        _ = Assert(!_ready); // once per session, like the event
        _ready = true;
        if (Enabled) Patch();
    }

    private static void Patch()
    {
        if (Patched || !Assert(_ready) || _harmony is null || !NotNull(_entity) || !NotNull(_block)) return;
        Patched = true;
        var self = typeof(ModTimes);
        try
        {
            if (_replay && _stage is { } stage && !EngineShape.Foreign([stage], PatchKinds.Body, null, self))
                _ = _harmony.Patch(stage, new HarmonyMethod(RenderStage));
            else if (_replay)
                _logger?.Warning(
                    "Komet: another mod rewrites TriggerRenderStage, the mod times leave the renderers out");
            _ = _harmony.Patch(_entity, new HarmonyMethod(Start), new HarmonyMethod(EndEntity));
            _ = _harmony.Patch(_block, new HarmonyMethod(Start), new HarmonyMethod(EndBlock));
        }
        catch (Exception e) when (e is HarmonyException or ArgumentException or InvalidOperationException
                                      or NotSupportedException)
        {
            _logger?.Warning("Komet: the mod times could not patch the engine ({0})", e.Message);
        }
    }

    // Over MaxRenderers back to the engine's loop: a bounded loop would skip renderers. Harmony injects ___ fields by
    // name.
    internal static bool RenderStage(ClientMain ___game, List<RenderHandler>[] ___renderersByStage,
        EnumRenderStage stage, float dt)
    {
        if (!Enabled || !Index((int)stage, ___renderersByStage.Length)) return true;
        if (stage == EnumRenderStage.Before)
        {
            _sampling = ++_frame % SampleEvery == 0;
            if (_sampling) _sampled++;
        }

        if (!_sampling) return true;
        var list = ___renderersByStage[(int)stage];
        if (!NotNull(list)) return true;
        if (list.Count > MaxRenderers)
        {
            if (!_crowded)
                _logger?.Warning("Komet: {0} holds more than {1} renderers, the mod times leave it out", stage,
                    MaxRenderers);
            _crowded = true;
            return true;
        }

        var debug = ___game.extendedDebugInfo;
        // Count re-read: a renderer may unregister. Idle animatables skipped as the engine does (~12,900 a frame cost
        // ms).
        for (var i = 0; i < Math.Min(list.Count, MaxRenderers); i++)
        {
            if (IdleAnimators.Skip(list, i)) continue;
            var handler = list[i];
            if (!NotNull(handler.Renderer) || IdleAnimators.TrySkip(handler.Renderer)) continue;
            var start = Stopwatch.GetTimestamp();
            handler.Renderer.OnRenderFrame(dt, stage);
            Book(handler.Renderer is DummyRenderer dummy ? dummy.action.Method : handler.Renderer.GetType(), start);
            if (!debug) continue;
            ScreenManager.Platform.CheckGlError(handler.ProfilingName);
            ScreenManager.FrameProfiler.Mark(handler.ProfilingName);
        }

        return false;
    }

    // Also runs on the singleplayer server thread; Entries is the client's
    private static void Start(out long __state) =>
        __state = Enabled && _sampling && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId
            ? Stopwatch.GetTimestamp()
            : 0;

    private static void EndEntity(GameTickListener __instance, long __state)
    {
        if (__state != 0 && NotNull(__instance.Handler)) Book(__instance.Handler.Method, __state);
    }

    private static void EndBlock(GameTickListenerBlock __instance, long __state)
    {
        if (__state == 0) return;
        var handler = __instance.Handler ?? (Delegate?)__instance.HandlerBare;
        if (NotNull(handler)) Book(handler.Method, __state);
    }

    private static void Book(object key, long start)
    {
        var ticks = Stopwatch.GetTimestamp() - start;
        if (!Assert(start > 0) || !Assert(ticks >= 0) || !Assert(key is Type or MethodInfo)) return;
        if (!Entries.TryGetValue(key, out var entry))
        {
            if (Entries.Count >= MaxEntries)
            {
                if (!_full)
                    _logger?.Warning("Komet: the mod times keep {0} renderers and listeners, later ones are left out",
                        MaxEntries);
                _full = true;
                return;
            }

            Entries[key] = entry =
                key is Type type ? new Entry(ModOf(type.Assembly), type.Name) : Create((MethodInfo)key);
        }

        entry.Ticks += ticks;
        entry.Calls++;
    }

    private static int TakeSampled(int frames)
    {
        var timed = _sampled > 0 ? Math.Min(_sampled, frames) : frames;
        _sampled = 0;
        return Assert(timed > 0) ? timed : 1;
    }

    private static Entry Create(MethodInfo method)
    {
        var owner = method.DeclaringType;
        if (!NotNull(owner) || !Assert(method.Name.Length > 0)) return new Entry("?", method.Name);
        for (var depth = 0; depth < MaxNesting && owner.Name.StartsWith('<') && owner.DeclaringType != null; depth++)
            owner = owner.DeclaringType; // lambdas live in generated nested classes
        var name = method.Name;
        var close = name.IndexOf('>', StringComparison.Ordinal);
        if (name.StartsWith('<') && Assert(close > 1)) name = name[1..close] + " (lambda)";
        return new Entry(ModOf(owner.Assembly), owner.Name + "." + name);
    }

    private static string ModOf(Assembly assembly)
    {
        if (ModByAssembly.TryGetValue(assembly, out var mod)) return mod;
        mod = assembly.GetName().Name ?? "?";
        if (!Assert(mod.Length > 0)) mod = "?";
        if (mod.StartsWith("Vintagestory", StringComparison.Ordinal)) mod = "game";
        foreach (var candidate in (_loader?.Mods ?? []).Bounded(HarmonyAudit.MaxLoadedMods))
            if (candidate is ModContainer container && container.Assembly == assembly) mod = candidate.Info.ModID;
        return ModByAssembly[assembly] = mod;
    }

    public double MsOf(string mod) => NotNull(mod) && _byMod.TryGetValue(mod, out var ms) && Assert(ms >= 0) ? ms : 0;

    public double TotalExcept(string mod)
    {
        double total = 0;
        using (var mods = _byMod.GetEnumerator())
        {
            for (var i = 0; i < MaxEntries && mods.MoveNext(); i++)
                if (mods.Current.Key != mod) total += mods.Current.Value;
        }
        return Assert(total >= 0) ? total : 0;
    }

    public string ModName(int place) => Index(place, MaxMods) ? _mods[place].Mod ?? "" : "";

    public double ModMs(int place) => Index(place, MaxMods) && _mods[place].Mod != null ? _mods[place].Ms : double.NaN;

    public string DetailName(int place, int rank) =>
        Index(place, MaxMods) && Index(rank, DetailCount) && _top[place][rank].Entry is { } e
            ? $"{e.Name} ({HudText.Format(e.CallsPerSecond, "F1")}/s)"
            : "";

    public double DetailMs(int place, int rank) => Index(place, MaxMods) && Index(rank, DetailCount)
        ? _top[place][rank].Entry?.SmoothMs ?? double.NaN
        : double.NaN;

    public void Update(int frames, float seconds)
    {
        if (!Assert(frames > 0) || !Assert(seconds > 0) || !Assert(Entries.Count <= MaxEntries)) return;
        var timed = TakeSampled(frames);
        var scale = (double)frames / timed;
        _byMod.Clear();
        using (var entries = Entries.GetEnumerator())
        {
            for (var i = 0; i < MaxEntries && entries.MoveNext(); i++)
            {
                var entry = entries.Current.Value;
                if (!Assert(entry.Ticks >= 0) || !Assert(entry.Calls >= 0)) continue;
                entry.SmoothMs += (FrameClock.ToMs(entry.Ticks) / timed - entry.SmoothMs) * Smoothing;
                entry.CallsPerSecond += (entry.Calls * scale / seconds - entry.CallsPerSecond) * Smoothing;
                (entry.Ticks, entry.Calls) = (0, 0);
                _byMod[entry.Mod] = _byMod.GetValueOrDefault(entry.Mod) + entry.SmoothMs;
            }
        }

        if (_updates++ % ReorderEvery == 0)
        {
            Reorder();
            return;
        }

        for (var i = 0; i < MaxMods; i++)
            if (_mods[i].Mod is { } mod) _mods[i].Ms = _byMod.GetValueOrDefault(mod);
    }

    private void Reorder()
    {
        if (!Assert(_byMod.Count <= MaxEntries)) return;
        Array.Clear(_mods);
        using (var mods = _byMod.GetEnumerator())
        {
            for (var i = 0; i < MaxEntries && mods.MoveNext(); i++)
                TopN.Rank(_mods, mods.Current.Key, mods.Current.Value);
        }

        for (var i = 0; i < MaxMods; i++) Array.Clear(_top[i]);
        using var entries = Entries.GetEnumerator();
        for (var i = 0; i < MaxEntries && entries.MoveNext(); i++)
        {
            var entry = entries.Current.Value;
            var place = Place(entry.Mod);
            if (place >= 0) TopN.Rank(_top[place], entry, entry.SmoothMs);
        }
    }

    private int Place(string mod)
    {
        if (!Assert(mod.Length > 0)) return -1; // ModOf and Create name every entry's mod
        for (var i = 0; i < MaxMods; i++)
            if (_mods[i].Mod == mod) return i;
        return -1;
    }

    private sealed class Entry(string mod, string name)
    {
        public readonly string Mod = mod, Name = name;
        public int Calls;
        public double SmoothMs, CallsPerSecond;
        public long Ticks;
    }
}
