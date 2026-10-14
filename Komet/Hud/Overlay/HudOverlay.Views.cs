using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Vintagestory.Client;

namespace Komet.Hud;

// Window tabs from HudLive. A hint names what stands out and fixes it on click; it goes once its cause has.
internal sealed partial class HudOverlay
{
    private const int MaxViewHints = 8, ShownSpikesInTab = 5, ShownLogLines = 40;
    private static readonly Rgba[] PassColors =
    [
        new(150 / 255.0, 120 / 255.0, 220 / 255.0, 1), new(110 / 255.0, 150 / 255.0, 230 / 255.0, 1),
        new(80 / 255.0, 170 / 255.0, 200 / 255.0, 1), new(89 / 255.0, 191 / 255.0, 115 / 255.0, 1),
        new(160 / 255.0, 200 / 255.0, 90 / 255.0, 1), new(200 / 255.0, 190 / 255.0, 60 / 255.0, 1),
        new(230 / 255.0, 150 / 255.0, 50 / 255.0, 1), Rgba.White(0.35), Rgba.White(0.15)
    ];

    private static readonly Rgba Yellow = new(200 / 255.0, 190 / 255.0, 60 / 255.0, 1), Orange = new(230 / 255.0, 150 / 255.0, 50 / 255.0, 1);
    private readonly LogStats _log = new("client-main.log");
    private bool _vulkanOpen, _renderMore;
    private string _logFilter = "all";
    private readonly HashSet<string> _logOpen = [];

    private static string T(string key, params object[] args) => HudText.Translate(key, args);

    private (string Text, Rgba Fill, Rgba Color, Action? Click)[] Badges()
    {
        var (text, fill, color) = EditionBadge();
        Action open = () => _window.Select(HudWindow.Version);
        return CheckBadge() is { } check
            ? [(text, fill, color, open), (check.Text, check.Fill, check.Color, open)]
            : [(text, fill, color, open)];
    }

    private static string N(double value, int decimals = 0) => HudText.Num(value, decimals);

    private void View(HudUi ui, int tab)
    {
        if (!NotNull(ui) || !Index(tab, HudWindow.Tabs.Length)) return;
        switch (tab)
        {
            case HudWindow.Overview: OverviewView(ui); break;
            case HudWindow.Frames: FramesView(ui); break;
            case HudWindow.System: SystemView(ui); break;
            case HudWindow.Render: RenderView(ui); break;
            case HudWindow.Threads: ThreadsView(ui); break;
            case HudWindow.Mods: ModsView(ui); break;
            case HudWindow.Log: LogView(ui); break;
            case HudWindow.Version: VersionView(ui); break;
            default: SettingsView(ui); break;
        }
    }

