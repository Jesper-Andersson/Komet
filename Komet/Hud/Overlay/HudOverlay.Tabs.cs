using Komet.Diagnostics;

namespace Komet.Hud;

// Shared by the views: the mods' time, the Harmony audit and the capture armed from a hint
internal sealed partial class HudOverlay
{
    private const double ModHintMs = 2, GcHintPercent = 5, LowHintFps = 30, VramHintPercent = 90;

    private HarmonyAudit? _audit;
    private Task? _auditing;

    private double ModsTotal() => NotNull(_timings) ? _timings.TotalExcept("game") : double.NaN;

    private void SpikeCapture()
    {
        if (!NotNull(_debugWindow) || !Assert(!_disposed)) return;
        Capture("spike", 0);
        _debugWindow.Toggle(true);
    }

    private void RequestAudit()
    {
        if (_auditing is { IsCompleted: false } || !NotNull(_capi.ModLoader)) return;
        var loader = _capi.ModLoader;
        _auditing = Task.Run(() => Volatile.Write(ref _audit, HarmonyAudit.Walk(loader)));
        _ = Assert(_auditing is not null);
    }
}
