namespace Komet.Hud;

// Shared by window and debug window: one canvas faded by HudMotion with hit boxes, snap-grid drag, close after the fade
internal abstract class HudDialog : GuiDialog
{
    private readonly SnapGrid _snap;
    private readonly float _depth;
    private readonly Action _finish;
    private (double X, double Y) _grab;

    protected HudDialog(ICoreClientAPI capi, SnapGrid snap, float depth) : base(capi)
    {
        Canvas = new HudCanvas(capi, background: true);
        Motion = new HudMotion(capi);
        (_snap, _depth) = (snap, depth);
        _finish = Finish;
        _ = NotNull(snap) && Assert(depth > 0);
    }

    protected HudCanvas Canvas { get; }
    protected HudMotion Motion { get; }
    // Clicks use the hit map painted with the shown picture (Canvas.Shown lags a background canvas): shown, waiting,
    // painted, free.
    private readonly List<HudUi.Hit>[] _hitMaps = [[], [], [], []];
    protected List<HudUi.Hit> Hits { get; private set; } = [];
    private List<HudUi.Hit> ShownHits => Canvas.Shown as List<HudUi.Hit> ?? Hits;

    protected void NewHits()
    {
        foreach (var map in _hitMaps.Bounded(4))
            if (!Canvas.Holds(map))
            {
                map.Clear();
                Hits = map;
                return;
            }

        _ = Assert(_hitMaps.Length == 4) && NotNull(Hits);
    }
    protected bool Moving { get; private set; }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public event Action? Composing;

    protected void Composed()
    {
        _ = Assert(_depth > 0) && NotNull(Canvas);
        Composing?.Invoke();
    }

    protected void Show(bool close)
    {
        if (!NotNull(Motion) || !Assert(_depth > 0)) return;
        if (!IsOpened()) _ = TryOpen();
        else if (Motion.Closing) Motion.Open();
        else if (close) _ = TryClose();
    }

    public override void OnGuiOpened()
    {
        if (!NotNull(Motion) || !Assert(_depth > 0)) return;
        base.OnGuiOpened();
        Motion.Open();
    }

    public override bool TryClose()
    {
        if (!IsOpened() || !NotNull(Motion)) return false;
        Motion.Close();
        return true;
    }

    // Returns false: GuiManager treats true as "removed" and asks the same slot again, which looped forever.
    public override bool OnEscapePressed()
    {
        if (!NotNull(Motion) || !Assert(_depth > 0)) return false;
        if (IsOpened()) Motion.Close();
        return false;
    }

    private void Finish()
    {
        if (Motion.Closing && IsOpened()) _ = base.TryClose();
        _ = Assert(_depth > 0);
    }

    public bool Contains(double px, double py) => IsOpened() && Canvas.Ready && Canvas.Contains(px, py);

    // A place saved at a larger resolution would put the title bar, the only grip, off-screen
    protected void Present(double x, double y)
    {
        if (!Finite(x + y) || !NotNull(_snap)) return;
        (x, y) = (Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Canvas.Width)),
            Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Canvas.Height)));
        if (Moving) _snap.Draw();
        Motion.Draw(Canvas, Math.Round(x), Math.Round(y), _depth);
    }

    protected void Hover()
    {
        if (!NotNull(Hits) || !Assert(_depth > 0)) return;
        Motion.Hover(Canvas, ShownHits, _depth);
        if (Motion.Closed) capi.Event.EnqueueMainThreadTask(_finish, "komet-hud-close");
    }

    protected abstract void Clicked();

    protected abstract bool Grabs(double x, double y, EnumMouseButton button);

    protected abstract void Moved(double x, double y);

    public override void OnMouseDown(MouseEvent args)
    {
        if (!NotNull(args) || !Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (Motion.Closing) return;
        var (x, y) = (args.X - Canvas.X, args.Y - Canvas.Y);
        if (HudUi.HitAt(ShownHits, x, y) is { } hit)
        {
            Motion.Press(hit);
            hit.Click();
            Clicked();
        }
        else if (Grabs(x, y, args.Button)) (_grab, Moving) = ((x, y), true);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (!Moving || !NotNull(args)) return;
        Moved(HudMotion.Snap(args.X - _grab.X, Canvas.Width, capi.Render.FrameWidth),
            HudMotion.Snap(args.Y - _grab.Y, Canvas.Height, capi.Render.FrameHeight));
        args.Handled = Assert(_grab.X >= 0);
    }

    public override void OnMouseUp(MouseEvent args) => Moving = false;

    public override void OnGuiClosed() => Moving = false;

    public override void Dispose()
    {
        base.Dispose();
        Canvas.Dispose();
        Motion.Dispose();
    }
}
