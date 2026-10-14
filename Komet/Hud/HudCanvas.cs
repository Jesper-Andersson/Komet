using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Cairo;

namespace Komet.Hud;

internal readonly record struct Rgba(double R, double G, double B, double A)
{
    public static Rgba White(double alpha) => new(1, 1, 1, Assert(alpha is >= 0 and <= 1) ? alpha : 1);
}

// Draws recorded on the main thread, painted by Cairo at End() there or (background) on a worker; the main thread only
// uploads. A page not yet painted when a newer one ends is dropped.
internal sealed class HudCanvas(ICoreClientAPI capi, bool background = false) : IDisposable
{
    public const int GraphFrames = 240;
    private const int SizeStep = 32, MaxSize = 16384, MaxGridLines = MaxSize / 8, MaxCmds = 1 << 16;

    public static readonly Rgba Accent = new(0.16, 0.45, 0.85, 1), Warning = new(1.0, 0.72, 0.25, 1), Error = new(1.0, 0.38, 0.35, 1),
        Good = new(0.35, 0.75, 0.45, 1);

    private static readonly Rgba GridLine = Rgba.White(0.07), GridMajor = Rgba.White(0.18);
    private static readonly FontOptions Subpixel = new() { Antialias = Antialias.Subpixel }; // what CairoFont.SetupContext sets

    private enum Op : byte { Fill, Text, Line, Polyline, Area, Circle, Arc, Triangle, Caret, Clip, Unclip, Outline }

    private readonly record struct Cmd(Op Op, double A, double B, double C = 0, double D = 0, double E = 0, double F = 0,
        Rgba Color = default, CairoFont? Font = null, string? Text = null, int From = 0, int Count = 0, bool Flag = false);

    private sealed class Page
    {
        public readonly List<Cmd> Cmds = [];
        public readonly List<double> Points = [];
        public double W, H;
        public object? Tag; // what goes with the picture (a dialog's click map)
    }

    private Page _page = new();
    private Page? _next, _busy, _spare; // waiting to be painted, being painted, free
    private Task? _job;

    private Context? _ctx; // the painter's: the worker's while a job runs
    private ImageSurface? _surface;
    private CairoFont? _font;
    private byte[] _text = new byte[256];

    private Context? _measure; // the main thread's
    private ImageSurface? _measureSurface;
    private CairoFont? _measureFont; // set up on _measure since Blank() or BeginMeasure()
    private byte[] _utf8 = new byte[256];

    private LoadedTexture _texture = new(capi);
    private readonly Vintagestory.API.MathTools.Vec4f _tint = new();

    public double Width { get; private set; }
    public double Height { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }

    public bool Ready
    {
        get
        {
            if (background) Pump();
            return _texture.TextureId != 0;
        }
    }

    public void Dispose()
    {
        if (_job is not null) _ = ((IAsyncResult)_job).AsyncWaitHandle.WaitOne(); // a worker still painting into the surface
        _texture.Dispose();
        Release();
        _measure?.Dispose();
        _measureSurface?.Dispose();
        (_measure, _measureSurface, _measureFont) = (null, null, null);
    }

    private void Release()
    {
        _ctx?.Dispose();
        _surface?.Dispose();
        (_ctx, _surface, _font) = (null, null, null);
    }

    public void BeginMeasure()
    {
        if (_measure is null)
        {
            _measureSurface = new ImageSurface(Format.Argb32, SizeStep, SizeStep);
            _measure = new Context(_measureSurface);
        }

        _measureFont = null;
    }

    // GetTextExtents runs SetupContext per call, slow and allocating
    public double TextWidth(CairoFont font, string text)
    {
        if (text.Length == 0 || !Assert(font.UnscaledFontsize > 0)) return 0;
        if (_measure is null) BeginMeasure();
        if (!ReferenceEquals(font, _measureFont)) Setup(_measure!, font);
        _measureFont = font;
        return _measure!.TextExtents(Utf8(ref _utf8, text)).XAdvance;
    }

