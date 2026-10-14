using Cairo;

namespace Komet.Hud;

// HUD fonts at the mockup's pixel sizes times the font scale (CairoFont applies the GUI scale)
internal sealed class HudUiFonts
{
    private const double Unit = 1.15; // mockup px to the game's unscaled font size
    private double _scale = double.NaN;
    private int _gui;

    private static readonly string Face = CairoFont.WhiteDetailText().Fontname;

    public CairoFont Small { get; } = new(11, Face, [1, 1, 1, 1], null);
    public CairoFont Body { get; } = new(12, Face, [1, 1, 1, 1], null);
    public CairoFont Strong { get; } = new CairoFont(13, Face, [1, 1, 1, 1], null).WithWeight(FontWeight.Bold);
    public CairoFont Stat { get; } = new(15, Face, [1, 1, 1, 1], null);
    public CairoFont Big { get; } = new(17, Face, [1, 1, 1, 1], null);
    public CairoFont Huge { get; } = new(20, Face, [1, 1, 1, 1], null);
    public CairoFont Mono { get; } = new(11, "monospace", [0.92, 0.92, 0.92, 1], null);
    public double Scale { get; private set; } = 1;
    public int Epoch { get; private set; }
    public double SmallRow { get; private set; }
    public double BodyRow { get; private set; }
    public double StrongRow { get; private set; }
    public double StatRow { get; private set; }
    public double BigRow { get; private set; }
    public double HugeRow { get; private set; }
    public double MonoRow { get; private set; }

    public void Update(double fontScale)
    {
        var gui = BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale);
        if (Math.Abs(fontScale - _scale) < 1e-9 && gui == _gui) return;
        (_scale, _gui, Scale) = (fontScale, gui, Assert(fontScale is > 0.3 and < 3) ? fontScale : 1);
        foreach (var (font, px) in (ReadOnlySpan<(CairoFont, double)>)[(Small, 11), (Body, 12), (Strong, 13), (Stat, 15), (Big, 17),
                     (Huge, 20), (Mono, 11)])
            font.UnscaledFontsize = px * Unit * Scale;
        (SmallRow, BodyRow, StrongRow, StatRow) = (Row(Small), Row(Body), Row(Strong), Row(Stat));
        (BigRow, HugeRow, MonoRow) = (Row(Big) * 0.95, Row(Huge) * 0.92, Row(Mono) * 1.05);
        Epoch++;
        _ = Assert(BodyRow > 0) && Assert(MonoRow > 0);
    }

    private static double Row(CairoFont font) =>
        Assert(font.UnscaledFontsize > 0) ? font.GetFontExtents().Height * 1.12 : 0;
}

