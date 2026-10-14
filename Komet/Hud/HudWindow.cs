namespace Komet.Hud;

// Ctrl+F7. Recomposed only on change: input, scroll, new numbers (at most every RefreshMs), fonts or frame size.
internal sealed class HudWindow : HudDialog
{
    public static readonly string[] Tabs = ["overview", "frames", "system", "render", "threads", "mods", "log", "settings", "version"];
    public const int Overview = 0, Frames = 1, System = 2, Render = 3, Threads = 4, Mods = 5, Log = 6, Settings = 7, Version = 8;

    private const double MaxWidth = 520, MaxHeight = 560, Margin = 24, RefreshMs = 400;
    private const float Depth = 60;

    private readonly HudSettings _settings;
    private readonly HudUiFonts _fonts;
    private readonly double[] _scroll = new double[Tabs.Length];
    private readonly Action<HudUi, int> _view;
    private readonly System.Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> _badges;
    private readonly (double X, double Y, double W, double H)[] _tabAt = new (double, double, double, double)[Settings];
    private int _tab;
    private double _content, _bodyTop, _titleH;
    private (double W, double H) _size;
    private bool _dirty = true, _urgent = true;
    private long _composed;
    private int _epoch;

    public HudWindow(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, SnapGrid snap, Action<HudUi, int> view,
        System.Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> badges) : base(capi, snap, Depth)
    {
        (_settings, _fonts, _view, _badges) = (settings, fonts, view, badges);
        _tab = Math.Max(0, Array.IndexOf(Tabs, settings.WindowTab));
        _ = Assert(Tabs.Length == Version + 1) && NotNull(view);
    }

    public override double DrawOrder => 0.9;
    public override bool CaptureAllInputs() => Typing?.Invoke() == true;

    public int Tab => _tab;

    public event Action? Changed; // Tab changed or window opened/closed: the HUD switches its collectors

    public System.Func<KeyEvent, bool, bool>? Keys { get; set; }
    public System.Func<bool>? Typing { get; set; }
    public Action? OpenDebug { get; set; }

    public bool Shows(int tab) => IsOpened() && _tab == tab;

    public void Toggle(int? tab = null)
    {
        if (!Assert(tab is null || Index(tab.Value, Tabs.Length))) return;
        if (tab is { } wanted) Select(wanted);
        Show(tab is null);
    }

    public void Select(int tab)
    {
        _ = NotNull(_settings);
        if (!Index(tab, Tabs.Length)) return;
        Invalidate(true);
        if (tab == _tab) return;
        _tab = tab;
        _settings.WindowTab = Tabs[tab];
        Changed?.Invoke();
    }

    public void Invalidate(bool urgent = false) => (_dirty, _urgent) = (true, _urgent || urgent);

    public override void OnGuiOpened()
    {
        if (!Assert(Tabs.Length > 0)) return;
        base.OnGuiOpened();
        Invalidate(true);
        Changed?.Invoke();
    }

    public override void OnGuiClosed()
    {
        if (!Assert(_tab >= 0) || !NotNull(_settings)) return;
        base.OnGuiClosed();
        Changed?.Invoke();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (!Finite(deltaTime) || !IsOpened() || !Assert(w > 0) || !Assert(h > 0)) return;
        _fonts.Update(_settings.FontScale);
        var size = (Math.Min(w - 2 * scaled(Margin), scaled(MaxWidth * _fonts.Scale)),
            Math.Min(h - 2 * scaled(Margin), scaled(MaxHeight * _fonts.Scale)));
        var due = _urgent || Environment.TickCount64 - _composed >= RefreshMs;
        if ((_dirty && due) || _epoch != _fonts.Epoch || size != _size) Compose(size);
        if (!Canvas.Ready) return;
        Present(double.IsFinite(_settings.WindowX) ? _settings.WindowX : (w - _size.W) / 2,
            double.IsFinite(_settings.WindowY) ? _settings.WindowY : (h - _size.H) / 2);
        if (_tab < Settings) Motion.Slide(Canvas, _tabAt[_tab], Depth, HudUi.Accent);
        Hover();
    }

    private void Compose((double W, double H) size)
    {
        if (!Assert(size.W > 0) || !Assert(size.H > 0)) return;
        (_dirty, _urgent, _epoch, _size, _composed) = (false, false, _fonts.Epoch, size, Environment.TickCount64);
        Composed();
        NewHits();
        if (!Canvas.Blank(size.W, size.H)) return;
        Canvas.Fill(0, 0, size.W, size.H, HudUi.Back, scaled(4));
        var chrome = new HudUi(Canvas, _fonts, (scaled(8), size.W - scaled(16), 0, size.H, 0), Hits);
        Title(chrome, size.W);
        TabBar(chrome, size.W);
        Body(size);
        Canvas.End(Hits);
        _ = Assert(Hits.Count <= HudUi.MaxHits);
    }