    // The longest start of text that fits width with an ellipsis (whole if it fits). trimmed: at least one character, and
    // no space before the ellipsis (the options screen's rows).
    public string Fit(CairoFont font, string text, double width, bool trimmed = false)
    {
        if (!NotNull(font) || !NotNull(text) || !Finite(width)) return text ?? "";
        if (text.Length == 0 || TextWidth(font, text) <= width) return text;
        var (lo, hi) = (0, text.Length);
        for (var step = 0; step < 16 && lo < hi; step++)
        {
            var mid = (lo + hi + 1) / 2;
            if (TextWidth(font, text[..mid] + "…") <= width) lo = mid;
            else hi = mid - 1;
        }

        if (!Assert(lo <= text.Length)) return text;
        return trimmed ? text[..Math.Max(1, lo)].TrimEnd() + "…" : text[..lo] + "…";
    }

    // Greedy wrap of one paragraph at its spaces, at most maxWords words: a line takes words while it fits width (a lone
    // word wider than that stays whole)
    public void Wrap(CairoFont font, string paragraph, double width, int maxWords, List<string> into)
    {
        if (!NotNull(paragraph) || !NotNull(into) || !Assert(maxWords > 0)) return;
        var line = "";
        foreach (var word in paragraph.Split(' ').Bounded(maxWords))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && TextWidth(font, next) > width)
            {
                into.Add(line);
                next = word;
            }

