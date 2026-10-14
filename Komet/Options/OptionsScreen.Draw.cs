using Komet.Api;
using Komet.Hud;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Options.KometPages;

namespace Komet.Options;

// Recomposed only on change; the hover highlight is a translucent rectangle on top.
internal sealed partial class OptionsScreen
{
    // Sizes before GUI scale
    private const double TextScale = 1.4, Margin = 28, Space = 10, SideWidth = 300, MinSide = 220, ContentWidth = 700,
        MinContent = 420, DescWidth = 360, MinDesc = 240, SideShare = 0.2, DescShare = 0.24, SearchHeight = 44,
        SectionHeight = 52, PageHeight = 44, GroupHeight = 32, RowHeight = 42, SideRow = 34, Spacer = 8, Pad = 18, Box = 18,
        TrackWidth = 220, ButtonWidth = 200, ButtonHeight = 42, AccentBar = 4;

    private const int MaxItems = 512, MaxLines = 12, MaxWords = 256, MaxSteps = 64, MaxDialogs = 256;
    private const double GearSize = 85, GearShown = 0.55, MinShare = 0.5; // the hotbar's temporal gear: its size, the part above

    private const float DimDepth = 900, ScreenDepth = 1000;
    private const int Dim = 150, BlurDim = 90; // the dim's alpha, lighter over the blur
    private const double FadeSeconds = 0.5;
    private const double SoftGlow = 0.04, RowGlow = 0.14, StrongGlow = 0.22, Flash = 0.1, Speed = 20, FlashFade = 7, Settle = 0.003;
    private const int TopGroup = 1, SideGroup = 2, ListGroup = 3, ButtonGroup = 4;
    private const float GlowDepth = ScreenDepth + 1;
    private double _fade; // how far the blur and the dim have come in since the screen opened, 0 to 1
    private (double X, double Y, double W, double H) _sideView;
    private double _sideScroll;
    private bool _sideFollow = true; // the chosen page is brought into the sidebar's view at the next composition
    private (double X, double Y) _mouse = (-1, -1); // in canvas pixels
    private Light _glow, _target;
    private double _flash;

    private static readonly Rgba Panel = new(0, 0, 0, 0.62), HeaderBack = new(0, 0, 0, 0.8),
        RowBack = new(0, 0, 0, 0.5), Selected = Rgba.White(0.08), Title = new(0.55, 0.78, 1, 1),
        Faint = Rgba.White(0.5), Soft = Rgba.White(0.82), BoxLine = Rgba.White(0.7);

    private enum ItemKind
    {
        Section,
        Page,
        Group,
        Row,
        Space
    }

    private sealed record Item(ItemKind Kind, double Height, OptionPage Page, OptionRow? Row = null, string Text = "");

    private sealed record Columns(double Width, double Height, double Side, double Content, double Desc, double Top, double Gap);