// The mockup's look, laid out top-down from cursor Y in column (X, W) under clip Top..Bottom; off-view parts are
// skipped but counted. Clickable parts leave hit boxes in canvas px.
internal sealed class HudUi(HudCanvas canvas, HudUiFonts fonts, (double X, double W, double Top, double Bottom, double Scroll) view,
    List<HudUi.Hit> hits)
{
    public const int MaxHits = 512;

    [ThreadStatic] private static List<string>? _lines; // Wrap's, reused
    private static List<string> Wrapped => _lines ??= [];

    public readonly record struct Hit(double X, double Y, double W, double H, Action Click);

    public static Hit? HitAt(List<Hit> hits, double x, double y)
    {
        if (!NotNull(hits) || !Assert(hits.Count <= MaxHits)) return null;
        for (var back = 0; back < Math.Min(hits.Count, MaxHits); back++)
            if (hits[hits.Count - 1 - back] is var hit && x >= hit.X && x < hit.X + hit.W && y >= hit.Y && y < hit.Y + hit.H)
                return hit;
        return null;
    }

    public static readonly Rgba Back = new(13 / 255.0, 15 / 255.0, 23 / 255.0, 0.92), Dim = Rgba.White(0.5),
        Faint = Rgba.White(0.45), Soft = Rgba.White(0.7), HeadBack = Rgba.White(0.08), TileBack = Rgba.White(0.06),
        StatBack = Rgba.White(0.05), Track = Rgba.White(0.12), Divider = Rgba.White(0.12), ChipBack = Rgba.White(0.08),
        Accent = new(41 / 255.0, 115 / 255.0, 217 / 255.0, 1), Warn = new(1, 184 / 255.0, 64 / 255.0, 1),
        Err = new(1, 97 / 255.0, 89 / 255.0, 1), Ok = new(89 / 255.0, 191 / 255.0, 115 / 255.0, 1),
        Link = new(120 / 255.0, 170 / 255.0, 240 / 255.0, 1), Stop = new(200 / 255.0, 60 / 255.0, 55 / 255.0, 1);

    public HudUiFonts F => fonts;
    public double X => view.X;
    public double W => view.W;
    public double Y { get; set; }

    public double Px(double px) => scaled(px * fonts.Scale);

    public static Rgba Tone(double percent)
    {
        var t = double.IsFinite(percent) ? Math.Clamp(percent / 100, 0, 1) : 0;
        return new Rgba(Math.Min(1, t * 2) * 230 / 255, Math.Min(1, (1 - t) * 2) * 200 / 255, 60 / 255.0, 1);
    }

    public double At(double y) => view.Top + y - view.Scroll;

    public bool Shown(double y, double h) => At(y) + h > view.Top && At(y) < view.Bottom;

    public void Click(double x, double y, double w, double h, Action click)
    {
        if (!Finite(x) || !Finite(w)) return;
        if (!NotNull(click) || !Shown(y, h) || hits.Count >= MaxHits) return;
        var top = Math.Max(At(y), view.Top);
        hits.Add(new Hit(x, top, w, Math.Min(At(y) + h, view.Bottom) - top, click));
    }

    public double TextW(CairoFont font, string text) => text.Length == 0 || !NotNull(font) ? 0 : canvas.TextWidth(font, text);

    public void Text(double x, double y, double h, CairoFont font, string text, Rgba? color = null)
    {
        if (!NotNull(font) || !NotNull(text)) return;
        if (text.Length == 0 || !Shown(y, h) || !Assert(h > 0)) return;
        canvas.Text(x, At(y), h, font, text, color ?? Rgba.White(1));
    }

    public void TextRight(double right, double y, double h, CairoFont font, string text, Rgba? color = null) =>
        Text(right - TextW(font, text), y, h, font, text, color);

    public string Fit(CairoFont font, string text, double width) =>
        NotNull(font) && Finite(width) ? canvas.Fit(font, text, width) : text;

    public void Fill(double x, double y, double w, double h, Rgba color, double radius = 0)
    {
        if (!Finite(x) || !Finite(y)) return;
        if (!Shown(y, h) || w <= 0 || h <= 0) return;
        canvas.Fill(x, At(y), w, h, color, radius);
    }

    public void Row(string left, string right, Rgba? leftColor = null, Rgba? rightColor = null, CairoFont? font = null,
        Action? click = null)
    {
        if (!NotNull(left) || !NotNull(right)) return;
        var f = font ?? fonts.Body;
        var h = f == fonts.Small ? fonts.SmallRow : fonts.BodyRow;
        var rw = TextW(f, right);
        Text(X, Y, h, f, Fit(f, left, W - rw - Px(10)), leftColor);
        TextRight(X + W, Y, h, f, right, rightColor);
        if (click is not null) Click(X, Y, W, h, click);
        Y += h;
    }

    public void Line(string text, Rgba? color = null, CairoFont? font = null, Action? click = null)
    {
        if (!NotNull(text) || !Assert(view.W > 0)) return;
        var f = font ?? fonts.Body;
        var h = f == fonts.Small ? fonts.SmallRow : fonts.BodyRow;
        Text(X, Y, h, f, Fit(f, text, W), color);
        if (click is not null) Click(X, Y, W, h, click);
        Y += h;
    }

    public void Wrap(string text, Rgba? color = null, CairoFont? font = null, double indent = 0)
    {
        var f = font ?? fonts.Body;
        var h = f == fonts.Small ? fonts.SmallRow : fonts.BodyRow;
        Wrapped.Clear();
        canvas.Wrap(f, text, W - indent, 512, Wrapped);
        foreach (var line in Wrapped.Bounded(512))
        {
            Text(X + indent, Y, h, f, line, color);
            Y += Assert(h > 0) ? h : 0;
        }
    }

    public string[] Lines(string text, CairoFont font, double width, int max)
    {
        if (!NotNull(text) || !Assert(max is > 0 and <= 64) || !Assert(width > 0)) return [];
        var lines = new List<string>(max);
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Bounded(1024))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length == 0 || TextW(font, next) <= width)
            {
                line = next;
                continue;
            }

            lines.Add(line);
            line = word;
            if (lines.Count < max) continue;
            lines[^1] = Fit(font, lines[^1] + " " + word + " …", width);
            line = "";
            break;
        }

        if (line.Length > 0 && lines.Count < max) lines.Add(line);
        for (var i = 0; i < Math.Min(lines.Count, 64); i++) lines[i] = Fit(font, lines[i], width);
        return [.. lines];
    }

    public void Label(string text)
    {
        if (!NotNull(text) || !Assert(view.W > 0)) return;
        Y += Px(9);
        Text(X, Y, fonts.SmallRow, fonts.Small, Fit(fonts.Small, text, W), Faint);
        Y += fonts.SmallRow + Px(3);
    }

    public void Header(string text)
    {
        if (!NotNull(text) || !Assert(view.W > 0)) return;
        Y += Px(8);
        var h = fonts.BodyRow + Px(2);
        Fill(X, Y, W, h, HeadBack, Px(2));
        Text(X + Px(4), Y, h, fonts.Strong, text);
        Y += h + Px(3);
    }

    public void Rule(double pad = 4)
    {
        if (!Finite(pad) || !Assert(pad >= 0)) return;
        Y += Px(pad);
        Fill(X, Y, W, Math.Max(1, Px(1)), Divider);
        Y += Px(pad);
    }

    public void Space(double px) => Y += Px(px);

    public void Grid(int columns, int count, double gap, System.Func<int, double, double, double, double> cell)
    {
        if (!Assert(columns is > 0 and <= 8) || !NotNull(cell) || count <= 0) return;
        var g = Px(gap);
        var w = (W - (columns - 1) * g) / columns;
        double row = 0;
        for (var i = 0; i < Math.Min(count, 64); i++)
        {
            var col = i % columns;
            if (col == 0 && i > 0) (Y, row) = (Y + row + g, 0);
            row = Math.Max(row, cell(i, X + col * (w + g), Y, w));
        }

        Y += row;
    }

    public double Tile(double x, double y, double w, string value, string label)
    {
        if (!NotNull(value) || !Assert(w > 0)) return 0;
        var (pad, h) = (Px(4), Px(8) + fonts.BigRow + fonts.SmallRow);
        Fill(x, y, w, h, TileBack, Px(3));
        Text(x + Px(7), y + pad, fonts.BigRow, fonts.Big, value);
        Text(x + Px(7), y + pad + fonts.BigRow, fonts.SmallRow, fonts.Small, Fit(fonts.Small, label, w - Px(14)), Dim);
        return Assert(h > 0) ? h : 0;
    }

    public double Stat(double x, double y, double w, string label, string value, string sub = "", Rgba? color = null)
    {
        if (!NotNull(value) || !Assert(w > 0)) return 0;
        var h = Px(8) + fonts.SmallRow + fonts.StatRow + (sub.Length > 0 ? fonts.SmallRow : 0);
        Fill(x, y, w, h, StatBack, Px(3));
        Text(x + Px(7), y + Px(4), fonts.SmallRow, fonts.Small, Fit(fonts.Small, label, w - Px(14)), Dim);
        Text(x + Px(7), y + Px(4) + fonts.SmallRow, fonts.StatRow, fonts.Stat, Fit(fonts.Stat, value, w - Px(14)), color);
        if (sub.Length > 0)
            Text(x + Px(7), y + Px(4) + fonts.SmallRow + fonts.StatRow, fonts.SmallRow, fonts.Small,
                Fit(fonts.Small, sub, w - Px(14)), Dim);
        return Assert(h > 0) ? h : 0;
    }

    public double Meter(double x, double y, double w, string label, string value, double percent)
    {
        if (!NotNull(value) || !Assert(w > 0)) return 0;
        var h = Px(8) + fonts.SmallRow + Px(7);
        Fill(x, y, w, h, StatBack, Px(3));
        Text(x + Px(7), y + Px(4), fonts.SmallRow, fonts.Small, label, Dim);
        TextRight(x + w - Px(7), y + Px(4), fonts.SmallRow, fonts.Body, value);
        var p = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        Fill(x + Px(7), y + Px(4) + fonts.SmallRow + Px(3), w - Px(14), Px(4), Rgba.White(0.1), Px(2));
        Fill(x + Px(7), y + Px(4) + fonts.SmallRow + Px(3), (w - Px(14)) * p / 100, Px(4), Tone(percent), Px(2));
        return Assert(h > 0) ? h : 0;
    }

    public void Bar(double x, double y, double w, double h, double percent)
    {
        if (!Finite(x) || !Finite(y)) return;
        var top = y + (h - Px(6)) / 2;
        var p = double.IsFinite(percent) ? Math.Clamp(percent, 0, 100) : 0;
        Fill(x, top, w, Px(6), Track);
        Fill(x, top, w * p / 100, Px(6), Tone(percent));
        _ = Assert(w > 0);
    }

    public double Chip(double x, double y, string text, bool on, Action? click)
    {
        if (!NotNull(text) || !Finite(x)) return 0;
        var w = TextW(fonts.Small, text) + Px(14);
        var h = fonts.SmallRow + Px(2);
        Fill(x, y, w, h, on ? Accent : ChipBack, Px(3));
        Text(x + Px(7), y + Px(1), fonts.SmallRow, fonts.Small, text, on ? Rgba.White(1) : Soft);
        if (click is not null) Click(x, y, w, h, click);
        return Assert(w > 0) ? w : 0;
    }

    public double ChipHeight => fonts.SmallRow + Px(2);

    public double Chips(double x, double y, ReadOnlySpan<(string Text, bool On, Action Click)> chips)
    {
        foreach (var (text, on, click) in chips.Bounded(16)) x += Chip(x, y, text, on, click) + Px(4);
        return Assert(x >= 0) ? x : 0;
    }

    public double Go(double x, double y, string text, Action click, bool stop = false, bool enabled = true)
    {
        if (!NotNull(text) || !NotNull(click)) return 0;
        var w = TextW(fonts.Strong, text) + Px(24);
        var h = fonts.StrongRow + Px(6);
        var fill = stop ? Stop : Accent;
        Fill(x, y, w, h, enabled ? fill : Rgba.White(0.12), Px(3));
        Text(x + Px(12), y + Px(3), fonts.StrongRow, fonts.Strong, text, enabled ? Rgba.White(1) : Dim);
        if (enabled) Click(x, y, w, h, click);
        return Assert(w > 0) ? w : 0;
    }

    public double GoHeight => fonts.StrongRow + Px(6);

    public double LinkText(double x, double y, string text, Action click, CairoFont? font = null)
    {
        if (!NotNull(text) || !NotNull(click)) return 0;
        var f = font ?? fonts.Body;
        var h = f == fonts.Small ? fonts.SmallRow : fonts.BodyRow;
        var w = TextW(f, text);
        Text(x, y, h, f, text, Link);
        Click(x, y, w, h, click);
        return Assert(w >= 0) ? w : 0;
    }

    public void Segments(double y, double h, ReadOnlySpan<(double Value, Rgba Color)> parts, double total = double.NaN)
    {
        double sum = 0;
        foreach (var (value, _) in parts.Bounded(32)) sum += Math.Max(0, value);
        var all = double.IsFinite(total) && total > sum ? total : sum;
        Fill(X, y, W, h, ChipBack, Px(2));
        if (all <= 0 || !Shown(y, h)) return;
        var x = X;
        foreach (var (value, color) in parts.Bounded(32))
        {
            var w = W * Math.Max(0, value) / all;
            if (w > 0.5) Fill(x, y, Math.Max(w, 1), h, color);
            x += w;
        }

        _ = Assert(x <= X + W + 1);
    }

    public void Graph(double h, int count, System.Func<int, double> ms, bool guides, double maxMs = 40, bool fpsLine = true)
    {
        if (!Assert(count > 1) || !NotNull(ms)) return;
        var (x, w, y) = (X, W, Y);
        Fill(x, y, w, h, guides ? StatBack : TileBack);
        if (Shown(y, h))
        {
            double ToY(double v) => Assert(maxMs > 0) ? At(y) + h - Math.Min(v, maxMs) / maxMs * h : At(y) + h;
            if (fpsLine) canvas.Line(x, ToY(1000 / 60.0), x + w, ToY(1000 / 60.0), Rgba.White(guides ? 0.22 : 0.2), 1, Px(3));
            if (guides)
            {
                canvas.Line(x, ToY(1000 / 30.0), x + w, ToY(1000 / 30.0), Err with { A = 0.35 }, 1, Px(3));
                canvas.Text(x + w - Px(4) - TextW(fonts.Small, "16,7 ms"), ToY(1000 / 60.0) - fonts.SmallRow, fonts.SmallRow,
                    fonts.Small, "16,7 ms", Rgba.White(0.45));
                canvas.Text(x + w - Px(4) - TextW(fonts.Small, "33,3 ms"), ToY(1000 / 30.0) - fonts.SmallRow, fonts.SmallRow,
                    fonts.Small, "33,3 ms", Err with { A = 0.6 });
            }

            (double, double) Point(int i) =>
                Index(i, count) && double.IsFinite(ms(i)) ? (x + w * i / (count - 1), ToY(ms(i))) : (x + w * i / (count - 1), double.NaN);
            canvas.Graph(count, Point, (At(y), At(y) + h), Accent with { A = 0.42 }, (Rgba.White(0.92), Math.Max(1, Px(1.2))));
            Peaks(count, ms, Point, fpsLine ? 1000 / 30.0 : maxMs * 0.6);
        }

        Y += h;
    }

    private void Peaks(int count, System.Func<int, double> ms, System.Func<int, (double X, double Y)> at, double limit)
    {
        if (!NotNull(ms) || !Assert(limit > 0)) return;
        for (var i = 1; i < Math.Min(count - 1, HudCanvas.GraphFrames * 4); i++)
            if (ms(i) > limit && ms(i) >= ms(i - 1) && ms(i) > ms(i + 1) && at(i) is var (px, py) && double.IsFinite(py))
                canvas.Circle(px, py, Px(2.4), Err);
    }

    public void Pin(double x, double y, double h)
    {
        if (!Finite(x) || !Assert(h > 0)) return;
        if (!Shown(y, h)) return;
        var cy = At(y) + h / 2;
        canvas.Circle(x + Px(3), cy - Px(1.5), Px(2.6), Accent);
        canvas.Line(x + Px(3), cy, x + Px(3), cy + Px(4), Accent, Math.Max(1, Px(1.2)));
    }

    public void Warning(double x, double y, double h)
    {
        if (!Finite(x) || !Assert(h > 0)) return;
        if (!Shown(y, h)) return;
        canvas.Triangle(x, At(y) + (h - Px(9)) / 2, Px(10), Warn);
    }

    // Caret, check mark and dot drawn: the game's fonts lack ▸ ▾ ✓ ●
    public double Caret(double x, double y, double h, bool down, Rgba? color = null)
    {
        if (!Finite(x) || !Assert(h > 0)) return 0;
        var size = Px(7);
        if (Shown(y, h)) canvas.Caret(x, At(y) + (h - size) / 2, size, down, color ?? Soft);
        return size;
    }

    public double Check(double x, double y, double h, Rgba color)
    {
        if (!Finite(x) || !Assert(h > 0)) return 0;
        var (s, cy, w) = (Px(9), At(y) + h / 2, Math.Max(1.2, Px(1.6)));
        if (!Shown(y, h)) return s;
        canvas.Line(x, cy, x + s * 0.38, cy + s * 0.36, color, w);
        canvas.Line(x + s * 0.38, cy + s * 0.36, x + s, cy - s * 0.42, color, w);
        return s;
    }

    public double Dot(double x, double y, double h, Rgba color)
    {
        if (!Finite(x) || !Assert(h > 0)) return 0;
        var r = Px(3.5);
        if (Shown(y, h)) canvas.Circle(x + r, At(y) + h / 2, r, color);
        return 2 * r;
    }

    public void Cross(double x, double y, double size, Rgba color)
    {
        if (!Finite(x) || !Assert(size > 0)) return;
        if (!Shown(y, size)) return;
        var w = Math.Max(1, Px(1.4));
        canvas.Line(x, At(y), x + size, At(y) + size, color, w);
        canvas.Line(x + size, At(y), x, At(y) + size, color, w);
    }

    public void Gear(double x, double y, double size, Rgba color)
    {
        if (!Finite(x) || !Assert(size > 0)) return;
        if (!Shown(y, size)) return;
        var (cx, cy, r) = (x + size / 2, At(y) + size / 2, size / 2);
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            canvas.Line(cx + Math.Cos(a) * r * 0.55, cy + Math.Sin(a) * r * 0.55, cx + Math.Cos(a) * r, cy + Math.Sin(a) * r, color,
                Math.Max(1.5, Px(2.2)));
        }

        canvas.Circle(cx, cy, r * 0.62, color);
        canvas.Circle(cx, cy, r * 0.28, Back with { A = 1 });
    }
}