            line = next;
        }

        into.Add(line);
    }

    public bool Contains(double px, double py) =>
        Finite(px) && Finite(py) && px >= X && px < X + Width && py >= Y && py < Y + Height;

    public bool Blank(double width, double height)
    {
        if (!Assert(width > 0) || !Assert(height > 0) || !Assert(width <= MaxSize) || !Assert(height <= MaxSize)) return false;
        (_page.W, _page.H, _measureFont) = (width, height, null);
        _page.Cmds.Clear();
        _page.Points.Clear();
        return true;
    }

    private void Add(in Cmd cmd)
    {
        if (Assert(_page.W > 0) && Assert(_page.Cmds.Count < MaxCmds)) _page.Cmds.Add(cmd); // W 0: a draw without Blank()
    }

    public void Line(double x1, double y1, double x2, double y2, Rgba color, double width = 1, double dash = 0)
    {
        if (Finite(x1 + y1 + x2 + y2) && Assert(width > 0)) Add(new(Op.Line, x1, y1, x2, y2, width, dash, color));
    }

    // Gradient area from fill to clear (NaN y: bottom) and the line (NaN y: gap)
    public void Graph(int count, System.Func<int, (double X, double Y)> at, (double Top, double Bottom) span, Rgba fill,
        (Rgba Color, double Width) line)
    {
        if (!NotNull(at) || !Assert(count is >= 0 and <= MaxSize) || count < 2 || !Assert(span.Bottom > span.Top)) return;
        var from = Points(count, at);
        Add(new(Op.Area, span.Top, span.Bottom, Color: fill, From: from, Count: count));
        Add(new(Op.Polyline, 0, 0, E: line.Width, Color: line.Color, From: from, Count: count));
    }

    private int Points(int count, System.Func<int, (double X, double Y)> at)
    {
        var from = _page.Points.Count;
        for (var i = 0; i < Math.Min(count, MaxSize); i++)
        {
            var (px, py) = at(i);
            _page.Points.Add(px);
            _page.Points.Add(py);
        }

        return from;
    }

    public void Circle(double cx, double cy, double r, Rgba color)
    {
        if (Finite(cx + cy) && Assert(r > 0)) Add(new(Op.Circle, cx, cy, r, Color: color));
    }

    public void Arc(double cx, double cy, double r, double from, double to, Rgba color, double width)
    {
        if (Finite(cx + cy + from + to) && Assert(r > 0) && Assert(width > 0)) Add(new(Op.Arc, cx, cy, r, from, to, width, color));
    }

    public void Triangle(double x, double y, double size, Rgba color)
    {
        if (Finite(x + y) && Assert(size > 0)) Add(new(Op.Triangle, x, y, size, Color: color));
    }

    public void Caret(double x, double y, double size, bool down, Rgba color)
    {
        if (Finite(x + y) && Assert(size > 0)) Add(new(Op.Caret, x, y, size, Color: color, Flag: down));
    }

    public void Text(double x, double y, double rowHeight, CairoFont font, string text, Rgba? color = null)
    {
        if (text.Length > 0 && NotNull(font) && Finite(x) && Finite(y) && Assert(rowHeight > 0))
            Add(new(Op.Text, x, y, rowHeight, Color: color ?? default, Font: font, Text: text, Flag: color is not null));
    }

    public void Fill(double x, double y, double width, double height, Rgba color, double radius = 0)
    {
        if (Finite(x) && Finite(y) && Assert(width >= 0) && Assert(height >= 0) && Assert(radius >= 0))
            Add(new(Op.Fill, x, y, width, height, radius, Color: color));
    }

    public void Clip(double x, double y, double width, double height)
    {
        if (Assert(width >= 0) && Assert(height >= 0)) Add(new(Op.Clip, x, y, width, height));
    }

    public void Unclip() => Add(new(Op.Unclip, 0, 0));

    public void Outline(double x, double y, double width, double height, Rgba color)
    {
        if (Finite(x) && Finite(y) && Assert(width > 1) && Assert(height > 1)) Add(new(Op.Outline, x, y, width, height, Color: color));
    }

    public static HudCanvas Grid(ICoreClientAPI capi, double step)
    {
        var canvas = new HudCanvas(capi);
        double width = capi.Render.FrameWidth, height = capi.Render.FrameHeight;
        if (!Assert(step > 0) || !Assert(width > 0) || !Assert(height > 0) || !canvas.Blank(width, height)) return canvas;
        for (var i = 0; i < Math.Min((int)(width / step) + 1, MaxGridLines); i++)
            canvas.Fill(i * step, 0, 1, height, i % 4 == 0 ? GridMajor : GridLine);
        for (var i = 0; i < Math.Min((int)(height / step) + 1, MaxGridLines); i++)
            canvas.Fill(0, i * step, width, 1, i % 4 == 0 ? GridMajor : GridLine);
        canvas.End();
        canvas.Release(); // kept for the session: the texture alone, not the full-screen surface
        return canvas;
    }

    // A background canvas shows the last page a while after End()
    public object? Shown { get; private set; }

    public bool Holds(object tag) =>
        NotNull(tag) && (ReferenceEquals(Shown, tag) || ReferenceEquals(_next?.Tag, tag) || ReferenceEquals(_busy?.Tag, tag));

    public void End(object? tag = null)
    {
        if (!Assert(_page.W > 0)) return; // End() without Blank()
        _page.Tag = tag;
        if (!background)
        {
            Paint(_page);
            Upload(_page);
            return;
        }

        var free = _next ?? _spare ?? new Page();
        if (ReferenceEquals(free, _spare)) _spare = null;
        (_next, _page) = (_page, free);
        Pump();
    }

    private void Pump()
    {
        if (_job is { IsCompleted: true } done)
        {
            if (Assert(done.IsCompletedSuccessfully) && NotNull(_busy)) Upload(_busy);
            (_spare, _busy, _job) = (_busy, null, null);
        }

        if (_job is not null || _next is not { } next) return;
        (_busy, _next) = (next, null);
        _job = Task.Run(() => Paint(next));
    }

    private void Upload(Page page)
    {
        if (!NotNull(_surface)) return;
        capi.Gui.LoadOrUpdateCairoTexture(_surface, false, ref _texture);
        (Width, Height, Shown) = (page.W, page.H, page.Tag);
    }

    private void Paint(Page page)
    {
        int w = RoundUp(page.W), h = RoundUp(page.H);
        if (_ctx is null || _surface is null || _surface.Width != w || _surface.Height != h) Surface(w, h);
        if (!Assert(_surface.Status == Status.Success)) return;
        var ctx = _ctx;
        _font = null; // a new page may come with resized fonts
        ctx.Operator = Operator.Source;
        ctx.SetSourceRGBA(0, 0, 0, 0);
        ctx.Paint();
        ctx.Operator = Operator.Over;
        var cmds = CollectionsMarshal.AsSpan(page.Cmds);
        var points = CollectionsMarshal.AsSpan(page.Points);
        for (var i = 0; i < Math.Min(cmds.Length, MaxCmds); i++) Paint(ctx, cmds[i], points);
        ctx.ResetClip();
        _surface.Flush();
    }

    private void Paint(Context ctx, in Cmd c, ReadOnlySpan<double> points)
    {
        if (c.Op is not (Op.Text or Op.Clip or Op.Unclip)) Source(ctx, c.Color);
        switch (c.Op)
        {
            case Op.Fill:
                RoundRectangle(ctx, c.A, c.B, c.C, c.D, c.E);
                ctx.Fill();
                break;
            case Op.Text:
                Text(ctx, c);
                break;
            case Op.Line:
                Line(ctx, c);
                break;
            case Op.Polyline:
                Polyline(ctx, c, points.Slice(c.From, 2 * c.Count));
                break;
            case Op.Area:
                Area(ctx, c, points.Slice(c.From, 2 * c.Count));
                break;
            case Op.Circle:
                ctx.Arc(c.A, c.B, c.C, 0, 2 * Math.PI);
                ctx.Fill();
                break;
            case Op.Arc:
                ctx.LineWidth = c.F;
                ctx.NewPath();
                ctx.Arc(c.A, c.B, c.C, c.D, c.E);
                ctx.Stroke();
                break;
            case Op.Triangle:
                ctx.MoveTo(c.A + c.C / 2, c.B);
                ctx.LineTo(c.A + c.C, c.B + c.C);
                ctx.LineTo(c.A, c.B + c.C);
                ctx.ClosePath();
                ctx.Fill();
                break;
            case Op.Caret:
                ctx.MoveTo(c.A, c.B);
                ctx.LineTo(c.A + c.C, c.Flag ? c.B : c.B + c.C / 2);
                ctx.LineTo(c.Flag ? c.A + c.C / 2 : c.A, c.B + c.C);
                ctx.ClosePath();
                ctx.Fill();
                break;
            case Op.Clip:
                ctx.Rectangle(c.A, c.B, c.C, c.D);
                ctx.Clip();
                break;
            case Op.Unclip:
                ctx.ResetClip();
                break;
            case Op.Outline:
                RoundRectangle(ctx, c.A + 0.5, c.B + 0.5, c.C - 1, c.D - 1, 0);
                ctx.LineWidth = 1;
                ctx.Stroke();
                break;
        }
    }

    private static void Line(Context ctx, in Cmd c)
    {
        ctx.LineWidth = c.E;
        if (c.F > 0) ctx.SetDash([c.F, c.F], 0);
        ctx.MoveTo(c.A, c.B);
        ctx.LineTo(c.C, c.D);
        ctx.Stroke();
        if (c.F > 0) ctx.SetDash([], 0);
    }

    private void Text(Context ctx, in Cmd c)
    {
        var font = c.Font!;
        var setUp = !ReferenceEquals(font, _font);
        if (setUp) Setup(ctx, font);
        _font = font;
        if (c.Flag) Source(ctx, c.Color);
        else if (!setUp) FontColor(ctx, font.Color);
        var extents = ctx.FontExtents;
        ctx.MoveTo(c.A, c.B + (c.C - extents.Height * font.LineHeightMultiplier) / 2 + extents.Ascent);
        ctx.ShowText(Utf8(ref _text, c.Text!));
    }

    private static void Polyline(Context ctx, in Cmd c, ReadOnlySpan<double> xy)
    {
        var open = false;
        for (var i = 0; i < Math.Min(xy.Length - 1, 2 * MaxSize); i += 2)
        {
            if (!double.IsFinite(xy[i + 1])) open = false;
            else if (open) ctx.LineTo(xy[i], xy[i + 1]);
            else if (double.IsFinite(xy[i]))
            {
                ctx.MoveTo(xy[i], xy[i + 1]);
                open = true;
            }
        }

        ctx.LineWidth = c.E;
        ctx.Stroke();
    }

    private static void Area(Context ctx, in Cmd c, ReadOnlySpan<double> xy)
    {
        ctx.MoveTo(xy[0], c.B);
        for (var i = 0; i < Math.Min(xy.Length - 1, 2 * MaxSize); i += 2)
            ctx.LineTo(xy[i], double.IsFinite(xy[i + 1]) ? xy[i + 1] : c.B);
        ctx.LineTo(xy[^2], c.B);
        ctx.ClosePath();
        using var gradient = new LinearGradient(0, c.A, 0, c.B);
        _ = gradient.AddColorStop(0, new Color(c.Color.R, c.Color.G, c.Color.B, c.Color.A));
        _ = gradient.AddColorStop(1, new Color(c.Color.R, c.Color.G, c.Color.B, 0));
        ctx.SetSource(gradient);
        ctx.Fill();
    }

    // z: depth among the GUI's draws (the hotbar's item stacks sit above the default)
    public void Draw(double x, double y, float z = 50)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0) || !Assert(_texture.Width >= Width)) return;
        X = Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Width));
        Y = Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Height));
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height, z);
    }

    public (double X, double Y) Within(double x, double y) => !Finite(x + y) ? (0, 0) :
        (Math.Clamp(x, 0, Math.Max(0, capi.Render.FrameWidth - Width)), Math.Clamp(y, 0, Math.Max(0, capi.Render.FrameHeight - Height)));

    // Faded and scaled about its centre (window opening); X and Y stay where a click lands
    public void Fade(double x, double y, float z, double alpha, double scale)
    {
        if (!Finite(alpha + scale) || !Assert(scale > 0) || !Assert(_texture.TextureId != 0)) return;
        (X, Y) = Within(x, y);
        var a = (float)Math.Clamp(alpha, 0, 1);
        _ = _tint.Set(a, a, a, a);
        var (w, h) = (_texture.Width * scale, _texture.Height * scale);
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X + Width * (1 - scale) / 2, Y + Height * (1 - scale) / 2, w, h,
            z, _tint);
    }

    // Premultiplied tint over a white canvas: a quad of any colour
    public void Stretch(double x, double y, double w, double h, float z, Rgba color)
    {
        if (!Finite(x + y) || w <= 0 || h <= 0 || color.A <= 0 || !Assert(_texture.TextureId != 0)) return;
        _ = _tint.Set((float)(color.R * color.A), (float)(color.G * color.A), (float)(color.B * color.A), (float)color.A);
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, x, y, w, h, z, _tint);
    }

    public void Place(double x, double y, float z)
    {
        if (!Finite(x) || !Finite(y) || !Assert(_texture.TextureId != 0)) return;
        (X, Y) = (x, y);
        capi.Render.Render2DTexturePremultipliedAlpha(_texture.TextureId, X, Y, _texture.Width, _texture.Height, z);
    }

    // SetupContext without a finalizable FontOptions per call and without writing the font (measured on main while a
    // worker paints); each context keeps the last font
    private static void Setup(Context ctx, CairoFont font)
    {
        ctx.SetFontSize(scaled(font.UnscaledFontsize));
        ctx.SelectFontFace(font.Fontname, font.Slant, font.FontWeight);
        ctx.FontOptions = Subpixel;
        FontColor(ctx, font.Color);
    }

    [MemberNotNull(nameof(_ctx), nameof(_surface))]
    private void Surface(int width, int height)
    {
        Release();
        _surface = new ImageSurface(Format.Argb32, width, height);
        _ctx = new Context(_surface);
        _ = Assert(width <= MaxSize && height <= MaxSize);
    }

    // ShowText/TextExtents(string) allocate per call; a 0-terminated array is taken as is. One buffer per thread.
    private static byte[] Utf8(ref byte[] buffer, string text)
    {
        var need = Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
        if (buffer.Length < need) buffer = new byte[Math.Max(need, 2 * buffer.Length)];
        var n = Encoding.UTF8.GetBytes(text, buffer);
        (buffer[n], buffer[^1]) = (0, 0);
        return Assert(n < buffer.Length) ? buffer : [0];
    }

    private static void FontColor(Context ctx, double[]? color)
    {
        if (!Assert(color is null or { Length: 3 or 4 })) return;
        if (color is [var r, var g, var b]) ctx.SetSourceRGB(r, g, b);
        else if (color is [var r4, var g4, var b4, var a]) ctx.SetSourceRGBA(r4, g4, b4, a);
    }

    private static void Source(Context ctx, Rgba color) => ctx.SetSourceRGBA(color.R, color.G, color.B, color.A);


    private static int RoundUp(double value) =>
        Assert(value is > 0 and <= MaxSize) ? (int)Math.Ceiling(value / SizeStep) * SizeStep : SizeStep;
}


