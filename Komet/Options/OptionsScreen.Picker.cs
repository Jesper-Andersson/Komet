using Komet.Api;
using Komet.Hud;
using Vintagestory.API.MathTools;

namespace Komet.Options;

// Names are their own texture, moved under a scissor: scrolling composes nothing. Gap clicks pick nothing.
internal sealed partial class OptionsScreen
{
    private const int MaxCycled = 3, PickerGroup = 5, MaxNames = OptionPage.MaxOptions + MaxLanguagesShown;
    private const int MaxLanguagesShown = 64, MaxHintLines = 6;
    private const double CellHeight = 36, CellGap = 3, ScrollBar = 4;
    private const float NamesDepth = ScreenDepth + 0.5f;

    private static readonly Rgba CellBack = Rgba.White(0.04);

    private HudCanvas? _names;
    private object? _namesKey;
    private OptionRow? _picker; // the row whose window is open
    private double _pickScroll = double.NaN; // NaN: the chosen name in the middle, as the window opens
    private (double X, double Y, double W, double H) _pickView; // where its names show, in canvas pixels
    private (double Cell, double Step, double Width, int Count, double Total) _pickLayout;

    private void Picker(Columns c)
    {
        _pickView = default;
        if (_picker is not { } row || !NotNull(c)) return;
        var side = c.Desc > 0;
        double x = side ? c.Width - c.Desc : c.Side + c.Gap, w = side ? c.Desc : c.Content;
        double bottom = side ? c.Height - 3 * (S(ButtonHeight) + c.Gap) : c.Height; // above the buttons
        double pad = S(Pad), head = S(PageHeight), line = _fonts.TextRow, cell = S(CellHeight), between = S(CellGap);
        var count = Math.Min(row.Names.Length, MaxNames);
        if (!Assert(w > 4 * pad) || !Assert(line > 0) || !Assert(cell > 0) || bottom - c.Top < head + cell + pad || count == 0) return;

        var hint = Wrap(Markup().Replace(row.Hint ?? "", "").Trim(), _fonts.Text, w - 2 * pad);
        var room = Math.Min(MaxHintLines, (int)((bottom - c.Top - head - 2 * cell - pad) / line)); // two names at least under it
        var lines = hint.Take(Math.Max(0, room)).ToArray();
        var top = c.Top + head + pad / 2 + lines.Length * line + pad / 2;
        var total = count * cell + (count - 1) * between;
        var view = Math.Min(total, bottom - pad / 2 - top);
        var height = top + view + pad / 2 - c.Top;

        _canvas.Fill(x, c.Top, w, height, Panel);
        // the window's ground: a click in a gap keeps it open, rows under it neither lit nor described
        _hits.Insert(0, new Hit(x, c.Top, w, height, (_, _) => { }, Group: PickerGroup));
        _hits.Insert(0, new Hit(x, c.Top, w, height, (_, _) => { }, Option: row, Row: true, Group: PickerGroup));
        Head(row, x, c.Top, w, head);
        var y = c.Top + head + pad / 2;
        foreach (var text in lines.Bounded(MaxHintLines))
        {
            _canvas.Text(x + pad, y, line, _fonts.Text, text, Faint);
            y += line;
        }

        var bar = total > view ? S(ScrollBar) + 2 * between : 0;
        var width = w - pad - bar;
        _pickLayout = (cell, cell + between, width, count, total);
        _pickView = (x + pad / 2, top, w - pad, view);
        _pickScroll = Scrolled(_pickScroll, (int)Math.Round(Value(row)), (cell, cell + between, view, total));
        Names(row, width, total);
    }

    internal static double Scrolled(double scroll, int chosen, // NaN (just opened) centres the chosen name
        (double Cell, double Step, double View, double Total) names)
    {
        if (!Finite(names.Step) || !Finite(names.View) || !Assert(names.Cell > 0)) return 0;
        var at = double.IsNaN(scroll) ? chosen * names.Step - (names.View - names.Cell) / 2 : scroll;
        return Math.Clamp(at, 0, Math.Max(0, names.Total - names.View));
    }

    private void Head(OptionRow row, double x, double y, double w, double h)
    {
        if (!NotNull(row) || !Assert(w > h) || !Assert(h > 0)) return;
        Band(x, y, w, h);
        _canvas.Clip(x, y, w - h, h);
        _canvas.Text(x + S(Pad), y, h, _fonts.Header, row.Label);
        _canvas.Unclip();
        _canvas.Text(x + w - (h + _canvas.TextWidth(_fonts.Header, "×")) / 2, y, h, _fonts.Header, "×", Soft);
        _hits.Insert(0, new Hit(x + w - h, y, h, h, (_, _) => _picker = null, Glow: StrongGlow, Group: PickerGroup));
    }

