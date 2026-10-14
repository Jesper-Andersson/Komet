namespace Komet.Hud;

// The debug window (Ctrl+F8), laid out as the mockup.
internal sealed class DebugWindow : HudDialog
{
    private const int MaxLines = 8192, MaxSections = 64;
    private const double Margin = 24, RefreshMs = 250, MaxWidth = 860, MaxHeight = 600;
    private const float Depth = 70;
    private static readonly int[] Durations = [10, 30, 60, 0];
    private static readonly int[] Thresholds = [25, 50, 100];

    private readonly HudSettings _settings;
    private readonly HudOverlay _hud;
    private readonly HudUiFonts _fonts = new();
    private string[] _lines = [];
    private (int Line, string Title)[] _sections = [];
    private string _shown = "", _mode = "record";
    private int _seconds = 30, _threshold = 50, _epoch;
    private double _scroll, _paneTop, _paneH;
    private (double X, double Y)? _at;
    private (double W, double H) _size;
    private bool _dirty = true;
    private long _composed;

    public DebugWindow(ICoreClientAPI capi, HudSettings settings, HudOverlay hud, SnapGrid snap) : base(capi, snap, Depth)
    {
        (_settings, _hud) = (settings, hud);
        _ = NotNull(settings) && NotNull(hud);
    }

    public override double DrawOrder => 0.91;

    public void Toggle(bool show = false)
    {
        if (!Assert(MaxLines > 0)) return;
        Show(!show);
        _dirty = true;
    }

    public override void OnRenderGUI(float deltaTime)
    {
        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (!Finite(deltaTime) || !IsOpened() || !Assert(w > 0) || !Assert(h > 0)) return;
        _fonts.Update(_settings.FontScale);
        if (!ReferenceEquals(_hud.LastProtocol, _shown)) Load(_hud.LastProtocol);
        var size = (Math.Min(w - 2 * scaled(Margin), scaled(MaxWidth * _fonts.Scale)), Math.Min(h - 2 * scaled(Margin), scaled(MaxHeight * _fonts.Scale)));
        var running = _hud.DebugProgress.Running;
        var due = Environment.TickCount64 - _composed >= RefreshMs;
        if (_dirty || (running && due) || _epoch != _fonts.Epoch || size != _size) Compose(size);
        if (!Canvas.Ready) return;
        var (x, y) = _at ?? ((w - _size.W) / 2, (h - _size.H) / 2);
        Present(x, y);
        Hover();
    }

    private void Load(string text)
    {
        if (!NotNull(text) || !Assert(MaxLines > 0)) return;
        _shown = text;
        _lines = text.Length == 0 ? [] : [.. text.Replace("\r", "", StringComparison.Ordinal).Split('\n').Take(MaxLines)];
        var sections = new List<(int, string)> { (0, T("hud-v-dbg-head")) };
        for (var i = 0; i < Math.Min(_lines.Length, MaxLines) && sections.Count < MaxSections; i++)
            if (_lines[i].StartsWith('[') && _lines[i].IndexOf(']', StringComparison.Ordinal) is var close and > 1)
                sections.Add((i, _lines[i][(close + 1)..].Trim().TrimEnd('=').Trim()));
        (_sections, _scroll, _dirty) = ([.. sections], 0, true);
        _ = Assert(_sections.Length <= MaxSections);
    }

    private static string T(string key, params object[] args) => HudText.Translate(key, args);

    private void Compose((double W, double H) size)
    {
        (_dirty, _epoch, _size, _composed) = (false, _fonts.Epoch, size, Environment.TickCount64);
        Composed();
        NewHits();
        if (!Assert(size.W > 0) || !Canvas.Blank(size.W, size.H)) return;
        Canvas.Fill(0, 0, size.W, size.H, HudUi.Back with { A = 0.97 }, scaled(4));
        var ui = new HudUi(Canvas, _fonts, (scaled(10), size.W - scaled(20), 0, size.H, 0), Hits) { Y = scaled(6) };
        Head(ui);
        Band(ui, size.W);
        Controls(ui);
        Band(ui, size.W);
        if (_lines.Length > 0)
        {
            Jumps(ui);
            Band(ui, size.W);
        }

        var footer = _fonts.SmallRow + scaled(8);
        Pane(size, ui.Y, size.H - ui.Y - footer);
        Footer(new HudUi(Canvas, _fonts, (scaled(10), size.W - scaled(20), 0, size.H, 0), Hits) { Y = size.H - footer }, size.W);
        Canvas.End(Hits);
    }

