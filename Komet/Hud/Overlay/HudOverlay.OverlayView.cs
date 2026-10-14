using Komet.Diagnostics;

namespace Komet.Hud;

// Overlay (F7) and spike toast, composed per interval and on settings change.
internal sealed partial class HudOverlay
{
    private const double OverlayWidth = 212, ToastEverySeconds = 3, ToastShownSeconds = 5;
    private readonly HudUiFonts _uiFonts = new();
    private HudCanvas? _overlayCanvas, _toastCanvas;
    private long _toastAt = long.MinValue / 2, _spikesSeen;
    private bool _overlayDirty = true;

    private void ComposeOverlay()
    {
        if (!NotNull(_settings) || !Assert(OverlayWidth > 0)) return;
        _overlayDirty = false;
        _uiFonts.Update(_settings.FontScale);
        _overlayCanvas ??= new HudCanvas(_capi, background: true);
        var (s, f) = (_settings, _uiFonts);
        var px = scaled(f.Scale);
        var w = OverlayWidth * px;
        var numbers = s.ShowFps || s.ShowLows || s.ShowFrametime;
        var pins = s.ShowPins ? Math.Min(s.PinnedMods.Count, HudSettings.MaxPinnedMods) : 0;
        var mods = s.ShowMods || pins > 0;
        var total = s.ShowMods ? f.BodyRow : 0;
        var h = 12 * px + f.StrongRow + (numbers ? 6 * px + f.HugeRow + f.SmallRow : 0) + (s.ShowGraph ? 34 * px : 0) +
                (mods ? 9 * px + total + pins * f.BodyRow : 0) + 3 * px + f.SmallRow;
        if (!_overlayCanvas.Blank(w, h)) return;
        _overlayCanvas.Fill(0, 0, w, h, HudUi.Back with { A = 0.95 * s.Opacity }, scaled(4));
        var ui = new HudUi(_overlayCanvas, f, (8 * px, w - 16 * px, 0.0, h, 0.0), []) { Y = 6 * px };
        OverlayHead(ui);
        if (numbers) OverlayNumbers(ui);
        if (s.ShowGraph)
        {
            ui.Space(2);
            Graph(ui, 30 * px, false);
            ui.Space(2);
        }

        if (mods) OverlayMods(ui, pins);
        ui.Space(3);
        ui.Line(T("hud-v-overlay-footer"), HudUi.Dim, f.Small);
        _overlayCanvas.End();
    }