    private readonly record struct Light(double X, double Y, double W, double H, double Alpha, int Group);

    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !IsOpened()) return;
        if (_embedded is { Handler: GuiDialog menu } && !menu.IsOpened())
        {
            Leave(); // the escape menu closed under the screen
            return;
        }

        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (!Assert(w > 0 && h > 0)) return;
        _fonts.Update(TextScale);
        var bottom = Math.Round(Bottom(h));
        Animate(deltaTime);
        if (_dirty || _epoch != _fonts.Epoch || _frame != (w, h) || Math.Abs(bottom - _bottom) >= 1) Compose(w, h, bottom);
        // HUD depth left behind would let hotbar, chat and minimap beat the screen; no depth test, no depth written.
        capi.Render.GLDisableDepthTest();
        try
        {
            _fade = Finite(_fade) ? Math.Min(1, _fade + deltaTime / FadeSeconds) : 1;
            var eased = _fade * _fade * (3 - 2 * _fade); // smoothstep: no jolt at either end
            var dim = _backdrop.Draw(DimDepth - 1, eased) ? BlurDim : Dim;
            capi.Render.RenderRectangle(0, 0, DimDepth, w, h, ColorUtil.ToRgba((int)Math.Round(dim * eased), 0, 0, 0));
            if (!_canvas.Ready) return;
            _canvas.Draw(Math.Round((w - _canvas.Width) / 2), Math.Round(scaled(Margin)), ScreenDepth);
            DrawNames();
            Glow(deltaTime);
        }
        finally
        {
            capi.Render.GLEnableDepthTest(); // as the ortho stage has it
        }
    }

    private void Compose(int width, int height, double bottom)
    {
        if (!Assert(width > 0) || !Assert(height > 0) || !Assert(bottom <= height)) return;
        if (_epoch != _fonts.Epoch) _widest.Clear();
        (_dirty, _epoch, _frame, _bottom) = (false, _fonts.Epoch, (width, height), bottom);
        Composing?.Invoke();
        _hits.Clear();
        _rows.Clear();
        _canvas.BeginMeasure();
        var c = Measure(width, bottom);
        var items = Items();
        var current = Scroll(items, c);
        if (!_canvas.Blank(c.Width, c.Height)) return;
        Search(c);
        Sidebar(c, current);
        Content(c, items);
        Description(c);
        Buttons(c);
        Picker(c);
        _canvas.End();
        Retarget(); // the boxes moved (a scroll, another page): the highlight follows what is under the cursor now
    }

    // Above the hotbar, its bars and gear; the frame margin without them (spectator) or when less than half would be
    // left
    private double Bottom(int height)
    {
        var (bottom, top) = (height - scaled(Margin), (double)height);
        var gui = capi.Gui?.OpenedGuis;
        if (gui is null || capi.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return bottom;
        for (var i = 0; i < Math.Min(gui.Count, MaxDialogs); i++) // every frame: no enumerator
        {
            var dialog = gui[i];
            var bounds = dialog switch
            {
                HudHotbar => dialog.Composers["hotbar"]?.Bounds,
                HudStatbar => dialog.Composers["statbar"]?.Bounds,
                _ => null
            };
            if (bounds is null || !Finite(bounds.renderY) || bounds.renderY <= 0) continue;
            top = Math.Min(top, bounds.renderY);
            // renderGear: gear (85 scaled, 5 around) reaches 0.55 of itself above a line 30 under the composer top
            if (dialog is HudHotbar && capi.World?.Config?.GetBool("temporalStability", true) != false)
                top = Math.Min(top, bounds.renderY + scaled(30) - (Math.Ceiling(scaled(GearSize)) + 10) * GearShown);
        }

        var above = top - S(Space);
        return above - scaled(Margin) >= height * MinShare ? Math.Min(bottom, above) : bottom;
    }

    // Sidebar and description within bounds, the list the rest; narrow: description goes, then buttons go under the
    // sidebar
    private static Columns Measure(double width, double bottom)
    {
        double gap = S(Space), room = width - 2 * S(Margin);
        if (!Finite(bottom) || !Assert(room > 0)) return new Columns(1, 1, 1, 1, 0, 0, 0);
        var side = Math.Clamp(room * SideShare, S(MinSide), S(SideWidth));
        var desc = Math.Clamp(room * DescShare, S(MinDesc), S(DescWidth));
        var content = Math.Min(S(ContentWidth), room - side - desc - 2 * gap);
        if (content < S(MinContent)) (desc, content) = (0, Math.Min(S(ContentWidth), room - side - gap));
        content = Math.Max(S(ButtonWidth), content);
        var total = side + gap + content + (desc > 0 ? gap + desc : 0);
        return new Columns(total, bottom - scaled(Margin), side, content, desc, scaled(SearchHeight) + gap, gap);
    }

    private List<Item> Items()
    {
        if (_query.Length > 0) return Found();
        List<Item> items = [];
        if (Array.Find(_pages, p => p.Id == _page) is not { } page) return items;
        items.Add(new Item(ItemKind.Section, S(SectionHeight), page, Text: page.Title));
        for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
        {
            var row = page[i];
            if (!NotNull(row) || !Assert(items.Count < MaxItems)) continue;
            if (row.Kind == OptionKind.Group) items.Add(new Item(ItemKind.Space, scaled(Spacer), page));
            items.Add(row.Kind == OptionKind.Group
                ? new Item(ItemKind.Group, scaled(GroupHeight), page, Text: row.Label)
                : new Item(ItemKind.Row, scaled(RowHeight), page, row));
        }

        _ = Assert(items.Count <= MaxItems);
        return items;
    }

    private List<Item> Found()
    {
        List<Item> items = [];
        foreach (var page in _pages.Bounded(MaxPages))
        {
            var header = false;
            for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
            {
                var row = page[i];
                if (row.Kind == OptionKind.Group || !Matches(row)) continue;
                if (!header) items.Add(new Item(ItemKind.Page, scaled(PageHeight), page, Text: page.Section + " › " + page.Title));
                header = true;
                items.Add(new Item(ItemKind.Row, scaled(RowHeight), page, row));
            }
        }

        _ = Assert(items.Count <= MaxItems);
        if (items.Count == 0 && _pages.Length > 0)
            items.Add(new Item(ItemKind.Group, scaled(GroupHeight), _pages[0], Text: T("nothing-found")));
        return items;
    }

    private bool Matches(OptionRow row) => NotNull(row) &&
        (row.Label.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
         (row.Hint ?? "").Contains(_query, StringComparison.CurrentCultureIgnoreCase));

    private string Scroll(List<Item> items, Columns c)
    {
        double total = 0;
        if (!NotNull(items) || !NotNull(c)) return "";
        foreach (var item in items.Bounded(MaxItems)) total += item.Height;
        if (!Finite(_scroll)) _scroll = 0;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, total - (c.Height - c.Top)));
        return Assert(total >= 0) && _query.Length == 0 ? _page : "";
    }

    private static double S(double size) => Finite(size) ? scaled(size) : 0;

    private List<string> Wrap(string text, CairoFont font, double width)
    {
        List<string> lines = [];
        if (!NotNull(text) || !Assert(width > 0)) return lines;
        foreach (var paragraph in text.Split('\n').Bounded(MaxLines)) _canvas.Wrap(font, paragraph, width, MaxWords, lines);
        return lines;
    }

    private void Retarget()
    {
        var (x, y) = _mouse;
        var hit = PickAt(x, y) ?? Under(_hits, x, y, null); // the open window's gaps (no glow) hide what is under them
        var (vx, vy, vw, vh) = _view;
        if (hit is null && x >= vx && x < vx + vw && y >= vy && y < vy + vh) hit = Under(_rows, x, y, true);
        if (hit is null || !Assert(hit.Glow >= 0) || hit.Glow <= 0)
        {
            _target = _target with { Alpha = 0 };
            return;
        }

        // a half-scrolled row is lit only where it shows
        var (top, bottom) = hit.Group == ListGroup ? (Math.Max(hit.Y, vy), Math.Min(hit.Y + hit.H, vy + vh)) : (hit.Y, hit.Y + hit.H);
        _target = new Light(hit.X, top, hit.W, Math.Max(0, bottom - top), hit.Glow, hit.Group);
    }

    private void Glow(float deltaTime)
    {
        if (!Finite(deltaTime) || !Assert(deltaTime >= 0)) return;
        var (t, k) = (_target, 1 - Math.Exp(-deltaTime * Speed));
        if (t.Alpha > 0 && (_glow.Alpha < Settle || _glow.Group != t.Group))
            _glow = t with { Alpha = _glow.Group == t.Group ? _glow.Alpha : 0 }; // appears where it is: no glide across the screen
        if (t.Alpha > 0) _glow = _glow with { X = Ease(_glow.X, t.X, k), Y = Ease(_glow.Y, t.Y, k), W = Ease(_glow.W, t.W, k),
            H = Ease(_glow.H, t.H, k) };
        _glow = _glow with { Alpha = Ease(_glow.Alpha, t.Alpha, k) };
        _flash *= Math.Exp(-deltaTime * FlashFade);
        var alpha = Math.Min(1, _glow.Alpha + (t.Alpha > 0 ? _flash : 0));
        if (alpha < Settle || _glow.W <= 0 || _glow.H <= 0) return;
        capi.Render.RenderRectangle((float)(_canvas.X + _glow.X), (float)(_canvas.Y + _glow.Y), GlowDepth, (float)_glow.W,
            (float)_glow.H, ColorUtil.ToRgba((int)(alpha * 255), 255, 255, 255));
    }

    private static double Ease(double from, double to, double k) =>
        Finite(from) && Finite(to) && Finite(k) ? from + (to - from) * k : to;
}