    private static void Band(HudUi ui, double width)
    {
        if (!NotNull(ui) || !Assert(width > 0)) return;
        ui.Space(6);
        ui.Fill(0, ui.Y, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Space(6);
    }

    private void Head(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "Komet Debug");
        ui.Text(ui.X + ui.TextW(ui.F.Strong, "Komet Debug") + ui.Px(6), ui.Y, ui.F.StrongRow, ui.F.Small, DebugFiles.Protocol, HudUi.Dim);
        var right = ui.X + ui.W;
        var close = T("hud-v-dbg-close");
        var cross = ui.Px(8);
        ui.Cross(right - cross, ui.Y + (ui.F.StrongRow - cross) / 2, cross, HudUi.Link);
        right -= cross + ui.Px(5);
        right -= ui.LinkText(right - ui.TextW(ui.F.Body, close), ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, close, () => TryClose()) + ui.Px(8);
        ui.Click(right, ui.Y, cross + ui.Px(13), ui.F.StrongRow, () => TryClose());
        ui.Text(right - ui.TextW(ui.F.Body, "·"), ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, ui.F.BodyRow, ui.F.Body, "·", HudUi.Dim);
        right -= ui.TextW(ui.F.Body, "·") + ui.Px(8);
        var copy = T("hud-v-dbg-copy");
        right -= ui.LinkText(right - ui.TextW(ui.F.Body, copy), ui.Y + (ui.F.StrongRow - ui.F.BodyRow) / 2, copy, Copy) + ui.Px(14);
        var chips = ui.TextW(ui.F.Small, "English") + ui.TextW(ui.F.Small, "Deutsch") + ui.Px(32);
        _ = ui.Chips(right - chips, ui.Y + (ui.F.StrongRow - ui.ChipHeight) / 2, [("English", _settings.DebugLanguage == "en",
            () => Language("en")), ("Deutsch", _settings.DebugLanguage == "de", () => Language("de"))]);
        ui.Y += ui.F.StrongRow;
    }

    private void Language(string code)
    {
        if (!Assert(code is "en" or "de") || code == _settings.DebugLanguage) return;
        var section = _sections.Length > 0 ? Current() : 0;
        _settings.DebugLanguage = code;
        _hud.Relabel(code);
        Load(_hud.LastProtocol);
        if (Index(section, _sections.Length)) _scroll = _sections[section].Line * _fonts.MonoRow;
    }

    private void Copy()
    {
        if (_shown.Length == 0 || !NotNull(capi.Input)) return;
        capi.Input.ClipboardText = _shown;
        capi.ShowChatMessage(T("hud-debug-copied"));
    }

    private void Controls(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var progress = _hud.DebugProgress;
        if (progress.Running) Running(ui, progress);
        else Setup(ui);
    }

    private void Setup(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var start = T(_mode switch { "now" => "hud-v-dbg-start-now", "spike" => "hud-v-dbg-start-spike", _ => "hud-v-dbg-start-record" });
        var x = ui.Chips(ui.X, ui.Y + ui.Px(3), [(T("hud-v-dbg-now"), _mode == "now", () => _mode = "now"),
            (T("hud-v-dbg-record"), _mode == "record", () => _mode = "record"), (T("hud-v-dbg-spike"), _mode == "spike", () => _mode = "spike")]);
        x += ui.Px(12);
        if (_mode == "record")
            foreach (var seconds in Durations.Bounded(4))
            {
                var label = seconds > 0 ? seconds + " s" : T("hud-v-dbg-until-stop");
                x += ui.Chip(x, ui.Y + ui.Px(3), label, _seconds == seconds, () => _seconds = seconds) + ui.Px(4);
            }
        else if (_mode == "spike")
            foreach (var ms in Thresholds.Bounded(3))
                x += ui.Chip(x, ui.Y + ui.Px(3), "> " + ms + " ms", _threshold == ms, () => _threshold = ms) + ui.Px(4);
        else ui.Text(x, ui.Y + ui.Px(3), ui.ChipHeight, ui.F.Small, T("hud-v-dbg-now-time"), HudUi.Dim);
        _ = ui.Go(ui.X + ui.W - ui.TextW(ui.F.Strong, start) - ui.Px(24), ui.Y, start, Start);
        ui.Y += ui.GoHeight + ui.Px(4);
        Result(ui);
    }

    private void Start()
    {
        if (!Assert(_mode is "now" or "record" or "spike")) return;
        _hud.Capture(_mode, _mode == "record" ? _seconds : 0, _threshold);
        _dirty = true;
    }