    private void OverlayHead(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "Komet");
        var (badge, fill, color) = EditionBadge();
        var x = ui.X + ui.TextW(ui.F.Strong, "Komet") + ui.Px(5);
        var bw = Math.Min(ui.TextW(ui.F.Small, badge) + ui.Px(10), ui.X + ui.W - x - ui.Px(50));
        ui.Fill(x, ui.Y + (ui.F.StrongRow - ui.F.SmallRow) / 2, bw, ui.F.SmallRow, fill, ui.Px(3));
        ui.Text(x + ui.Px(5), ui.Y + (ui.F.StrongRow - ui.F.SmallRow) / 2, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, badge, bw - ui.Px(10)), color);
        if (_debug is not null)
        {
            var armed = _debugMode == "spike" && _debugTrigger.Length == 0;
            var rec = armed ? T("hud-v-armed-short") : "REC " + N(Math.Max(0, _debug.Now));
            var recColor = armed ? HudUi.Warn : HudUi.Err;
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.StrongRow, ui.F.Small, rec, recColor);
            _ = ui.Dot(ui.X + ui.W - ui.TextW(ui.F.Small, rec) - ui.Px(11), ui.Y, ui.F.StrongRow, recColor);
        }

        ui.Y += ui.F.StrongRow;
    }

    private void OverlayNumbers(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (s, l) = (_settings, _live);
        ui.Space(4);
        var x = ui.X;
        foreach (var (on, value, label) in (ReadOnlySpan<(bool, string, string)>)[(s.ShowFps, N(l.Fps), "FPS"),
                     (s.ShowLows, N(l.Low1), "1% Low"), (s.ShowFrametime, N(l.AvgMs, 1), "ms")])
        {
            if (!on) continue;
            ui.Text(x, ui.Y, ui.F.HugeRow, ui.F.Huge, value);
            ui.Text(x, ui.Y + ui.F.HugeRow, ui.F.SmallRow, ui.F.Small, label, HudUi.Dim);
            x += Math.Max(ui.TextW(ui.F.Huge, value), ui.TextW(ui.F.Small, label)) + ui.Px(14);
        }

        ui.Y += ui.F.HugeRow + ui.F.SmallRow + ui.Px(2);
    }

    private void OverlayMods(HudUi ui, int pins)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Space(5);
        ui.Fill(ui.X, ui.Y, ui.W, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Space(3);
        if (_settings.ShowMods) ui.Row(T("hud-v-mods-total"), N(_live.ModsMs, 2) + " ms");
        for (var i = 0; i < Math.Min(pins, HudSettings.MaxPinnedMods); i++)
        {
            var mod = _settings.PinnedMods[i];
            var ms = _timings.MsOf(mod);
            var h = ui.F.BodyRow;
            ui.Pin(ui.X, ui.Y, h);
            var value = N(ms, 2);
            var valueW = ui.TextW(ui.F.Body, value);
            ui.Text(ui.X + ui.Px(10), ui.Y, h, ui.F.Body, ui.Fit(ui.F.Body, ModName(mod), ui.W - valueW - ui.Px(60)));
            ui.Bar(ui.X + ui.W - valueW - ui.Px(46), ui.Y, ui.Px(40), h, ms / 0.45 * 100);
            ui.TextRight(ui.X + ui.W, ui.Y, h, ui.F.Body, value);
            ui.Y += h;
        }
    }

    private void Toast()
    {
        if (!NotNull(_spikes) || !Assert(_settings.ToastMs >= 0)) return;
        var (count, seen) = (_spikes.Count, _spikesSeen);
        _ = Assert(count >= 0);
        _spikesSeen = count;
        if (count == seen || _settings.ToastMs <= 0 || _spikes.Stored == 0) return;
        var spike = _spikes.Latest(0);
        var now = Environment.TickCount64;
        if (spike.DtMs < _settings.ToastMs || now - _toastAt < ToastEverySeconds * 1000) return;
        _toastAt = now;
        ComposeToast(spike, count);
    }

    private void ComposeToast(Spike spike, long count)
    {
        if (!Assert(spike.DtMs >= 0) || !Assert(count > 0)) return;
        _uiFonts.Update(_settings.FontScale);
        _toastCanvas ??= new HudCanvas(_capi);
        var (f, px) = (_uiFonts, scaled(_uiFonts.Scale));
        var (w, h) = (OverlayWidth * px, 10 * px + f.StrongRow + f.BodyRow + f.SmallRow);
        if (!_toastCanvas.Blank(w, h)) return;
        _toastCanvas.Fill(0, 0, w, h, HudUi.Back with { A = 0.95 * _settings.Opacity });
        _toastCanvas.Fill(0, 0, 3 * px, h, HudUi.Warn);
        var ui = new HudUi(_toastCanvas, f, (11 * px, w - 19 * px, 0.0, h, 0.0), []) { Y = 5 * px };
        ui.Row(T("hud-v-toast-title", N(spike.DtMs)), T("hud-v-just-now"), HudUi.Warn, HudUi.Dim, f.Strong);
        ui.Line(Describe(spike.Cause));
        ui.Line(T("hud-v-toast-more", count), HudUi.Dim, f.Small);
        _toastCanvas.End();
    }

    private void DrawOverlay()
    {
        double w = _capi.Render.FrameWidth, h = _capi.Render.FrameHeight, m = scaled(12);
        if (!Assert(w > 0 && h > 0)) return;
        if (_overlayDirty || _overlayCanvas is null) ComposeOverlay();
        var right = _settings.Corner is HudCorner.TopRight or HudCorner.BottomRight;
        var bottom = _settings.Corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        double oh = 0;
        if (_overlayCanvas is { Ready: true } overlay)
        {
            overlay.Draw(right ? w - m - overlay.Width : m, bottom ? h - m - overlay.Height : m);
            oh = overlay.Height + scaled(8);
        }

        var age = Environment.TickCount64 - _toastAt;
        if (_toastCanvas is not { Ready: true } toast || age > ToastShownSeconds * 1000) return;
        var t = Math.Clamp(Math.Min(age / 180.0, (ToastShownSeconds * 1000 - age) / 500.0), 0, 1);
        var shift = (1 - t) * (1 - t) * scaled(16) * (right ? 1 : -1);
        toast.Fade((right ? w - m - toast.Width : m) + shift, bottom ? h - m - oh - toast.Height : m + oh, 50, t, 1);
    }
}