    private void Names(OptionRow row, double width, double total)
    {
        if (!NotNull(row) || !Assert(width > 0) || !Assert(total > 0)) return;
        var chosen = (int)Math.Round(Value(row));
        var key = (row, chosen, Math.Round(width), Math.Round(total), _fonts.Epoch);
        if (_names is { Ready: true } && Equals(_namesKey, key)) return;
        _names ??= new HudCanvas(capi);
        if (!_names.Blank(width, total)) return;
        var (cell, step, _, count, _) = _pickLayout;
        for (var i = 0; i < Math.Min(count, MaxNames); i++) Cell(_names, row, i, i * step, width, cell, i == chosen);
        _names.End();
        _namesKey = key;
    }

    private void Cell(HudCanvas canvas, OptionRow row, int index, double y, double w, double h, bool chosen)
    {
        if (!NotNull(row) || !Index(index, row.Names.Length)) return;
        canvas.Fill(0, y, w, h, chosen ? Selected : CellBack);
        if (chosen) canvas.Fill(0, y, S(AccentBar), h, HudCanvas.Accent);
        var (name, aside) = Parts(row.Names[index]);
        var main = Fit(name, w - 2 * Inset);
        canvas.Text(Inset, y, h, _fonts.Text, main, chosen ? null : Soft);
        var room = w - 2 * Inset - _canvas.TextWidth(_fonts.Text, main) - S(Pad);
        var asideW = _canvas.TextWidth(_fonts.Text, aside);
        if (aside.Length > 0 && asideW <= room) canvas.Text(w - Inset - asideW, y, h, _fonts.Text, aside, Faint);
    }

    private void DrawNames()
    {
        var (x, y, w, h) = _pickView;
        if (_picker is null || _names is not { Ready: true } names || w <= 0 || h <= 0) return;
        var (sx, sy) = (_canvas.X + x, _canvas.Y + y);
        if (!Finite(sx) || !Finite(sy) || !Finite(_pickScroll)) return;
        capi.Render.GlScissor((int)Math.Round(sx), (int)Math.Round(capi.Render.FrameHeight - sy - h), (int)Math.Round(w),
            (int)Math.Round(h));
        capi.Render.GlScissorFlag(true);
        names.Place(sx, sy - _pickScroll, NamesDepth);
        capi.Render.GlScissorFlag(false);
        var (_, _, _, _, total) = _pickLayout;
        if (total <= h) return;
        var (bar, thumb) = (S(ScrollBar), Math.Max(S(CellHeight), h * h / total));
        var at = (h - thumb) * _pickScroll / Math.Max(1, total - h);
        capi.Render.RenderRectangle((float)(sx + w - bar), (float)sy, NamesDepth, (float)bar, (float)h, ColorUtil.ToRgba(20, 255, 255, 255));
        capi.Render.RenderRectangle((float)(sx + w - bar), (float)(sy + at), NamesDepth, (float)bar, (float)thumb,
            ColorUtil.ToRgba(100, 255, 255, 255));
    }

    private Hit? PickAt(double x, double y)
    {
        var (vx, vy, _, vh) = _pickView;
        var (cell, step, width, count, _) = _pickLayout;
        if (_picker is not { } row || !Finite(x) || !Finite(y) || x < vx || x >= vx + width || y < vy || y >= vy + vh || step <= 0)
            return null;
        var at = y - vy + _pickScroll;
        var index = (int)(at / step);
        if (!Index(index, count) || at - index * step >= cell) return null;
        var top = vy - _pickScroll + index * step;
        var (from, to) = (Math.Max(top, vy), Math.Min(top + cell, vy + vh));
        return new Hit(vx, from, width, to - from, (_, _) =>
        {
            Stage(row, index);
            _picker = null;
        }, Glow: RowGlow, Group: PickerGroup);
    }

    private bool ScrollNames(double delta)
    {
        var (x, y, w, h) = _pickView;
        var (mx, my) = _mouse;
        if (_picker is null || !Finite(delta) || mx < x || mx >= x + w || my < y || my >= y + h) return false;
        var (_, step, _, _, total) = _pickLayout;
        _pickScroll = Math.Clamp(_pickScroll - delta * step * 3, 0, Math.Max(0, total - h));
        Retarget();
        return true;
    }

    private static (string Main, string Aside) Parts(string name)
    {
        var tab = NotNull(name) ? name.IndexOf('\t', StringComparison.Ordinal) : -1;
        return tab < 0 ? (name ?? "", "") : (name[..tab], name[(tab + 1)..]);
    }

    private static double Inset => S(Pad) * 0.75; // a tile's text from its edges
}