    private void Result(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        if (_lines.Length == 0)
        {
            var key = _mode switch { "now" => "hud-v-dbg-about-now", "spike" => "hud-v-dbg-about-spike", _ => "hud-v-dbg-about-record" };
            ui.Line(T(key) + " · " + Command(), HudUi.Dim, ui.F.Small);
            return;
        }

        var check = ui.Check(ui.X, ui.Y, ui.F.SmallRow, HudUi.Ok) + ui.Px(5);
        ui.Text(ui.X + check, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-v-dbg-done", _lines.Length), HudUi.Ok);
        ui.Y += ui.F.SmallRow;
        var compare = _hud.LastCompare;
        if (compare.Rows.Length == 0 || !Assert(compare.Stamp.Length > 0)) return;
        var x = ui.X;
        var lead = T("hud-v-dbg-against", compare.Stamp) + " ";
        ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, lead, HudUi.Dim);
        x += ui.TextW(ui.F.Small, lead);
        foreach (var (key, before, now) in compare.Rows.Bounded(16))
            if (Change(key, before, now) is { } part && x + ui.TextW(ui.F.Small, part.Text + part.Delta) < ui.X + ui.W)
            {
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, part.Text);
                x += ui.TextW(ui.F.Small, part.Text);
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, part.Delta, part.Better ? HudUi.Ok : HudUi.Err);
                x += ui.TextW(ui.F.Small, part.Delta) + ui.Px(8);
            }

        ui.Y += ui.F.SmallRow;
        if (compare.Unlike.Length > 0) ui.Line(T("hud-v-dbg-unlike", compare.Unlike), HudUi.Warn, ui.F.Small);
    }

    // "Ø 7,12 ms" and "−0,41"; null for a number the line leaves out
    private static (string Text, string Delta, bool Better)? Change(string key, double before, double now)
    {
        if (!double.IsFinite(before) || !double.IsFinite(now) || !Assert(key.Length > 0)) return null;
        var (label, decimals, lowerBetter) = key switch
        {
            "avg" => ("Ø", 2, true), "low1" => ("1% Low", 0, false), "worst" => ("max", 0, true), "over50" => (">50 ms %", 1, true),
            "gc" => ("GC %", 1, true), "alloc" => ("MB/s", 0, true), _ => ("", 0, true)
        };
        if (label.Length == 0) return null;
        var delta = now - before;
        var sign = delta >= 0 ? "+" : "−";
        return ($"{label} {HudText.Num(now, decimals)} ", sign + HudText.Num(Math.Abs(delta), decimals), lowerBetter ? delta <= 0 : delta >= 0);
    }

    private string Command()
    {
        var language = _settings.DebugLanguage == "de" ? " de" : "";
        var mode = _mode switch { "now" => " now", "spike" => " spike " + _threshold, _ => " " + (_seconds > 0 ? _seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) : "600") };
        return Assert(mode.Length > 0) ? "/komet debug" + mode + language : "";
    }

    private void Running(HudUi ui, (bool Running, bool Armed, double Elapsed, double Total, long Frames, int Spikes) p)
    {
        var h = ui.GoHeight;
        ui.Fill(ui.X, ui.Y + h / 2 - ui.Px(3.5), ui.Px(7), ui.Px(7), p.Armed ? HudUi.Warn : HudUi.Err, ui.Px(3.5));
        var state = p.Frames == 0 ? T("hud-debug-writing") : T("hud-v-dbg-recording-open", Clock(p.Elapsed));
        if (p.Total > 0) state = T("hud-v-dbg-recording", Clock(p.Elapsed), Clock(p.Total));
        if (p.Armed) state = T("hud-v-dbg-armed", _threshold);
        ui.Text(ui.X + ui.Px(12), ui.Y, h, ui.F.Body, state);
        var stop = T(p.Armed ? "hud-v-dbg-cancel" : "hud-v-dbg-stop");
        var stopX = ui.X + ui.W - ui.TextW(ui.F.Strong, stop) - ui.Px(24);
        var stats = T("hud-v-dbg-stats", HudText.Num(p.Frames), p.Spikes);
        ui.TextRight(stopX - ui.Px(12), ui.Y, h, ui.F.Small, stats, HudUi.Dim);
        if (p.Frames > 0) _ = ui.Go(stopX, ui.Y, stop, _hud.EndCapture, stop: true);
        ui.Y += h + ui.Px(5);
        var share = p.Total > 0 ? Math.Clamp(p.Elapsed / p.Total, 0, 1) : 0;
        ui.Fill(ui.X, ui.Y, ui.W, ui.Px(4), Rgba.White(0.1), ui.Px(2));
        ui.Fill(ui.X, ui.Y, ui.W * share, ui.Px(4), HudUi.Stop, ui.Px(2));
        ui.Y += ui.Px(4);
    }

    private static string Clock(double seconds) =>
        Assert(seconds >= 0) && double.IsFinite(seconds) ? $"{(int)seconds / 60}:{(int)seconds % 60:00}" : "0:00";

    private void Jumps(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(_sections.Length > 0)) return;
        var current = Current();
        ui.Text(ui.X, ui.Y, ui.ChipHeight, ui.F.Small, T("hud-v-dbg-jump"), HudUi.Dim);
        var x = ui.X + ui.TextW(ui.F.Small, T("hud-v-dbg-jump")) + ui.Px(6);
        for (var i = 0; i < Math.Min(_sections.Length, MaxSections); i++)
        {
            var (line, _) = _sections[i];
            var label = i == 0 ? T("hud-v-dbg-head") : i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            x += ui.Chip(x, ui.Y, label, i == current, () => (_scroll, _dirty) = (line * _fonts.MonoRow, true)) + ui.Px(2);
        }

        ui.Text(x + ui.Px(6), ui.Y, ui.ChipHeight, ui.F.Small, ui.Fit(ui.F.Small, _sections[current].Title, ui.X + ui.W - x - ui.Px(6)));
        ui.Y += ui.ChipHeight;
    }

    private int Current()
    {
        var top = (int)(_scroll / Math.Max(1, _fonts.MonoRow));
        var current = 0;
        for (var i = 0; i < Math.Min(_sections.Length, MaxSections); i++)
            if (_sections[i].Line <= top + 1) current = i;
        return Assert(current >= 0) ? current : 0;
    }

    private void Pane((double W, double H) size, double top, double height)
    {
        if (!Assert(height > 0) || !Assert(size.W > 0)) return;
        (_paneTop, _paneH) = (top, height);
        var content = _lines.Length * _fonts.MonoRow + scaled(20);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, content - height));
        Canvas.Clip(0, top, size.W, height);
        var ui = new HudUi(Canvas, _fonts, (scaled(12), size.W - scaled(36), top + scaled(6), top + height, _scroll), Hits);
        if (_lines.Length == 0)
        {
            ui.Space(30);
            ui.Wrap(T(_hud.DebugProgress.Running ? "hud-v-dbg-pane-waiting" : "hud-v-dbg-pane-empty"), HudUi.Dim);
        }

        var first = Math.Max(0, (int)(_scroll / _fonts.MonoRow) - 1);
        var count = (int)(height / _fonts.MonoRow) + 3;
        for (var i = first; i < Math.Min(Math.Min(_lines.Length, first + count), MaxLines); i++)
            ui.Text(ui.X, i * _fonts.MonoRow, _fonts.MonoRow, _fonts.Mono, _lines[i]);
        Canvas.Unclip();
        if (content <= height) return;
        var thumb = Math.Max(scaled(24), height * height / content);
        var at = top + (height - thumb) * _scroll / (content - height);
        Canvas.Fill(size.W - scaled(14), top, scaled(10), height, Rgba.White(0.05));
        Canvas.Fill(size.W - scaled(14), at, scaled(10), thumb, Rgba.White(0.28), scaled(5));
    }

    private void Footer(HudUi ui, double width)
    {
        if (!NotNull(ui) || !Assert(width > 0)) return;
        ui.Fill(0, ui.Y, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Space(4);
        ui.Row(T("hud-v-dbg-footer"), T(_settings.DebugLanguage == "de" ? "hud-v-dbg-footer-de" : "hud-v-dbg-footer-en"), HudUi.Dim, HudUi.Dim,
            ui.F.Small);
    }

    protected override void Clicked() => _dirty = Assert(MaxLines > 0);

    protected override bool Grabs(double x, double y, EnumMouseButton button)
    {
        if (!Finite(x + y) || !Assert(_paneTop >= 0)) return false;
        if (y >= _paneTop && y < _paneTop + _paneH && x >= Canvas.Width - scaled(18)) ScrollTo(y);
        return y < _paneTop;
    }

    protected override void Moved(double x, double y) => _at = Finite(x) && Finite(y) ? (x, y) : _at;

    private void ScrollTo(double y)
    {
        var content = _lines.Length * _fonts.MonoRow + scaled(20);
        if (!Assert(_paneH > 0) || content <= _paneH) return;
        (_scroll, _dirty) = (Math.Clamp((y - _paneTop) / _paneH, 0, 1) * (content - _paneH), true);
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!NotNull(args) || !Contains(capi.Input.MouseX, capi.Input.MouseY) || !Finite(args.deltaPrecise)) return;
        (_scroll, _dirty) = (_scroll - args.deltaPrecise * _fonts.MonoRow * 3, true);
        args.SetHandled();
    }
}