    private void Title(HudUi ui, double width)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (y, row) = (ui.Px(5), _fonts.StrongRow + ui.Px(4));
        _titleH = y + row;
        ui.Text(ui.X, y, row, _fonts.Strong, "Komet");
        var x = ui.X + ui.TextW(_fonts.Strong, "Komet") + ui.Px(6);
        foreach (var (text, fill, color, click) in _badges().Bounded(4))
        {
            var w = ui.TextW(_fonts.Small, text) + ui.Px(10);
            ui.Fill(x, y + (row - _fonts.SmallRow) / 2, w, _fonts.SmallRow, fill, ui.Px(3));
            ui.Text(x + ui.Px(5), y + (row - _fonts.SmallRow) / 2, _fonts.SmallRow, _fonts.Small, text, color);
            if (click is not null) ui.Click(x, y + (row - _fonts.SmallRow) / 2, w, _fonts.SmallRow, click);
            x += w + ui.Px(4);
        }

        var right = width - ui.Px(8);
        var size = ui.Px(10);
        ui.Cross(right - size, y + (row - size) / 2, size, Rgba.White(0.85));
        ui.Click(right - size - ui.Px(4), y, size + ui.Px(8), row, () => TryClose());
        right -= size + ui.Px(14);
        ui.Gear(right - ui.Px(12), y + (row - ui.Px(12)) / 2, ui.Px(12), _tab == Settings ? Rgba.White(1) : HudUi.Dim);
        ui.Click(right - ui.Px(16), y, ui.Px(20), row, () => Select(_tab == Settings ? Overview : Settings));
        right -= ui.Px(26);
        var debug = HudText.Translate("hud-v-debug");
        _ = ui.LinkText(right - ui.TextW(_fonts.Body, debug), y + (row - _fonts.BodyRow) / 2, debug, () => OpenDebug?.Invoke());
    }

    private void TabBar(HudUi ui, double width)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (top, h) = (_titleH + ui.Px(2), _fonts.BodyRow + ui.Px(10));
        var x = ui.Px(6);
        for (var i = 0; i < Settings; i++)
        {
            var (label, tab) = (HudText.Cached("hud-tab-", Tabs[i]), i);
            var w = ui.TextW(_fonts.Body, label) + ui.Px(10);
            ui.Text(x + ui.Px(5), top + ui.Px(5), _fonts.BodyRow, _fonts.Body, label, i == _tab ? Rgba.White(1) : Rgba.White(0.55));
            _tabAt[i] = (x, top + h - ui.Px(2), w, ui.Px(2));
            ui.Click(x, top, w, h, () => Select(tab));
            x += w;
        }

        ui.Fill(0, top + h, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        _bodyTop = top + h + 1;
        _ = Assert(_bodyTop < _size.H);
    }

    private void Body((double W, double H) size)
    {
        if (!Assert(size.W > 0) || !NotNull(_view)) return;
        var bar = scaled(6);
        var (top, bottom) = (_bodyTop + scaled(4), size.H - scaled(10));
        var max = Math.Max(0, _content - (bottom - top));
        _scroll[_tab] = Math.Clamp(_scroll[_tab], 0, max);
        Canvas.Clip(0, _bodyTop, size.W, size.H - _bodyTop);
        var ui = new HudUi(Canvas, _fonts, (scaled(10), size.W - scaled(20) - bar, top, bottom, _scroll[_tab]), Hits);
        _view(ui, _tab);
        Canvas.Unclip();
        _content = ui.Y + scaled(6);
        if (_content <= bottom - top || !Assert(_content > 0)) return;
        var thumb = Math.Max(scaled(24), (bottom - top) * (bottom - top) / _content);
        var at = top + (bottom - top - thumb) * _scroll[_tab] / Math.Max(1, _content - (bottom - top));
        Canvas.Fill(size.W - bar - scaled(4), top, bar - scaled(2), bottom - top, Rgba.White(0.05), scaled(2));
        Canvas.Fill(size.W - bar - scaled(4), at, bar - scaled(2), thumb, Rgba.White(0.28), scaled(2));
    }

    protected override void Clicked()
    {
        _ = Assert(_tab >= 0);
        Invalidate(true);
    }

    protected override bool Grabs(double x, double y, EnumMouseButton button) =>
        Finite(x) && Assert(_titleH > 0) && y < _titleH && button == EnumMouseButton.Left;

    protected override void Moved(double x, double y)
    {
        if (Finite(x) && Finite(y)) (_settings.WindowX, _settings.WindowY) = (x, y);
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!NotNull(args) || !Contains(capi.Input.MouseX, capi.Input.MouseY) || !Finite(args.deltaPrecise)) return;
        _scroll[_tab] -= args.deltaPrecise * _fonts.BodyRow * 3;
        Invalidate(true);
        args.SetHandled();
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (!Assert(_tab >= 0)) return;
        if (!NotNull(args) || Keys?.Invoke(args, false) != true) return;
        args.Handled = true;
        Invalidate(true);
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (!NotNull(args) || Keys?.Invoke(args, true) != true) return;
        args.Handled = true;
        Invalidate(true);
    }
}
