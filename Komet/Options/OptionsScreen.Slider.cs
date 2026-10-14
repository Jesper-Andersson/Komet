using Komet.Api;
using Komet.Hud;

namespace Komet.Options;

// Sliders: thicker when active, knob glides (follows a drag), staged changes grow the bar; shipped values marked,
// restored by Delete, right click or ↺. Keys act on the focused slider. Recomposes only while something moves.
internal sealed partial class OptionsScreen
{
    private const double TrackThin = 10, TrackThick = 14, KnobRadius = 11, KnobRing = 3, Halo = 6, Dot = 4, ChipPad = 8,
        ChipHeight = 28, ResetSize = 32, MarkHeight = 22, Spring = 18, Settled = 0.002, BigStep = 10;
    private const int MaxTicks = 12, MaxMotions = 256;

    private sealed class Motion
    {
        public required OptionRow Row { get; init; }
        public double Thick { get; set; }
        public double Shown { get; set; }
        public double Staged { get; set; }
    }

    private static readonly Rgba BubbleBack = new(0.043, 0.051, 0.07, 0.96), ChipBack = Rgba.White(0.06);
    private readonly List<Motion> _motions = [];
    private OptionRow? _focus;

    private static double Fraction(OptionRow row, double value) =>
        Assert(row.Max > row.Min) && Finite(value) ? Math.Clamp((value - row.Min) / (row.Max - row.Min), 0, 1) : 0;

    private bool Active(OptionRow row) =>
        ReferenceEquals(row, _hover) || ReferenceEquals(row, _focus) || ReferenceEquals(_drag?.Option, row);

    private static bool Resettable(OptionRow row, double value) => double.IsFinite(row.Default) && Math.Abs(value - row.Default) > 1e-9;

    private Motion MotionOf(OptionRow row)
    {
        for (var i = 0; i < Math.Min(_motions.Count, MaxMotions); i++)
            if (ReferenceEquals(_motions[i].Row, row))
                return _motions[i];
        var made = new Motion { Row = row, Shown = Fraction(row, Value(row)), Staged = _staged.ContainsKey(row) ? 1 : 0 };
        if (_motions.Count < MaxMotions) _motions.Add(made);
        _ = Assert(_motions.Count <= MaxMotions) && NotNull(made.Row);
        return made;
    }

    private void Animate(float deltaTime)
    {
        if (!Finite(deltaTime) || !Assert(deltaTime >= 0) || _motions.Count == 0) return;
        var k = 1 - Math.Exp(-deltaTime * Spring);
        var moving = false;
        foreach (var m in _motions.Bounded(MaxMotions))
        {
            var (row, shown) = (m.Row, Fraction(m.Row, Value(m.Row)));
            if (ReferenceEquals(_drag?.Option, row)) m.Shown = shown; // a drag: the knob stays under the cursor
            var (thick, staged) = (Active(row) ? 1.0 : 0, _staged.ContainsKey(row) ? 1.0 : 0);
            if (Math.Abs(m.Thick - thick) + Math.Abs(m.Shown - shown) + Math.Abs(m.Staged - staged) <= Settled)
            {
                (m.Thick, m.Shown, m.Staged) = (thick, shown, staged);
                continue;
            }

            (m.Thick, m.Shown, m.Staged, moving) = (Ease(m.Thick, thick, k), Ease(m.Shown, shown, k), Ease(m.Staged, staged, k), true);
        }

        if (moving) _dirty = true;
    }

    private double SliderWidth(OptionRow row) => Widest(row) + 2 * S(ChipPad) + S(ResetSize) + S(TrackWidth) + S(Pad);

    private void Slider(OptionRow row, double y, double right, double h, bool enabled)
    {
        var value = Value(row);
        if (!Finite(value) || !Assert(row.Max > row.Min) || !Assert(h > 0)) return;
        var (m, text, chip) = (MotionOf(row), row.ValueText(value), Widest(row) + 2 * S(ChipPad));
        var chipRight = right - S(ResetSize);
        _canvas.Fill(chipRight - chip, y + (h - S(ChipHeight)) / 2, chip, S(ChipHeight), ChipBack, S(5));
        Value(chipRight - S(ChipPad), y, h, text, enabled ? Soft : Faint);
        double w = S(TrackWidth), left = chipRight - chip - S(Pad) - w;
        Track(row, m, left, y + h / 2, w, enabled);
        if (ReferenceEquals(_drag?.Option, row)) Bubble(left + w * m.Shown, y + h / 2, text);
        if (!enabled) return;
        if (Resettable(row, value)) Reset(row, right - S(ResetSize), y, h);
        _rows.Insert(0, new Hit(left, y, w, h, (f, back) =>
        {
            _focus = row;
            Stage(row, back && double.IsFinite(row.Default) ? row.Default : row.At(f));
        }, true, row));
    }

