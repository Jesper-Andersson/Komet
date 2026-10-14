using Komet.Api;
using Komet.Diagnostics;

namespace Komet.Hud;

// /komet debug: every collector on, each frame recorded, ending in DebugProtocol (clipboard, Logs/komet-debug). Modes:
// timed (10 s), "now" (3 s), "spike [ms]" (armed until a frame exceeds ms, then 2 s), "stop"; "en"/"de" language.
internal sealed partial class HudOverlay
{
    private const float DefaultSeconds = 10, NowSeconds = 3, MaxSeconds = 600, SpikeWindow = 20, PostRoll = 2;
    private const double DefaultSpikeMs = 50;
    private DebugFrames? _debug;
    private string _debugMode = "", _debugTrigger = "";
    private float _debugLeft, _debugTotal;
    private double _debugSpikeMs;
    private DateTimeOffset _debugStarted;
    private long _debugLogFrom;
    private Task? _debugWriting;
    private readonly Dictionary<string, (double Sum, int Intervals)> _debugMods = [];
    private readonly Dictionary<string, (string, double)[]> _debugModTop = [];

    private bool Forced => _benchLeft > 0 || _debug is not null || _profile is not null;

    private void RegisterDebug(ICoreClientAPI capi)
    {
        if (!NotNull(capi.ChatCommands) || !Assert(DefaultSeconds > 0)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").BeginSubCommand("debug")
            .WithDescription(HudText.Translate("cmd-debug"))
            .WithArgs(capi.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(args => DebugCommand(args[0] as string ?? "")).EndSubCommand();
    }

    private TextCommandResult DebugCommand(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!Assert(words.Length < 64) || !NotNull(words)) return TextCommandResult.Error("");
        var (mode, seconds, spikeMs) = ("record", DefaultSeconds, double.NaN);
        foreach (var word in words.Bounded(64))
        {
            var number = double.TryParse(word, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1e6;
            if (word is "en" or "de") _settings.DebugLanguage = word;
            else if (word is "now" or "stop" or "spike") mode = word;
            else if (number && mode == "spike") spikeMs = value;
            else if (number) seconds = (float)Math.Min(value, MaxSeconds);
            else return TextCommandResult.Error(HudText.Translate("cmd-debug-usage"));
        }

        if (mode == "stop") return StopDebug();
        if (_debug is not null || _debugWriting is { IsCompleted: false })
            return TextCommandResult.Error(HudText.Translate("cmd-debug-running"));
        if (_benchLeft > 0) return TextCommandResult.Error(HudText.Translate("cmd-debug-bench"));
        StartDebug(mode, mode == "now" ? NowSeconds : seconds, double.IsNaN(spikeMs) ? DefaultSpikeMs : spikeMs);
        return TextCommandResult.Success(mode == "spike"
            ? HudText.Translate("cmd-debug-armed", DebugText.Num(_debugSpikeMs, "F0"))
            : HudText.Translate("cmd-debug-started", DebugText.Num(_debugLeft, "F0")));
    }

    private void StartDebug(string mode, float seconds, double spikeMs)
    {
        if (!Assert(seconds > 0) || !Assert(spikeMs > 0)) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetBench();
        _frames.ResetHistory();
        _passes.ResetWorst();
        _spikes.Reset();
        _debugMods.Clear();
        _debugModTop.Clear();
        _profiled.Clear();
        _debug = new DebugFrames();
        (_debugMode, _debugSpikeMs, _debugTrigger, _debugStarted) = (mode, spikeMs, "", DateTimeOffset.Now);
        _debugLeft = mode == "spike" ? float.PositiveInfinity : seconds;
        _debugTotal = mode == "spike" ? 0 : seconds;
        _debugLogFrom = DebugFiles.LogLength();
        _ = KometDebug.TakeTimings(); // what an earlier capture left behind
        KometDebug.Capturing = true;
        UpdateProfiler();
        Quiet(); // the first switch-on of the collectors patches the engine
    }

    private TextCommandResult StopDebug()
    {
        if (_debug is null || !Assert(_debugMode.Length > 0)) return TextCommandResult.Error(HudText.Translate("cmd-debug-idle"));
        if (_debugTrigger.Length == 0 && _debugMode == "spike") _debugTrigger = HudText.Translate("hud-dbg-stopped");
        FinishDebug();
        return Assert(_debug is null) ? TextCommandResult.Success(HudText.Translate("cmd-debug-stopped")) : TextCommandResult.Error("");
    }

    private void DebugFrame(DebugFrames debug, in FrameRecord frame, bool steady)
    {
        var record = debug.Add(frame, _passes.FrameTop, steady);
        if (_debugMode != "spike" || _debugTrigger.Length > 0 || !steady || record.DtMs < _debugSpikeMs) return;
        _debugTrigger = HudText.Translate("hud-dbg-triggered", DebugText.Num(record.DtMs, "F1"), DebugText.Num(record.At, "F1"));
        _debugLeft = PostRoll;
        _ = Assert(_debugLeft > 0) && Assert(_debugTrigger.Length > 0);
    }

    private void DebugInterval(float elapsed)
    {
        if (!Assert(elapsed > 0) || _debug is null) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Accumulate();
        for (var i = 0; i < ModTimes.MaxMods; i++)
            if (_timings.ModName(i) is { Length: > 0 } mod && _timings.ModMs(i) is var ms && double.IsFinite(ms))
            {
                var (sum, intervals) = _debugMods.GetValueOrDefault(mod);
                _debugMods[mod] = (sum + ms, intervals + 1);
                _debugModTop[mod] = [.. Enumerable.Range(0, ModTimes.DetailCount).Select(r => (_timings.DetailName(i, r), _timings.DetailMs(i, r)))
                    .Where(static d => d.Item1.Length > 0)];
            }

        _debugLeft -= elapsed;
        if (_debugLeft <= 0 && Assert(_debugMods.Count <= HarmonyAudit.MaxLoadedMods)) FinishDebug();
    }

    private void FinishDebug()
    {
        if (_debug is not { } debug || !Assert(_debugMode.Length > 0)) return;
        _spikes.Rank();
        var input = DebugSnapshot(debug);
        (_debug, KometDebug.Capturing) = (null, false);
        UpdateProfiler();
        var (capi, from) = (_capi, _debugLogFrom);
        _debugWriting = Task.Run(() =>
        {
            var (text, folder, compare, rebuild) = DebugFiles.Write(capi, input, from);
            capi.Event.EnqueueMainThreadTask(() => Delivered(text, folder, compare, rebuild), "komet-debug");
        });
    }

    private System.Func<string, string>? _rebuild;

    internal void Relabel(string code)
    {
        if (_rebuild is not { } rebuild || !Assert(code is "en" or "de")) return;
        LastProtocol = rebuild(code);
        _capi.Input.ClipboardText = LastProtocol;
    }

    private void Delivered(string text, string folder, DebugCompare compare, System.Func<string, string>? rebuild)
    {
        if (_disposed || !NotNull(text)) return;
        if (text.Length == 0)
        {
            _capi.ShowChatMessage(HudText.Translate("cmd-debug-failed", folder));
            return;
        }

        _capi.Input.ClipboardText = text;
        _capi.ShowChatMessage(HudText.Translate("cmd-debug-done", folder));
        (LastProtocol, LastCompare, _rebuild) = (text, compare, rebuild);
        _debugWindow.Toggle(true);
        _ = Assert(folder.Length > 0);
    }

    internal string LastProtocol { get; private set; } = "";
    internal DebugCompare LastCompare { get; private set; } = DebugCompare.None;

    internal (bool Running, bool Armed, double Elapsed, double Total, long Frames, int Spikes) DebugProgress =>
        _debug is { } debug && Assert(_debugMode.Length > 0)
            ? (true, _debugMode == "spike" && _debugTrigger.Length == 0, debug.Now, _debugTotal, debug.Count, _spikes.Count)
            : (_debugWriting is { IsCompleted: false }, false, 0, 0, 0, 0);
    internal bool DebugRunning => _debug is not null || _debugWriting is { IsCompleted: false };
    internal double SpikeThresholdMs => _settings.ToastMs > 0 ? _settings.ToastMs : DefaultSpikeMs;

    // seconds 0: until stopped; spikeMs NaN: the overlay's threshold
    internal void Capture(string mode, float seconds, double spikeMs = double.NaN)
    {
        if (!Assert(mode is "now" or "record" or "spike") || DebugRunning) return;
        if (_benchLeft > 0)
        {
            _capi.ShowChatMessage(HudText.Translate("cmd-debug-bench"));
            return;
        }

        var time = seconds <= 0 ? MaxSeconds : Math.Clamp(seconds, 1, MaxSeconds);
        if (mode == "now") time = NowSeconds;
        StartDebug(mode, time, double.IsFinite(spikeMs) && spikeMs > 0 ? spikeMs : SpikeThresholdMs);
    }

    internal void EndCapture()
    {
        if (_debug is null || !Assert(_debugMode.Length > 0)) return;
        _ = StopDebug();
    }
}
