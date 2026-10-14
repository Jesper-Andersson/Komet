using System.Text;
using Komet.Api;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using Vintagestory.Client;

namespace Komet.Hud;

internal sealed partial class HudOverlay : IRenderer
{
    private const int WarmupMs = 5000, SettleMs = 30000;

    // Not steady after the HUD appears or around a dialog: JIT, first Cairo passes and composition owned the 0.1 % low
    // ~20 s.
    private const int QuietMs = 1000;
    private readonly (string Version, bool Preview, string Commit, string SourcePath) _build;
    private readonly ICoreClientAPI _capi;
    private readonly FrameStats _frames = new();
    private readonly (string Mode, string Budget) _gc = GcConfig(HudText.Translate);
    private readonly GpuStats _gpu = new();
    private readonly List<HudPanel> _panels = [];
    private readonly RenderPassStats _passes = new();

    private readonly ResourceStats _resources = new();
    private readonly HudSettings _settings;
    private readonly SnapGrid _snap;
    private readonly SpikeLedger _spikes = new();
    private readonly ModTimes _timings = new();
    private GuiDialogConfirm? _ask;
    private long _consumed; // FrameClock records taken
    private bool _disposed, _wasVisible, _profilerOwned, _resumed, _settled;
    private double _elapsedTotal;
    private long _frameTotal; // frames and seconds since the HUD began, what the rows divide counter growth by
    private int _interval;
    private long _saveCallback;
    private long _quietUntil;
    private int _skip;
    private Task? _sampling;
    private Action? _sample;
    private float _sinceInterval, _benchLeft;
    private UpdateCheck? _update;

    internal OptionsScreen Options { get; }
    private readonly HudWindow _window;
    private readonly DebugWindow _debugWindow;

    internal static Action<int>? ShowForBench { get; private set; }

    // Bench-started capture ("gui": "debug"): its end runs under the freeze watchdog
    internal static Action<float>? CaptureForBench { get; private set; }

    internal static Action<bool>? OptionsForBench { get; private set; }

    private void ShowTab(int lap)
    {
        if (!Assert(lap >= 0) || _disposed) return;
        if (lap < HudWindow.Tabs.Length) _window.Toggle(lap);
        else
        {
            _ = !_window.IsOpened() || _window.TryClose();
            _debugWindow.Toggle(true);
        }
    }

    private bool Showing => _settings.Visible || _window.IsOpened();