    private void Track(OptionRow row, Motion m, double left, double cy, double w, bool enabled)
    {
        var t = S(TrackThin + (TrackThick - TrackThin) * m.Thick);
        if (!Assert(w > t) || !Finite(cy)) return;
        _canvas.Fill(left, cy - t / 2, w, t, Rgba.White(0.12 + 0.05 * m.Thick), t / 2);
        _canvas.Fill(left, cy - t / 2, Math.Max(t, w * m.Shown), t, enabled ? HudCanvas.Accent : Rgba.White(0.28), t / 2);
        var steps = (int)Math.Round((row.Max - row.Min) / row.Step);
        for (var i = 0; steps <= MaxTicks && i <= Math.Min(steps, MaxTicks); i++)
            _canvas.Circle(left + w * i / steps, cy, S(Dot) / 2, Rgba.White(0.4));
        if (enabled && Resettable(row, Value(row)))
            _canvas.Fill(left + w * Fraction(row, row.Default) - S(1.5), cy - S(MarkHeight) / 2, S(3), S(MarkHeight), Rgba.White(0.75), S(1.5));
        var (x, drag) = (left + w * m.Shown, ReferenceEquals(_drag?.Option, row));
        var r = S(KnobRadius) * (1 + (drag ? 0.22 : 0.12) * m.Thick);
        if (ReferenceEquals(row, _focus)) _canvas.Circle(x, cy, r + S(KnobRing + Halo), HudCanvas.Accent with { A = 0.3 });
        _canvas.Circle(x, cy, r + S(KnobRing), enabled ? HudCanvas.Accent : Rgba.White(0.28));
        _canvas.Circle(x, cy, r, Rgba.White(enabled ? 1 : 0.45));
    }

    private void Bubble(double x, double cy, string text)
    {
        if (!NotNull(text) || !Finite(x)) return;
        var (bw, bh) = (_canvas.TextWidth(_fonts.Text, text) + 2 * S(ChipPad), _fonts.TextRow);
        var by = cy - S(KnobRadius + KnobRing) * 1.22 - S(6) - bh;
        _canvas.Fill(x - bw / 2, by, bw, bh, BubbleBack, S(6));
        _canvas.Text(x - bw / 2 + S(ChipPad), by, bh, _fonts.Text, text);
    }

    private void Reset(OptionRow row, double x, double y, double h)
    {
        if (!double.IsFinite(row.Default) || !Assert(h > 0)) return;
        var (cx, cy, r) = (x + S(ResetSize) / 2, y + h / 2, S(6));
        var color = ReferenceEquals(row, _hover) ? Rgba.White(0.9) : Rgba.White(0.6);
        _canvas.Arc(cx, cy, r, -0.15 * Math.PI, 1.55 * Math.PI, color, S(1.6));
        var (tx, ty) = (cx + r * Math.Cos(-0.15 * Math.PI), cy + r * Math.Sin(-0.15 * Math.PI)); // the arrow at the arc's start
        _canvas.Line(tx, ty, tx - S(4), ty - S(1), color, S(1.6));
        _canvas.Line(tx, ty, tx + S(0.5), ty - S(4), color, S(1.6));
        _rows.Insert(0, new Hit(x, y, S(ResetSize), h, (_, _) =>
        {
            _focus = row;
            Stage(row, row.Default);
        }, Option: row));
    }

    private bool Keyed(KeyEvent args)
    {
        if (!NotNull(args) || _binding is not null || _searching) return false;
        if (args.KeyCode == (int)GlKeys.Tab) return Focus(args.ShiftPressed ? -1 : 1);
        if (_focus is not { } row || !Assert(row.Step > 0)) return false;
        var (value, step) = (Value(row), row.Step * (args.ShiftPressed ? BigStep : 1));
        double? next = args.KeyCode switch
        {
            (int)GlKeys.Left => value - step,
            (int)GlKeys.Right => value + step,
            (int)GlKeys.Home => row.Min,
            (int)GlKeys.End => row.Max,
            (int)GlKeys.Delete or (int)GlKeys.BackSpace when double.IsFinite(row.Default) => row.Default,
            _ => null
        };
        if (next is not { } to) return false;
        if (row.IsEnabled) Stage(row, row.At(Fraction(row, to)));
        return true;
    }

    private bool Focus(int direction)
    {
        var items = Items();
        var (at, top, found) = (-1, 0.0, new List<(OptionRow Row, double Top, double Height)>());
        foreach (var item in items.Bounded(MaxItems))
        {
            if (item.Row is { Kind: OptionKind.Slider, IsEnabled: true } row)
            {
                if (ReferenceEquals(row, _focus)) at = found.Count;
                found.Add((row, top, item.Height));
            }

            top += item.Height;
        }

        if (found.Count == 0 || !Assert(direction is 1 or -1)) return false;
        var first = direction > 0 ? 0 : found.Count - 1;
        var next = found[at < 0 ? first : (at + direction + found.Count) % found.Count];
        _focus = next.Row;
        var view = _view.H;
        if (next.Top < _scroll) _scroll = next.Top;
        else if (view > 0 && next.Top + next.Height > _scroll + view) _scroll = next.Top + next.Height - view;
        return true;
    }
}
