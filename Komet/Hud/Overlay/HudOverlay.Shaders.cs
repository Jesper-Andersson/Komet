namespace Komet.Hud;

// Progress card while Vulkan builds shaders and pipelines, HUD or not, so waiting frames are no mystery.
internal sealed partial class HudOverlay
{
    private const double ShadersWidth = 300, ShadersFadeMs = 600, ShadersLinger = 1500;

    private HudCanvas? _shadersCanvas;
    private (long Done, long Started) _shadersShown = (-1, -1);
    private long _shadersSince = -1, _shadersIdleAt, _shadersBase, _shadersComposed;

    private void DrawShaders()
    {
        var (pending, started) = (Komet.Vulkan.Builds.Pending, Komet.Vulkan.Builds.Started);
        if (!Assert(pending >= 0) || !Assert(started >= 0)) return;
        var now = Environment.TickCount64;
        if (pending > 0 && _shadersSince < 0) (_shadersSince, _shadersBase) = (now, started - pending); // a new burst counts from 0
        if (_shadersSince < 0) return;
        if (pending > 0) _shadersIdleAt = now;
        var idle = now - _shadersIdleAt;
        if (pending == 0 && idle > ShadersLinger + ShadersFadeMs)
        {
            _shadersSince = -1;
            return;
        }

        var shown = (started - pending - _shadersBase, started - _shadersBase);
        // at most ten compositions a second (each paints and uploads on the main thread); the last always
        if (_shadersCanvas is null || (shown != _shadersShown && (now - _shadersComposed >= 100 || shown.Item1 == shown.Item2)))
        {
            _shadersComposed = now;
            ComposeShaders(shown.Item1, shown.Item2);
        }
        if (_shadersCanvas is not { Ready: true } card) return;
        var alpha = Math.Clamp(Math.Min((now - _shadersSince) / 200.0, 1 - (idle - ShadersLinger) / ShadersFadeMs), 0, 1);
        card.Fade(Math.Round((_capi.Render.FrameWidth - card.Width) / 2), scaled(12), 60, alpha, 1);
    }

    private void ComposeShaders(long done, long started)
    {
        if (!Assert(done >= 0) || !Assert(started >= done)) return;
        _shadersShown = (done, started);
        _uiFonts.Update(_settings.FontScale);
        _shadersCanvas ??= new HudCanvas(_capi);
        var (f, px) = (_uiFonts, scaled(_uiFonts.Scale));
        var (w, h) = (ShadersWidth * px, 14 * px + f.BodyRow + 6 * px);
        if (!_shadersCanvas.Blank(w, h)) return;
        _shadersCanvas.Fill(0, 0, w, h, HudUi.Back with { A = 0.92 }, 6 * px);
        var text = done >= started ? T("hud-shaders-done", started) : T("hud-shaders", done, started);
        _shadersCanvas.Text(12 * px, 5 * px, f.BodyRow, f.Body, text);
        var (barX, barY, barW) = (12 * px, h - 9 * px, w - 24 * px);
        _shadersCanvas.Fill(barX, barY, barW, 4 * px, Rgba.White(0.12), 2 * px);
        _shadersCanvas.Fill(barX, barY, Math.Max(4 * px, barW * done / Math.Max(1, started)), 4 * px, HudUi.Accent, 2 * px);
        _shadersCanvas.End();
    }
}