    public HudOverlay(ICoreClientAPI capi)
    {
        _capi = capi;
        _settings = HudSettings.Load(capi);
        _build = ReadBuild(capi);
        _uiFonts.Update(_settings.FontScale);
        _snap = new SnapGrid(capi, Quiet);
        BuildPanels();
        _window = new HudWindow(capi, _settings, _uiFonts, _snap, View, Badges);
        _debugWindow = new DebugWindow(capi, _settings, this, _snap);
        (_window.Keys, _window.Typing, _window.OpenDebug) =
            (ModKeys, () => _modTyping && _window.Shows(HudWindow.Mods), () => _debugWindow.Toggle(true));
        Options = new OptionsScreen(capi, _settings, Dump, StartBench, OpenVerify, OpenWindow, () => _debugWindow.Toggle(true));
        if (!Assert(_panels.Count == PanelCount) || !NotNull(capi.Event)) return;
        Options.OnOpened += Quiet;
        Options.OnClosed += Quiet;
        Options.Composing += Quiet;
        _settings.RegisterHotkeys(capi, () => _window.Toggle(), () => _debugWindow.Toggle());
        _window.Changed += WindowChanged;
        ShowForBench = ShowTab;
        CaptureForBench = seconds => Capture("record", seconds);
        OptionsForBench = open => _ = open ? Options.Open() : Options.TryClose();
        _window.Composing += Skip;
        _debugWindow.Composing += Skip;
        RegisterDebug(capi);
        RegisterProfile(capi);
        _settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        capi.Event.LevelFinalize += AskForUpdateCheck;
        capi.Event.LevelFinalize += LevelReady;
        Warm(capi.Logger, _uiFonts);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!Showing && !Forced) return;
        if (!Assert(stage is EnumRenderStage.Before or EnumRenderStage.Ortho or EnumRenderStage.Done)) return;
        if (stage == EnumRenderStage.Before) _gpu.Begin();
        else if (stage == EnumRenderStage.Done)
        {
            _gpu.End();
            RenderCost.EndFrame();
        }
        else Render(deltaTime);
    }

    // Registered for three stages: may be called three times
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capi.Event.UnregisterCallback(_saveCallback);
        _window.Changed -= WindowChanged;
        _settings.Changed -= OnSettingsChanged; // a late change would switch the collectors on again
        (ShowForBench, CaptureForBench, OptionsForBench) = (null, null, null);
        _window.Dispose();
        _overlayCanvas?.Dispose();
        _toastCanvas?.Dispose();
        _shadersCanvas?.Dispose();
        _debugWindow.Dispose();
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        _capi.Event.LevelFinalize -= LevelReady;
        Options.OnOpened -= Quiet;
        Options.OnClosed -= Quiet;
        Options.Composing -= Quiet;
        _update?.Dispose();
        _ask?.Dispose();
        _settings.Save(_capi);
        Options.Dispose();
        _snap.Dispose();
        _gpu.Dispose();
        if (_profilerOwned) _capi.World.FrameProfiler.Enabled = false;
        (_debug, KometDebug.Capturing) = (null, false);
        _profile?.Stop();
        ModTimes.Enabled = FrameClock.Stats = Counting.Hud = false;
    }

    double IRenderer.RenderOrder => 0.9;
    int IRenderer.RenderRange => int.MaxValue;

    // A click on "check" is consent for that one check.
    private void RunUpdateCheck()
    {
        if (!Assert(_build.Version.Length > 0) || !Assert(!_disposed)) return;
        if (_update is null)
            _update = new UpdateCheck(_capi.Logger, _build.Version, _build.Preview, _build.Commit, _build.SourcePath);
        else _update.Start();
    }

    // From the options screen, which closes so the window is not hidden behind it
    private void OpenWindow()
    {
        if (!Assert(!_disposed) || !NotNull(_window)) return;
        _ = !Options.IsOpened() || Options.TryClose();
        _window.Toggle(_window.Tab);
    }

    private void OpenVerify()
    {
        if (!Assert(!_disposed) || !NotNull(_window)) return;
        if (_update is null) RunUpdateCheck();
        _ = !Options.IsOpened() || Options.TryClose();
        _window.Toggle(HudWindow.Version);
    }

    // Once the world is up: api.github.com only after a yes.
    private void AskForUpdateCheck()
    {
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        if (_settings.UpdateAsked || !Assert(_build.Version.Length > 0)) return;
        Quiet(); // GuiDialogConfirm composes itself in its constructor
        _ask = new GuiDialogConfirm(_capi, HudText.Translate("update-ask"),
            yes => (_settings.UpdateAsked, _settings.UpdateCheck) = (true, yes));
        _ask.OnClosed += Quiet;
        if (!_ask.TryOpen())
            _capi.Logger.Warning("Komet: update opt-in dialog could not be opened, update notices stay off");
    }

    private void Skip()
    {
        if (!Assert(_skip >= 0) || _disposed) return;
        _skip = 2;
    }

    private void Quiet()
    {
        _quietUntil = Math.Max(_quietUntil, Environment.TickCount64 + QuietMs);
        _ = Assert(_quietUntil > 0);
    }

    private string Means()
    {
        if (!Assert(_panels.Count == PanelCount) || !NotNull(_spikes)) return "";
        var text = new StringBuilder();
        foreach (var panel in _panels.Bounded(PanelCount)) panel.AppendMeans(text);
        if (text.Length > 0) AppendSpikes(text);
        return text.ToString();
    }

    private void OnSettingsChanged()
    {
        if (!Assert(_settings.Interval > 0)) return;
        if (_settings.Visible && !_wasVisible) Quiet();
        _wasVisible = _settings.Visible;
        _window.Invalidate(true);
        _overlayDirty = true;

        if (_settings.UpdateCheck && _update is null) RunUpdateCheck();
        (_sinceInterval, _interval) = ((float)_settings.Interval, 0);
        UpdateProfiler();
        _capi.Event.UnregisterCallback(_saveCallback); // coalesces slider drags and hotkey bursts into one write
        // the options screen pauses singleplayer; without the flag the engine refuses (developer mode crashes)
        _saveCallback = _capi.Event.RegisterCallback(_ => _settings.Save(_capi), 500, permittedWhilePaused: true);
    }

    // Only disable what Komet enabled and Begin() at once, or End() of the running frame crashes without a root entry.
    private void UpdateProfiler()
    {
        var measuring = Forced || Showing;
        var (patched, costPatched, epoch) = (ModTimes.Patched, RenderCost.Patched, Counting.Epoch);
        var threads = _window.Shows(HudWindow.System) || _window.Shows(HudWindow.Threads);
        Counting.Hud = Forced;
        if (Counting.Epoch != epoch)
            foreach (var panel in _panels.Bounded(PanelCount)) panel.Prime();
        ModTimes.Enabled = measuring;
        // opt-in even for the HUD's bench: its probes would skew a bench of the rest
        RenderCost.Enabled = _debug is not null || threads;
        // first switch-on patches the engine this frame: 3-29 ms of Harmony in tests
        if (ModTimes.Patched != patched || RenderCost.Patched != costPatched) Quiet();
        FrameClock.Stats = measuring;
        var profiler = _capi.World.FrameProfiler;
        if (!NotNull(profiler) || !Finite(_benchLeft)) return;
        if (!measuring && _profilerOwned) _profilerOwned = profiler.Enabled = false;
        if (!measuring || profiler.Enabled) return;
        _profilerOwned = profiler.Enabled = true;
        profiler.Begin();
    }

    private void WindowChanged()
    {
        if (_disposed || !Assert(_panels.Count == PanelCount)) return;
        Quiet();
        UpdateProfiler();
        if (_window.Shows(HudWindow.Overview) || _window.Shows(HudWindow.Mods)) RequestAudit();
        if (_window.Shows(HudWindow.Log)) _ = Task.Run(_log.Sample);
        _modTyping &= _window.Shows(HudWindow.Mods);
    }

    private void Dump()
    {
        if (Assert(!_disposed)) Capture("now", 0);
    }

    private void Copy(string report, string message)
    {
        if (!Assert(message.Length > 0) || !Assert(report.Length > 0)) return;
        _capi.Input.ClipboardText = report;
        _capi.Logger.Notification("{0}", report);
        _capi.ShowChatMessage(HudText.Translate(message));
    }

    private void StartBench(float seconds)
    {
        if (!Assert(seconds is > 0 and <= 3600) || !Assert(_panels.Count > 0)) return;
        if (_debug is not null)
        {
            _capi.ShowChatMessage(HudText.Translate("cmd-debug-running"));
            return;
        }

        var running = _benchLeft > 0; // not restarted, only its time left is told
        _capi.ShowChatMessage(HudText.Translate("hud-bench-start", running ? MathF.Ceiling(_benchLeft) : seconds));
        if (running) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetBench();
        _frames.ResetHistory();
        _passes.ResetWorst();
        _spikes.Reset();
        _benchLeft = seconds;
        UpdateProfiler();
    }

    private void Bench(float elapsed)
    {
        if (!Assert(elapsed > 0) || !Assert(_benchLeft > 0)) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Accumulate();
        _benchLeft -= elapsed;
        if (_benchLeft > 0) return;
        _spikes.Rank(); // the report reads the ledger's rows, which otherwise wait for the next interval
        Copy(Means(), "hud-bench-done");
        UpdateProfiler();
    }

    private void Render(float deltaTime)
    {
        if (!Assert(deltaTime is >= 0 and < 10)) return; // NaN fails too
        var profiler = _capi.World.FrameProfiler;
        // Without this mark the Ortho renderers before the HUD were booked to the HUD
        profiler.Mark("ortho-before-komet-hud");
        var steady = Steady();
        if (FrameClock.Completed != _consumed) Record(FrameClock.Last, profiler, steady);
        else _frames.Record(deltaTime, float.NaN, steady);

        (_sinceInterval, _frameTotal, _elapsedTotal) =
            (_sinceInterval + deltaTime, _frameTotal + 1, _elapsedTotal + deltaTime);
        var interval = _sinceInterval >= _settings.Interval;
        if (interval) EndInterval();
        ProfileFrame();
        profiler.Mark("komet-hud-interval");
        Toast();
        profiler.Mark("komet-hud-panel");
        if (_settings.Visible) DrawOverlay();
        DrawShaders(); // the HUD shown or not
        profiler.Mark("komet-hud");
    }

    private void Record(in FrameRecord frame, FrameProfilerUtil profiler, bool steady)
    {
        var consumed = _consumed;
        _consumed = FrameClock.Completed; // taken even when broken, or the HUD would stop at it
        if (!Assert(frame.Index >= consumed) || !Assert(frame.DtMs >= 0) || !Finite(frame.GcMs)) return;
        _frames.Record((float)(frame.DtMs / 1000), (float)frame.GcMs, steady);
        if (profiler.Enabled && frame.Root != null) _passes.AddFrame(frame, steady);
        else _passes.AddFrame(null, steady); // clears the previous frame's marks
        _ = _spikes.Add(frame, _passes.FrameTop, steady, _capi.InWorldEllapsedMilliseconds / 1000.0);
        if (_debug is { } debug) DebugFrame(debug, frame, steady);
    }

    // The world clock stops while loading or paused, the resuming frame carries the gap; the initial chunk burst is
    // load cost
    private bool Steady()
    {
        var inWorld = _capi.InWorldEllapsedMilliseconds;
        _settled |= inWorld > SettleMs ||
                    (RuntimeStats.chunksAwaitingTesselation == 0 && RuntimeStats.chunksAwaitingPooling == 0);
        if (!Assert(inWorld >= 0) || !_settled || _capi.IsGamePaused || inWorld < WarmupMs)
        {
            _resumed = true;
            return false;
        }

        if (Environment.TickCount64 < _quietUntil) return false;
        if (_skip > 0)
        {
            _skip--;
            return false;
        }

        if (!_resumed) return true;
        _resumed = false;
        return false;
    }

    private void EndInterval()
    {
        if (!Assert(_sinceInterval > 0) || !Assert(_frames.Frames > 0))
        {
            _sinceInterval = 0;
            return;
        }

        _frames.SampleWindow();
        _passes.UpdateOrder();
        _spikes.Rank();
        if (ModTimes.Enabled) _timings.Update(_frames.Frames, _sinceInterval);
        _gpu.Sample();
        RenderCost.Sample();
        if (_window.Shows(HudWindow.Render)) OcclusionCulling.Sample();
        _capi.World.FrameProfiler.Mark("komet-hud-readback"); // the GPU's numbers, each read a wait for Mesa's glthread
        if (_interval % HudSettings.SlowEvery == 0)
        {
            _gpu.SampleVram(_window.IsOpened() || Forced);
            if (_sampling?.Exception?.GetBaseException() is { } failed)
                _capi.Logger.Warning("Komet HUD: sampling failed ({0})", failed.Message);
            if (_sampling?.IsCompleted != false) _sampling = Task.Run(_sample ??= SampleSlow);
        }

        if (Showing) TakeLive();
        _window.Invalidate();
        _overlayDirty = true;
        if (_benchLeft > 0) Bench(_sinceInterval);
        if (_debug is not null) DebugInterval(_sinceInterval);
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetPeaks();
        _frames.Reset();
        _passes.Reset();
        (_sinceInterval, _interval) = (0, _interval + 1);
    }

    private void LevelReady()
    {
        _capi.Event.LevelFinalize -= LevelReady;
        ModTimes.Ready(); // every mod has started and patched
    }

    private void SampleSlow()
    {
        if (!Assert(!_disposed) || !Assert(_panels.Count == PanelCount)) return;
        _resources.Sample();
        if (_window.Shows(HudWindow.Log)) _log.Sample();
    }
}
