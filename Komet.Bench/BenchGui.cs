using Komet.Core;

namespace Komet.Bench;

// "gui": "creative": every lap, standing still, the creative inventory scrolls by wheel ticks (the vanilla worst case).
// "gui": "options": every lap's knobs switch over the paused game as in Komet's settings (SwitchMs, ResumeMs).
internal static class BenchGui
{
    public const string Creative = "creative", Debug = "debug", Options = "options", Help = "help";
    private const long SwitchMs = 2000, ResumeMs = 5000;
    public const float CaptureSeconds = 15;
    private const double TicksPerSecond = 12;

    private static GuiDialog? _dialog;
    private static double _owed;
    private static (int[] Values, long SwitchAt, long ResumeAt)? _switching;

    public static void Switch(ICoreClientAPI capi, int[] values)
    {
        if (!NotNull(capi) || !NotNull(values) || capi.World is not Vintagestory.Client.NoObf.ClientMain game) return;
        Hud.HudOverlay.OptionsForBench?.Invoke(true);
        game.PauseGame(true);
        var now = Environment.TickCount64;
        _switching = (values, now + SwitchMs, now + ResumeMs);
    }

    // Every frame, paused or not
    public static void Switching(ICoreClientAPI capi)
    {
        if (_switching is not var (values, switchAt, resumeAt) || !NotNull(capi)) return;
        var now = Environment.TickCount64;
        if (values.Length > 0 && now >= switchAt)
        {
            _ = Knobs.Apply(values);
            _switching = ([], switchAt, resumeAt);
        }

        if (now < resumeAt) return;
        (capi.World as Vintagestory.Client.NoObf.ClientMain)?.PauseGame(false);
        Hud.HudOverlay.OptionsForBench?.Invoke(false);
        _switching = null;
    }

    public static void Open(ICoreClientAPI capi)
    {
        if (!NotNull(capi) || !Assert(TicksPerSecond > 0)) return;
        _dialog ??= capi.Gui.LoadedGuis.FirstOrDefault(static g => g.GetType().Name == "GuiDialogInventory");
        if (_dialog is null)
        {
            capi.Logger.Warning("Komet bench: no inventory dialog to scroll");
            return;
        }

        if (!_dialog.IsOpened()) _ = _dialog.TryOpen();
        Scrollbar()?.SetScrollbarPosition(0);
        _owed = 0;
    }

    // dt seconds of flicking: whole wheel ticks, the rest owed to the next frame
    public static void Scroll(ICoreClientAPI capi, float dt)
    {
        if (!NotNull(capi) || !Finite(dt) || Scrollbar() is not { } bar) return;
        _owed += dt * TicksPerSecond;
        for (var i = 0; i < Math.Min((int)_owed, 8); i++)
            bar.OnMouseWheel(capi, new MouseWheelEventArgs { delta = -1, deltaPrecise = -1 });
        _owed -= Math.Floor(_owed);
        _ = Assert(_owed is >= 0 and < 1);
    }

    private static GuiElementScrollbar? Scrollbar() =>
        Assert(TicksPerSecond > 0) && _dialog is { } dialog && dialog.IsOpened() && dialog.Composers["maininventory"] is { } composer
            ? composer.GetScrollbar("scrollbar")
            : null;
}