// Rebuilt on font or GUI scale change (GUIScale has no event). Main thread: CairoFont measures on the engine's context.
internal sealed class HudFonts
{
    private const double BaseFontSize = 14, BaseTitleSize = 16, HeaderPad = 2, BadgePad = 3;
    private int _gui;
    private long _scale = BitConverter.DoubleToInt64Bits(double.NaN);

    public CairoFont Text { get; } = CairoFont.WhiteDetailText().WithLineHeightMultiplier(0.9);
    public CairoFont Header { get; } = CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold);
    public CairoFont Title { get; } = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
    public int Epoch { get; private set; }
    public double TextRow { get; private set; }
    public double HeaderRow { get; private set; }
    public double BadgeRow { get; private set; }

    public void Update(double fontScale)
    {
        var (scale, gui) = (BitConverter.DoubleToInt64Bits(fontScale),
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (scale == _scale && gui == _gui) return;
        (_scale, _gui) = (scale, gui);
        if (!Assert(HudSettings.ScaleRange.Contains(fontScale))) fontScale = 1;
        Text.UnscaledFontsize = Header.UnscaledFontsize = BaseFontSize * fontScale;
        Title.UnscaledFontsize = BaseTitleSize * fontScale;
        var header = Height(Header);
        (TextRow, HeaderRow, BadgeRow) = (Height(Text), header + 2 * scaled(HeaderPad), header + 2 * scaled(BadgePad));
        Epoch++;
        _ = Assert(TextRow > 0 && BadgeRow > 0);
    }

    private static double Height(CairoFont font) =>
        Assert(font.UnscaledFontsize > 0) ? font.GetFontExtents().Height * font.LineHeightMultiplier : 0;
}

// Full-screen texture (2560x1440: 14.7 MB), built on the first drag, again only on frame size or GUI scale change
internal sealed class SnapGrid(ICoreClientAPI capi, Action built) : IDisposable
{
    private HudCanvas? _canvas;
    private (int Width, int Height, int Scale) _key;

    public void Dispose()
    {
        _canvas?.Dispose();
        _canvas = null;
    }

    public void Draw()
    {
        var key = (capi.Render.FrameWidth, capi.Render.FrameHeight,
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (!Assert(key.FrameWidth > 0 && key.FrameHeight > 0)) return;
        if (_canvas is null || key != _key)
        {
            _canvas?.Dispose();
            _canvas = HudCanvas.Grid(capi, scaled(HudSettings.SnapStep));
            _key = key;
            built();
        }

        _canvas.Draw(0, 0);
    }
}