    private void OverviewView(HudUi ui)
    {
        if (!Assert(_live.PassCount >= 0)) return;
        var l = _live;
        ui.Space(5);
        ui.Grid(4, 4, 5, (i, x, y, w) => i switch
        {
            0 => ui.Tile(x, y, w, N(l.Fps), "FPS"),
            1 => ui.Tile(x, y, w, N(l.Low1), "1% Low"),
            2 => ui.Tile(x, y, w, N(l.AvgMs, 1), T("hud-v-frametime-ms")),
            _ => ui.Tile(x, y, w, N(l.GpuMs, 1), T("hud-v-gpu-ms"))
        });
        ui.Label(T("hud-v-graph", Math.Min(_frames.Recorded, HudCanvas.GraphFrames)));
        Graph(ui, ui.Px(48), false);
        ui.Label(T("hud-v-hints"));
        if (_spikes.Stored > 0)
        {
            var spike = _spikes.Latest(0);
            var text = T("hud-v-spikes-last", _spikes.Count, Describe(spike.Cause), N(spike.DtMs));
            var w = ui.TextW(ui.F.Body, text) + ui.Px(6);
            ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, text, ui.W - ui.Px(60)), HudUi.Warn);
            _ = ui.LinkText(Math.Min(ui.X + w, ui.X + ui.W - ui.Px(56)), ui.Y, T("hud-v-look"), () => _window.Select(HudWindow.Frames));
            ui.Space(0);
            ui.Y += ui.F.BodyRow;
        }

        var hints = ViewHints();
        foreach (var hint in hints.Bounded(MaxViewHints)) HintRow(ui, hint);
        if (hints.Length == 0) ui.Line(T("hud-v-fine"), HudUi.Ok);
        ui.Space(4);
        _ = ui.LinkText(ui.X, ui.Y, T("hud-v-make-protocol"), () => _debugWindow.Toggle(true));
        ui.Y += ui.F.BodyRow;
    }

    private readonly record struct ViewHint(Rgba Color, string Title, string Sub, string Chip, Action? Act);

    private static void HintRow(HudUi ui, ViewHint hint)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var chipW = hint.Act is null ? 0 : ui.TextW(ui.F.Small, hint.Chip) + ui.Px(14);
        var textW = ui.W - chipW - ui.Px(8);
        var top = ui.Y + ui.Px(2);
        ui.Y = top;
        ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, hint.Title, textW), hint.Color);
        if (hint.Act is { } act) _ = ui.Chip(ui.X + ui.W - chipW, top + ui.Px(1), hint.Chip, false, act);
        ui.Y += ui.F.BodyRow;
        if (hint.Sub.Length > 0) ui.Line(ui.Fit(ui.F.Small, hint.Sub, textW), HudUi.Dim, ui.F.Small);
        ui.Space(2);
    }

    private ViewHint[] ViewHints()
    {
        var hints = new List<ViewHint>(MaxViewHints);
        var latency = Knobs.Find("GcLatency");
        if (latency >= 0 && _settings.Knobs.Knob(latency) == 0)
            hints.Add(new ViewHint(HudUi.Err, T("hud-v-hint-gclatency"), T("hud-v-hint-gclatency-sub"), T("hud-v-apply"),
                () => _settings.Knobs.SetKnob(latency, 1)));
        if (_resources.GcPausePercent >= GcHintPercent)
            hints.Add(new ViewHint(HudUi.Err, T("hud-v-hint-gc", N(_resources.GcPausePercent, 1)), T("hud-v-hint-gc-sub"),
                T("hud-v-record"), () => Capture("record", 10)));
        TessHint(hints);
        if (_audit is { } audit && audit.Methods.Count(static m => m.Risk == PatchRisk.High) is var risky and > 0)
            hints.Add(new ViewHint(HudUi.Warn, T("hud-v-hint-conflicts", risky), RiskySample(audit), T("hud-v-look"), ShowConflicts));
        ModHints(hints);
        if (100 * _gpu.VramUsedMb / _gpu.VramTotalMb >= VramHintPercent)
            hints.Add(new ViewHint(HudUi.Err, T("hud-v-hint-vram", N(100 * _gpu.VramUsedMb / _gpu.VramTotalMb)), T("hud-v-hint-vram-sub"),
                "", null));
        if (_live.Low1 < LowHintFps)
            hints.Add(new ViewHint(HudUi.Warn, T("hud-v-hint-low", N(_live.Low1)), T("hud-v-hint-low-sub"), T("hud-v-arm"),
                SpikeCapture));
        if (Features.NotActive > 0)
            hints.Add(new ViewHint(HudUi.Warn, T("hud-v-hint-features", Features.NotActive), Features.FirstNotActive(), T("hud-v-look"),
                () => _window.Select(HudWindow.Mods)));
        if (DebugBuild) hints.Add(new ViewHint(HudUi.Warn, T("hud-hint-debugbuild"), "", "", null));
        if (_update?.Report is { State: UpdateState.Outdated or UpdateState.Mismatch } report)
            hints.Add(new ViewHint(report.State == UpdateState.Mismatch ? HudUi.Err : HudUi.Warn,
                T(report.State == UpdateState.Mismatch ? "hud-v-hint-mismatch" : "hud-v-hint-outdated", report.Newest), T("hud-v-hint-version-sub"),
                T("hud-v-look"), () => _window.Select(HudWindow.Version)));
        return Assert(hints.Count <= 16) ? [.. hints.Take(MaxViewHints)] : [];
    }

    private void TessHint(List<ViewHint> hints)
    {
        if (!NotNull(hints) || !Assert(hints.Count <= MaxViewHints)) return;
        var priority = Knobs.Find("TessPriority");
        if (priority < 0 || !(_live.RunQueueMs >= 0.5) || _settings.Knobs.Knob(priority) <= 25) return;
        var now = _settings.Knobs.Knob(priority);
        hints.Add(new ViewHint(HudUi.Warn, T("hud-v-hint-runqueue", N(_live.RunQueueMs, 1)), T("hud-v-hint-runqueue-sub", now),
            T("hud-v-apply"), () => _settings.Knobs.SetKnob(priority, 25)));
        _ = Assert(now > 25);
    }

    private void ModHints(List<ViewHint> hints)
    {
        if (!NotNull(hints) || !Assert(hints.Count <= MaxViewHints)) return;
        for (var i = 0; i < ModTimes.MaxMods; i++)
        {
            var (mod, ms) = (_timings.ModName(i), _timings.ModMs(i));
            if (mod.Length == 0 || mod is "game" or KometModSystem.ModId || !(ms >= ModHintMs)) continue;
            hints.Add(new ViewHint(Rgba.White(0.85), T("hud-v-hint-mod", ModName(mod), N(ms, 2)), T("hud-v-hint-mod-sub"),
                T("hud-v-measure"), () => ShowProfile(mod, true)));
        }

        _ = Assert(hints.Count <= 32);
    }

    private void Graph(HudUi ui, double height, bool guides)
    {
        if (!NotNull(ui) || !Assert(height > 0)) return;
        var n = Math.Min(_frames.Recorded, HudCanvas.GraphFrames);
        var first = _frames.HistoryLength - n;
        if (n < 2)
        {
            ui.Fill(ui.X, ui.Y, ui.W, height, HudUi.TileBack);
            ui.Y += height;
            return;
        }

        ui.Graph(height, n, i => _frames.HistoryMs(first + i), guides);
        _ = Assert(first >= 0);
    }

    private void FramesView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var l = _live;
        ui.Space(6);
        ui.Grid(5, 5, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "FPS", N(l.Fps)),
            1 => ui.Stat(x, y, w, T("hud-v-avg-ms"), N(l.AvgMs, 1)),
            2 => ui.Stat(x, y, w, T("hud-v-p99-ms"), N(l.P99, 1)),
            3 => ui.Stat(x, y, w, "1% Low", N(l.Low1)),
            _ => ui.Stat(x, y, w, "0,1% Low", N(l.Low01))
        });
        ui.Label(T("hud-v-graph-big", Math.Min(_frames.Recorded, HudCanvas.GraphFrames)));
        Graph(ui, ui.Px(110), true);
        ui.Label(T("hud-v-distribution"));
        ui.Segments(ui.Y, ui.Px(10), [(l.Under8, HudUi.Ok), (l.Under12, Yellow), (l.Under25, Orange), (Math.Max(l.Over25, l.Over25 > 0 ? 0.3 : 0), HudUi.Err)]);
        ui.Y += ui.Px(12);
        ui.Grid(4, 4, 4, (i, x, y, _) =>
        {
            var (text, color) = i switch
            {
                0 => ("<8 ms " + N(l.Under8, 0) + " %", HudUi.Dim),
                1 => ("8-12 ms " + N(l.Under12, 0) + " %", HudUi.Dim),
                2 => ("12-25 ms " + N(l.Under25, 1) + " %", HudUi.Dim),
                _ => (">25 ms " + N(l.Over25, 1) + " %", HudUi.Err)
            };
            ui.Text(x, y, ui.F.SmallRow, ui.F.Small, text, color);
            return ui.F.SmallRow;
        });
        SpikeList(ui);
    }

    private void SpikeList(HudUi ui)
    {
        if (!NotNull(ui) || !NotNull(_spikes)) return;
        var stored = Math.Min(_spikes.Stored, ShownSpikesInTab);
        ui.Label(_spikes.Count > 0 ? T("hud-v-spikes-count", _spikes.Count) : T("hud-v-spikes"));
        if (stored == 0) ui.Line(T("hud-v-spikes-none"), HudUi.Dim);
        var now = _capi.InWorldEllapsedMilliseconds / 1000.0;
        for (var age = 0; age < Math.Min(stored, ShownSpikesInTab); age++)
        {
            var spike = _spikes.Latest(age);
            var ms = N(spike.DtMs) + " ms";
            var right = T("hud-v-ago", N(Math.Max(0, now - spike.AtSeconds)));
            ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ms, HudUi.Warn);
            var x = ui.X + ui.TextW(ui.F.Body, ms) + ui.Px(5);
            ui.Text(x, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, Describe(spike.Cause), ui.X + ui.W - x - ui.TextW(ui.F.Body, right) - ui.Px(8)));
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.BodyRow, ui.F.Body, right, HudUi.Dim);
            ui.Y += ui.F.BodyRow;
        }

        _ = Assert(stored <= ShownSpikesInTab);
    }

    private void SystemView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var r = _resources;
        static double gb(double mb) => mb / 1024;
        ui.Space(6);
        ui.Grid(2, 6, 5, (i, x, y, w) => i switch
        {
            0 => ui.Meter(x, y, w, T("hud-v-cpu-process"), N(r.CpuPercent) + " %", r.CpuPercent),
            1 => ui.Meter(x, y, w, T("hud-v-cpu-system"), N(r.SystemCpuPercent) + " %", r.SystemCpuPercent),
            2 => ui.Meter(x, y, w, T("hud-v-ram"), N(gb(r.UsedRamMb), 1) + " / " + N(gb(r.TotalRamMb), 1) + " GB",
                r.PercentOfRam(r.UsedRamMb)),
            3 => ui.Meter(x, y, w, "VRAM", N(gb(_gpu.VramUsedMb), 1) + " / " + N(gb(_gpu.VramTotalMb), 0) + " GB",
                100 * _gpu.VramUsedMb / _gpu.VramTotalMb),
            4 => ui.Meter(x, y, w, T("hud-v-working-set"), N(gb(r.WorkingSetMb), 1) + " GB", r.PercentOfRam(r.WorkingSetMb)),
            _ => ui.Meter(x, y, w, T("hud-v-gc-pause"), N(r.GcPausePercent, 1) + " %", r.GcPausePercent * 10)
        });
        ui.Label(T("hud-v-memory"));
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "Managed Heap", N(gb(r.ManagedUsedMb), 1) + " GB", T("hud-v-reserved", N(gb(r.ManagedCommittedMb), 1))),
            1 => ui.Stat(x, y, w, T("hud-v-alloc"), N(r.AllocatedMbPerSec) + " MB/s",
                T("hud-v-per-frame-mb", N(r.AllocatedMbPerSec / Math.Max(1, _live.Fps), 1))),
            _ => ui.Stat(x, y, w, "GC", "Gen0 " + N(r.Gen0PerSec, 1) + "/s", "Gen2 " + N(r.Gen2PerSec, 2) + "/s · " +
                System.Runtime.GCSettings.LatencyMode)
        });
        ui.Label(T("hud-v-world"));
        var loaded = RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded;
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "Chunks", N(loaded),
                T("hud-v-chunks-sub", N(RuntimeStats.chunksAwaitingTesselation), N(RuntimeStats.chunksAwaitingPooling))),
            1 => ui.Stat(x, y, w, "Entities", N(_capi.World.LoadedEntities.Count), T("hud-v-visible", N(RuntimeStats.renderedEntities))),
            _ => ui.Stat(x, y, w, "Server", T(KometModSystem.LocalServer ? "hud-v-local" : "hud-v-remote"),
                T("hud-v-viewdistance", Vintagestory.Client.NoObf.ClientSettings.ViewDistance))
        });
        ThreadMeters(ui);
    }

    private void ThreadMeters(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Label(T("hud-v-threads-core"));
        var l = _live;
        var (main, gl) = (l.ThreadCount > 0 ? l.Threads[0].Busy : double.NaN, l.ThreadCount > 1 ? l.Threads[1].Busy : double.NaN);
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Meter(x, y, w, "Main", N(main) + " %", main),
            1 => ui.Meter(x, y, w, T("hud-v-tess-workers", WorkerPool.Running), N(l.WorkerTess) + " %", l.WorkerTess),
            _ => ui.Meter(x, y, w, "glthread", N(gl) + " %", gl)
        });
    }

    private void RenderView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (l, vk) = (_live, _live.Vulkan);
        var running = Komet.Vulkan.VulkanRenderer.State == Komet.Vulkan.VulkanRenderer.Phase.Running;
        ui.Space(8);
        var y = ui.Y;
        ui.Fill(ui.X, y + ui.F.StrongRow / 2 - ui.Px(3.5), ui.Px(7), ui.Px(7), running ? HudUi.Ok : HudUi.Dim, ui.Px(3.5));
        ui.Text(ui.X + ui.Px(12), y, ui.F.StrongRow, ui.F.Strong, Komet.Options.VulkanMode.Renderer());
        ui.TextRight(ui.X + ui.W, y, ui.F.StrongRow, ui.F.Small, running ? N(vk.MemoryMb / 1024.0, 1) + " GB" : "OpenGL", HudUi.Dim);
        ui.Y = y + ui.F.StrongRow + ui.Px(6);
        var share = vk.Draws + vk.Left > 0 ? 100 * vk.Draws / (vk.Draws + vk.Left) : 0;
        ui.Segments(ui.Y, ui.Px(4), [(share, HudUi.Accent), (100 - share, Rgba.White(0.35))], 100);
        ui.Y += ui.Px(7);
        DrawShare(ui, running, share, vk);
        ui.Label(T("hud-v-frame-goes", N(l.TotalMs > 0 ? l.TotalMs : l.AvgMs, 1)));
        Span<(double, Rgba)> parts = stackalloc (double, Rgba)[HudLive.MaxPasses];
        for (var i = 0; i < Math.Min(l.PassCount, HudLive.MaxPasses); i++) parts[i] = (l.Passes[i].Ms, PassColors[i % PassColors.Length]);
        ui.Segments(ui.Y, ui.Px(12), parts[..l.PassCount]);
        ui.Y += ui.Px(16);
        ui.Grid(3, l.PassCount, 12, (i, x, py, w) =>
        {
            ui.Fill(x, py + (ui.F.SmallRow - ui.Px(7)) / 2, ui.Px(7), ui.Px(7), PassColors[i % PassColors.Length]);
            ui.Text(x + ui.Px(11), py, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, PassName(l.Passes[i].Key), w - ui.Px(48)));
            ui.TextRight(x + w, py, ui.F.SmallRow, ui.F.Small, N(l.Passes[i].Ms, 2));
            return ui.F.SmallRow;
        });
        BigNumbers(ui);
        RenderFooter(ui, vk);
    }

    private void DrawShare(HudUi ui, bool running, double share, (double Draws, double Left, double Segments, long MemoryMb, string[] Closers) vk)
    {
        // what Vulkan leaves is missing, OpenGL draws none of it
        var line = running ? T("hud-v-vk-only-share", N(share)) : T("hud-v-vk-off");
        ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small, line, HudUi.Dim);
        if (running && vk.Left > 0)
        {
            var link = T("hud-v-vk-missing", N(vk.Left, 1));
            var x = ui.X + ui.W - ui.TextW(ui.F.Small, link) - ui.Px(11);
            _ = ui.LinkText(x, ui.Y, link, () => _vulkanOpen = !_vulkanOpen, ui.F.Small);
            _ = ui.Caret(ui.X + ui.W - ui.Px(7), ui.Y, ui.F.SmallRow, _vulkanOpen, HudUi.Link);
        }

        ui.Y += ui.F.SmallRow;
        if (_vulkanOpen && vk.Closers.Length > 0) ui.Line(string.Join(", ", vk.Closers), HudUi.Dim, ui.F.Small);
        _ = Assert(share is >= 0 and <= 100.001);
    }

    private void BigNumbers(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var l = _live;
        ui.Space(16);
        ui.Grid(4, 4, 8, (i, x, y, _) =>
        {
            var (value, label) = i switch
            {
                0 => (N(l.GpuMs, 1) + " ms", T("hud-v-gpu-time")),
                1 => (N(l.DrawCalls), "Draw Calls"),
                2 => (N(l.Triangles / 1e6, 1) + " M", T("hud-v-triangles")),
                _ => (double.IsFinite(l.CulledPercent) ? N(l.CulledPercent) + " %" : "–", T("hud-v-hidden"))
            };
            ui.Text(x, y, ui.F.BigRow, ui.F.Big, value);
            ui.Text(x, y + ui.F.BigRow, ui.F.SmallRow, ui.F.Small, label, HudUi.Dim);
            return ui.F.BigRow + ui.F.SmallRow;
        });
    }

    private void RenderFooter(HudUi ui, (double Draws, double Left, double Segments, long MemoryMb, string[] Closers) vk)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Space(16);
        var line = T("hud-v-render-footer-only", N(vk.Segments, 1), N(vk.MemoryMb / 1024.0, 1), N(vk.Draws), N(_live.DrawCalls));
        var more = T(_renderMore ? "hud-v-less" : "hud-v-more");
        ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, line, ui.W - ui.TextW(ui.F.Small, more) - ui.Px(8)), HudUi.Dim);
        _ = ui.LinkText(ui.X + ui.W - ui.TextW(ui.F.Small, more), ui.Y, more, () => _renderMore = !_renderMore, ui.F.Small);
        ui.Y += ui.F.SmallRow;
        if (!_renderMore) return;
        ui.Space(3);
        if (Komet.Vulkan.VulkanRenderer.Status is { Length: > 0 } status) ui.Wrap(status, HudUi.Dim, ui.F.Small);
        if (Komet.Vulkan.VulkanCore.Enabled) ui.Wrap(Komet.Vulkan.VulkanCore.Status, HudUi.Dim, ui.F.Small);
        ui.Wrap(UploadCapText(), HudUi.Dim, ui.F.Small);
    }

    private static string PassName(string key) =>
        Assert(key.Length > 0) && HudText.Cached("hud-pass-", key) is var name && !name.StartsWith("hud-", StringComparison.Ordinal)
            ? name
            : key.TrimStart('~');

    private void ThreadsView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var l = _live;
        var main = l.ThreadCount > 0 ? l.Threads[0].Busy : double.NaN;
        var tess = Knobs.Find("TessPriority") is var k and >= 0 ? _settings.Knobs.Knob(k) : 0;
        ui.Space(6);
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Meter(x, y, w, T("hud-v-main-thread"), N(main) + " %", main),
            1 => ui.Stat(x, y, w, T("hud-v-waits-core"), N(l.RunQueueMs, 2) + " ms", T("hud-v-per-frame")),
            _ => ui.Stat(x, y, w, T("hud-v-tess-worker-stat"), N(WorkerPool.Running), T("hud-v-priority", tess))
        });
        ui.Label(T("hud-v-all-threads"));
        ThreadRow(ui, T("hud-v-thread"), double.NaN, T("hud-v-busy"), "ms/F", true);
        for (var i = 0; i < Math.Min(l.ThreadCount, HudLive.MaxThreads); i++)
            ThreadRow(ui, l.Threads[i].Name, l.Threads[i].Busy, N(l.Threads[i].Busy) + " %", N(l.Threads[i].MsPerFrame, 2), false);
        ThreadRow(ui, T("hud-v-workers-frame", WorkerPool.Running), l.WorkerFrame, N(l.WorkerFrame) + " %", "", false);
        ThreadRow(ui, T("hud-v-workers-tess", WorkerPool.Running), l.WorkerTess, N(l.WorkerTess) + " %", "", false);
        if (l.ThreadCount == 0) ui.Line(T("hud-rc-nothreads"), HudUi.Dim, ui.F.Small);
        ui.Label(T("hud-v-pool-locks"));
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "Thread-Pool", N(ThreadPool.ThreadCount), T("hud-v-waiting", N(ThreadPool.PendingWorkItemCount))),
            1 => ui.Stat(x, y, w, T("hud-v-locks"), N(l.Locks) + "/s"),
            _ => ui.Stat(x, y, w, T("hud-v-gc-pause"), N(_resources.GcPausePercent, 1) + " %")
        });
    }

    private static void ThreadRow(HudUi ui, string name, double busy, string busyText, string ms, bool head)
    {
        if (!NotNull(ui) || !NotNull(name)) return;
        var (h, f) = (head ? ui.F.SmallRow : ui.F.BodyRow, head ? ui.F.Small : ui.F.Body);
        var barX = ui.X + ui.W - ui.Px(110 + 54 + 46 + 16);
        ui.Text(ui.X, ui.Y, h, f, ui.Fit(f, name, barX - ui.X - ui.Px(8)), head ? HudUi.Dim : null);
        if (!head) ui.Bar(barX, ui.Y, ui.Px(62), h, busy);
        ui.Text(barX + (head ? 0 : ui.Px(68)), ui.Y, h, f, busyText, head ? HudUi.Dim : null);
        ui.TextRight(ui.X + ui.W, ui.Y, h, f, ms, head ? HudUi.Dim : null);
        ui.Fill(ui.X, ui.Y + h, ui.W, Math.Max(1, ui.Px(1)), Rgba.White(0.05));
        ui.Y += h + ui.Px(2);
        _ = Assert(barX > ui.X);
    }

    private void LogView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Space(5);
        var x = ui.Chips(ui.X, ui.Y, [(T("hud-v-all"), _logFilter == "all", () => _logFilter = "all"),
            (T("hud-v-warnings"), _logFilter == "wn", () => _logFilter = "wn"), (T("hud-v-errors"), _logFilter == "er", () => _logFilter = "er")]);
        var hint = T("hud-v-log-newest");
        ui.TextRight(ui.X + ui.W, ui.Y, ui.ChipHeight, ui.F.Small, hint, HudUi.Dim);
        var room = ui.X + ui.W - ui.TextW(ui.F.Small, hint) - x - ui.Px(18);
        if (room > ui.Px(40)) ui.Text(x + ui.Px(6), ui.Y, ui.ChipHeight, ui.F.Small, ui.Fit(ui.F.Small, "client-main.log", room), HudUi.Faint);
        ui.Y += ui.ChipHeight + ui.Px(6);
        var shown = 0;
        for (var back = 0; back < LogStats.KeepEntries && shown < ShownLogLines; back++)
        {
            if (_log.Entry(back) is not { } entry) break;
            if ((_logFilter == "wn" && entry.Level < LogLevel.Warning) || (_logFilter == "er" && entry.Level != LogLevel.Error)) continue;
            LogEntryRow(ui, entry, shown % 2 == 1);
            shown++;
        }

        if (shown == 0) ui.Line(T("hud-v-log-empty"), HudUi.Dim);
    }

    private void LogEntryRow(HudUi ui, LogEntry entry, bool odd)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (pad, column) = (ui.Px(4), ui.TextW(ui.F.Small, "00:00:00") + ui.Px(18));
        var key = entry.Time + entry.Text;
        _ = Assert(key.Length > 0) && NotNull(_logOpen);
        var open = _logOpen.Contains(key);
        var lines = ui.Lines(entry.Text, ui.F.Body, ui.W - column - ui.Px(6), open ? 24 : 2);
        var h = Math.Max(lines.Length * ui.F.BodyRow, 2 * ui.F.SmallRow) + 2 * pad;
        var (color, stripe) = entry.Level switch
        {
            LogLevel.Error => (HudUi.Err, HudUi.Err), LogLevel.Warning => (HudUi.Warn, HudUi.Warn), LogLevel.Debug => (HudUi.Dim, (Rgba?)null),
            _ => (Rgba.White(0.86), null)
        };
        if (odd || open) ui.Fill(ui.X, ui.Y, ui.W, h, Rgba.White(open ? 0.06 : 0.025), ui.Px(3));
        if (stripe is { } s) ui.Fill(ui.X, ui.Y + pad, ui.Px(3), h - 2 * pad, s, ui.Px(1.5));
        ui.Text(ui.X + ui.Px(9), ui.Y + pad, ui.F.SmallRow, ui.F.Small, entry.Time, HudUi.Dim);
        if (entry.Source.Length > 0)
            ui.Text(ui.X + ui.Px(9), ui.Y + pad + ui.F.SmallRow, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, entry.Source, column - ui.Px(12)),
                entry.Source == "komet" ? HudUi.Link : HudUi.Faint);
        for (var i = 0; i < Math.Min(lines.Length, 24); i++)
            ui.Text(ui.X + column, ui.Y + pad + i * ui.F.BodyRow, ui.F.BodyRow, ui.F.Body, lines[i], color);
        ui.Click(ui.X, ui.Y, ui.W, h, () => _ = _logOpen.Add(key) || _logOpen.Remove(key));
        ui.Y += h + ui.Px(2);
    }

    private void SettingsView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var s = _settings;
        ui.Header(T("hud-v-overlay-shows"));
        _ = ui.Chips(ui.X, ui.Y, [("FPS", s.ShowFps, () => s.ShowFps = !s.ShowFps), ("1% Low", s.ShowLows, () => s.ShowLows = !s.ShowLows),
            (T("hud-v-frametime"), s.ShowFrametime, () => s.ShowFrametime = !s.ShowFrametime), ("Graph", s.ShowGraph, () => s.ShowGraph = !s.ShowGraph),
            (T("hud-v-mods-total"), s.ShowMods, () => s.ShowMods = !s.ShowMods), (T("hud-v-pinned"), s.ShowPins, () => s.ShowPins = !s.ShowPins)]);
        ui.Y += ui.ChipHeight + ui.Px(2);
        ui.Header(T("hud-v-look-section"));
        ChipRow(ui, T("hud-v-position"), [(T("hud-v-pos-tl"), s.Corner == HudCorner.TopLeft, () => s.Corner = HudCorner.TopLeft),
            (T("hud-v-pos-tr"), s.Corner == HudCorner.TopRight, () => s.Corner = HudCorner.TopRight),
            (T("hud-v-pos-bl"), s.Corner == HudCorner.BottomLeft, () => s.Corner = HudCorner.BottomLeft),
            (T("hud-v-pos-br"), s.Corner == HudCorner.BottomRight, () => s.Corner = HudCorner.BottomRight)]);
        ChipRow(ui, T("hud-v-opacity"), [("70 %", Near(s.Opacity, 0.7), () => s.Opacity = 0.7), ("85 %", Near(s.Opacity, 0.85), () => s.Opacity = 0.85),
            ("100 %", Near(s.Opacity, 1), () => s.Opacity = 1)]);
        ChipRow(ui, T("hud-v-fontsize"), [("90 %", Near(s.FontScale, 0.9), () => s.FontScale = 0.9), ("100 %", Near(s.FontScale, 1), () => s.FontScale = 1),
            ("115 %", Near(s.FontScale, 1.15), () => s.FontScale = 1.15)]);
        ui.Header(T("hud-v-spike-hints"));
        ChipRow(ui, T("hud-v-show-from"), [(T("hud-off"), s.ToastMs <= 0, () => s.ToastMs = 0), ("25 ms", Near(s.ToastMs, 25), () => s.ToastMs = 25),
            ("50 ms", Near(s.ToastMs, 50), () => s.ToastMs = 50), ("100 ms", Near(s.ToastMs, 100), () => s.ToastMs = 100)]);
        ui.Line(T("hud-v-toast-rate"), HudUi.Dim, ui.F.Small);
        ui.Header(T("hud-v-keys"));
        ui.Row(T("hud-v-key-overlay"), "F7");
        ui.Row(T("hud-v-key-window"), T("hud-v-ctrl") + "+F7");
        ui.Row(T("hud-v-key-debug"), T("hud-v-ctrl") + "+F8");
        ui.Space(6);
        ui.Wrap(T("hud-v-remembered"), HudUi.Dim, ui.F.Small);
        _ = ui.LinkText(ui.X, ui.Y, T("hud-v-open-options"), () => Options.Open(KometPages.Hud), ui.F.Small);
        ui.Y += ui.F.SmallRow;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.01;

    private static void ChipRow(HudUi ui, string label, ReadOnlySpan<(string Text, bool On, Action Click)> chips)
    {
        double width = 0;
        foreach (var (text, _, _) in chips.Bounded(8)) width += ui.TextW(ui.F.Small, text) + ui.Px(18);
        var h = Math.Max(ui.F.BodyRow, ui.ChipHeight);
        ui.Text(ui.X, ui.Y, h, ui.F.Body, label);
        _ = ui.Chips(ui.X + ui.W - width + ui.Px(4), ui.Y + (h - ui.ChipHeight) / 2, chips);
        ui.Y += h + ui.Px(2);
        _ = Assert(width > 0);
    }
}
