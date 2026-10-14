using System.Text.RegularExpressions;
using Komet.Api;
using Komet.Hud;
using static Komet.Options.KometPages;

namespace Komet.Options;

internal sealed partial class OptionsScreen
{
    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Markup();

    private void Search(Columns c)
    {
        double h = S(SearchHeight), close = c.Desc > 0 ? h : 0, w = c.Side + c.Gap + c.Content - (close > 0 ? 0 : h + c.Gap);
        if (!Assert(w > 0) || !Assert(h > 0)) return;
        _canvas.Fill(0, 0, w, h, Panel);
        var text = _searching ? "" : T("search");
        if (_query.Length > 0) text = _query;
        _canvas.Text(S(Pad), 0, h, _fonts.Text, text + (_searching ? "_" : ""), _query.Length > 0 ? null : Faint);
        _hits.Add(new Hit(0, 0, w, h, (_, _) => _searching = true, Glow: SoftGlow, Group: TopGroup));
        if (_query.Length > 0)
        {
            _canvas.Text(w - S(Pad) - _canvas.TextWidth(_fonts.Header, "×"), 0, h, _fonts.Header, "×", Soft);
            _hits.Insert(0, new Hit(w - h, 0, h, h, (_, _) => (_query, _searching) = ("", false), Glow: StrongGlow, Group: TopGroup));
        }

        var x = close > 0 ? c.Width - h : w + c.Gap;
        _canvas.Fill(x, 0, h, h, Panel);
        _canvas.Text(x + (h - _canvas.TextWidth(_fonts.Header, "×")) / 2, 0, h, _fonts.Header, "×", Soft);
        _hits.Add(new Hit(x, 0, h, h, (_, _) => Close(), Glow: StrongGlow, Group: TopGroup));
    }

    private bool Close()
    {
        _staged.Clear();
        return Assert(_staged.Count == 0) && TryClose();
    }

    // Scrolls when the pages outgrow it (the game's, Komet's and every mod's); the chosen page is brought into view
    // as it is selected (SideFollow), a wheel over the sidebar scrolls it
    private void Sidebar(Columns c, string current)
    {
        var (bottom, rowH) = (c.Height - (c.Desc > 0 ? 0 : 3 * (S(ButtonHeight) + c.Gap)), S(SideRow));
        if (!Assert(bottom > c.Top) || !Assert(rowH > 0)) return;
        _canvas.Fill(0, c.Top, c.Side, c.Height - c.Top, Panel);
        var view = bottom - c.Top;
        _sideView = (0, c.Top, c.Side, view);
        var total = SideScrolled(current, rowH, view);
        _canvas.Clip(0, c.Top, c.Side, view);
        var y = c.Top + S(Spacer) - _sideScroll;
        string? section = null;
        foreach (var page in _pages.Bounded(MaxPages))
        {
            if (page.Section != section)
            {
                if (section is not null) y += 2 * S(Spacer);
                section = page.Section;
                SectionName(y, section, c.Side);
                y += S(SectionHeight);
            }

            var chosen = _query.Length == 0 && page.Id == current;
            if (chosen) Lit(0, y, c.Side, rowH);
            _canvas.Text(S(Pad) * 1.5, y, rowH, _fonts.Text, Fit(page.Title, c.Side - 2.5 * S(Pad)), chosen ? null : Soft);
            if (y + rowH > c.Top && y < bottom)
                _hits.Add(new Hit(0, Math.Max(y, c.Top), c.Side, Math.Min(y + rowH, bottom) - Math.Max(y, c.Top), (_, _) => Select(page),
                    Glow: RowGlow, Group: SideGroup));
            y += rowH;
        }

        if (total > view) // a thumb at the edge says how much lies above and below
        {
            var (thumb, track) = (Math.Max(S(Spacer) * 2, view * view / total), view - 2);
            _canvas.Fill(c.Side - S(AccentBar), c.Top + 1 + (track - thumb) * _sideScroll / Math.Max(1, total - view), S(AccentBar) / 2,
                thumb, Faint);
        }

        _canvas.Unclip();
    }

    // The sidebar's whole height; the scroll clamped to it and, following a selection, moved to show the chosen page
    private double SideScrolled(string current, double rowH, double view)
    {
        var (total, chosenAt) = (S(Spacer), double.NaN);
        string? section = null;
        if (!Assert(rowH > 0) || !Assert(view > 0)) return 0;
        foreach (var page in _pages.Bounded(MaxPages))
        {
            if (page.Section != section)
            {
                if (section is not null) total += 2 * S(Spacer);
                section = page.Section;
                total += S(SectionHeight);
            }

            if (_query.Length == 0 && page.Id == current) chosenAt = total;
            total += rowH;
        }

        total += S(Spacer);
        if (!Finite(_sideScroll)) _sideScroll = 0;
        if (_sideFollow && Finite(chosenAt))
        {
            if (chosenAt < _sideScroll) _sideScroll = chosenAt - S(Spacer);
            else if (chosenAt + rowH > _sideScroll + view) _sideScroll = chosenAt + rowH + S(Spacer) - view;
        }

        _sideFollow = false;
        _sideScroll = Math.Clamp(_sideScroll, 0, Math.Max(0, total - view));
        return total;
    }

    private void SectionName(double y, string section, double width)
    {
        var half = S(SectionHeight) / 2;
        if (!NotNull(section) || !Finite(y) || !Assert(half > 0)) return;
        _canvas.Text(S(Pad), y, half, _fonts.Title, Fit(section, width - 2 * S(Pad), _fonts.Title));
        _canvas.Text(S(Pad), y + half, half, _fonts.Text, Fit(Version(section), width - 2 * S(Pad)), Faint);
    }

    private string Version(string section)
    {
        if (!NotNull(section) || !NotNull(capi)) return "";
        if (section == T("section-game")) return GameVersion.ShortGameVersion;
        var mod = capi.ModLoader?.Mods?.FirstOrDefault(m => m.Info?.Name == section || m.Info?.ModID == section);
        return mod?.Info?.Version ?? "";
    }

    private void Select(OptionPage page)
    {
        if (!NotNull(page) || !Assert(page.Id.Length > 0)) return;
        (_page, _scroll, _query, _searching, _picker, _dirty, _focus, _sideFollow) = (page.Id, 0, "", false, null, true, null, true);
    }

    private void Content(Columns c, List<Item> items)
    {
        var x = c.Side + c.Gap;
        if (!Assert(c.Content > 0) || !NotNull(items)) return;
        _canvas.Clip(x, c.Top, c.Content, c.Height - c.Top);
        _view = (x, c.Top, c.Content, c.Height - c.Top);
        var y = c.Top - _scroll;
        foreach (var item in items.Bounded(MaxItems))
        {
            if (y + item.Height > c.Top && y < c.Height) Paint(item, x, y, c.Content);
            y += item.Height;
        }

        _canvas.Unclip();
    }

    private void Paint(Item item, double x, double y, double w)
    {
        if (!NotNull(item) || !Finite(y)) return;
        switch (item.Kind)
        {
            case ItemKind.Section:
                Band(x, y, w, item.Height);
                _canvas.Text(x + S(Pad), y, item.Height, _fonts.Title,
                    Fit(item.Text, w - 3 * S(Pad) - _canvas.TextWidth(_fonts.Text, item.Page.Section), _fonts.Title));
                Value(x + w - S(Pad), y, item.Height, item.Page.Section, Faint);
                break;
            case ItemKind.Page:
                Band(x, y, w, item.Height);
                _canvas.Text(x + S(Pad), y, item.Height, _fonts.Header, Fit(item.Text, w - 2 * S(Pad), _fonts.Header), Title);
                break;
            case ItemKind.Group:
                _canvas.Fill(x, y, w, item.Height, RowBack);
                _canvas.Text(x + S(Pad), y, item.Height, _fonts.Header, Fit(item.Text.ToUpperInvariant(), w - 2 * S(Pad), _fonts.Header), Faint);
                break;
            case ItemKind.Row when item.Row is { } row:
                Guarded(row, x, y, w, item.Height);
                break;
        }
    }

    // A throwing mod getter costs its row, not the screen; logged once
    private void Guarded(OptionRow row, double x, double y, double w, double h)
    {
        if (!NotNull(row) || !Finite(y)) return;
        try
        {
            Row(row, x, y, w, h);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = _rows.RemoveAll(hit => ReferenceEquals(hit.Option, row));
            _canvas.Text(x + S(Pad), y, h, _fonts.Text, row.Label, Faint);
            if (_failed.Add(row)) capi.Logger?.Warning("Komet options: the row '{0}' failed and is left out: {1}", row.Label, e);
        }
    }

    private void Row(OptionRow row, double x, double y, double w, double h)
    {
        var enabled = row.IsEnabled;
        if (!Assert(h > 0) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, RowBack);
        double staged = _staged.ContainsKey(row) ? 1 : 0;
        if (row.Kind == OptionKind.Slider) staged = MotionOf(row).Staged; // a slider's grows in
        if (staged > 0.01) _canvas.Fill(x, y + h * (1 - staged) / 2, S(AccentBar), h * staged, HudCanvas.Accent);
        var reserved = row.Kind == OptionKind.Slider ? SliderWidth(row) : ValueWidth(row);
        var label = Fit(row.Label, w - reserved - 3 * S(Pad));
        _canvas.Text(x + S(Pad), y, h, _fonts.Text, label, enabled ? null : Faint);
        if (!enabled) _canvas.Fill(x + S(Pad), y + h / 2, _canvas.TextWidth(_fonts.Text, label), 1, Faint);
        _rows.Add(new Hit(x, y, w, h, (_, _) => { }, Option: row, Row: true, Glow: enabled ? RowGlow : SoftGlow, Group: ListGroup));
        var right = x + w - S(Pad);
        switch (row.Kind)
        {
            case OptionKind.Switch:
                Check(row, x, y, w, h, right, enabled);
                break;
            case OptionKind.Choice:
                Choice(row, x, y, w, h, right, enabled);
                break;
            case OptionKind.Slider:
                Slider(row, y, right, h, enabled);
                break;
            case OptionKind.Button:
                Value(right, y, h, Caption(row), enabled ? Title : Faint);
                if (enabled) _rows.Insert(0, new Hit(x, y, w, h, (_, _) => row.Set(0), Option: row));
                break;
            case OptionKind.Key:
                Key(row, x, y, w, h, right, enabled);
                break;
        }
    }

    private static string Caption(OptionRow row) => NotNull(row) ? "›  " + (row.Shows?.Invoke() ?? "") : "";

    private void Key(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        if (!NotNull(row) || !Finite(right)) return;
        var waiting = ReferenceEquals(row, _binding);
        if (waiting) Lit(x, y, w, h);
        var color = enabled ? Soft : Faint;
        if (enabled && row.Warn?.Invoke() == true) color = HudCanvas.Error;
        if (waiting) color = Title;
        Value(right, y, h, Bound(row), color);
        if (!enabled) return;
        _rows.Insert(0, new Hit(x, y, w, h, (_, back) =>
        {
            if (back) EngineOptions.Unbind(row.Code);
            else Binding(row);
        }, Option: row));
    }

    private string Bound(OptionRow row)
    {
        if (!NotNull(row)) return "";
        return ReferenceEquals(row, _binding) ? T("key-waiting") : row.Shows?.Invoke() ?? "";
    }

    private void Check(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        if (!NotNull(row)) return;
        var (box, on) = (S(Box), Value(row) >= 0.5);
        if (!Assert(box > 4)) return;
        var top = y + (h - box) / 2;
        _canvas.Outline(right - box, top, box, box, enabled ? BoxLine : Faint);
        if (on) _canvas.Fill(right - box + 3, top + 3, box - 6, box - 6, enabled ? HudCanvas.Accent : Faint);
        if (enabled) _rows.Insert(0, new Hit(x, y, w, h, (_, _) => Stage(row, on ? 0 : 1), Option: row));
    }

    private void Choice(OptionRow row, double x, double y, double w, double h, double right, bool enabled)
    {
        var names = row.Names;
        if (!Assert(names.Length > 0) || !Finite(right)) return;
        var at = Math.Clamp((int)Math.Round(Value(row)), 0, names.Length - 1);
        var (listed, open) = (names.Length > MaxCycled, ReferenceEquals(row, _picker));
        if (open) Lit(x, y, w, h);
        var color = enabled ? Soft : Faint;
        Value(right, y, h, Shown(row), open ? Title : color);
        if (!enabled) return;
        _rows.Insert(0, new Hit(x, y, w, h, (_, back) =>
        {
            if (listed && !back) (_picker, _pickScroll) = (ReferenceEquals(_picker, row) ? null : row, double.NaN);
            else Stage(row, (at + (back ? names.Length - 1 : 1)) % names.Length);
        }, Option: row));
    }

    private string Shown(OptionRow row)
    {
        var names = row.Names;
        if (!NotNull(names) || names.Length == 0) return "";
        var name = Parts(names[Math.Clamp((int)Math.Round(Value(row)), 0, names.Length - 1)]).Main;
        return names.Length > MaxCycled ? name + "  ›" : name;
    }

    private double ValueWidth(OptionRow row)
    {
        if (!NotNull(row) || !Assert(row.Label.Length > 0)) return 0;
        return row.Kind switch
        {
            OptionKind.Switch => S(Box),
            OptionKind.Choice => _canvas.TextWidth(_fonts.Text, Shown(row)),
            OptionKind.Button => _canvas.TextWidth(_fonts.Text, Caption(row)),
            OptionKind.Key => _canvas.TextWidth(_fonts.Text, Bound(row)),
            _ => Widest(row)
        };
    }

    // Keeps the track still; up to 65 measures, cached per font size
    private double Widest(OptionRow row)
    {
        if (!NotNull(row) || !Assert(row.Step > 0)) return 0;
        if (_widest.TryGetValue(row, out var known)) return known;
        var (widest, steps) = (0.0, (int)Math.Min((row.Max - row.Min) / row.Step, MaxSteps));
        for (var i = 0; i <= Math.Min(steps, MaxSteps); i++)
            widest = Math.Max(widest, _canvas.TextWidth(_fonts.Text, row.ValueText(row.Min + i * row.Step)));
        widest = Math.Max(widest, _canvas.TextWidth(_fonts.Text, row.ValueText(row.Max)));
        if (_widest.Count < MaxItems) _widest[row] = widest;
        return widest;
    }

    // Cut with an ellipsis where it would run past width (the hover description shows it whole)
    private string Fit(string text, double width, CairoFont? font = null) =>
        NotNull(text) && Assert(width > 0) ? _canvas.Fit(font ?? _fonts.Text, text, width, trimmed: true) : text;

    private void Lit(double x, double y, double w, double h)
    {
        if (!Finite(y) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, Selected);
        _canvas.Fill(x + w - S(AccentBar), y, S(AccentBar), h, HudCanvas.Accent);
    }

    private void Band(double x, double y, double w, double h)
    {
        if (!Finite(y) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, HeaderBack);
        _canvas.Fill(x, y, S(AccentBar), h, Title);
    }

    private void Value(double right, double y, double h, string text, Rgba color)
    {
        if (NotNull(text) && Finite(right)) _canvas.Text(right - _canvas.TextWidth(_fonts.Text, text), y, h, _fonts.Text, text, color);
    }

    private void Description(Columns c)
    {
        if (c.Desc <= 0 || _picker is not null || (_hover ?? _focus) is not { } row) return; // an open window shows the hint itself
        double x = c.Width - c.Desc, pad = S(Pad), line = _fonts.TextRow;
        var hint = Markup().Replace(row.Hint ?? "", ""); // the game's hints carry VTML
        if (ReferenceEquals(row, _focus)) hint += (hint.Length > 0 ? "\n\n" : "") + T("slider-keys");
        if (row.Locked?.Invoke() is { } locked) hint = locked + "\n" + hint;
        var (title, lines) = (Wrap(row.Label, _fonts.Header, c.Desc - 2 * pad), Wrap(hint, _fonts.Text, c.Desc - 2 * pad));
        var room = c.Height - c.Top - 3 * (S(ButtonHeight) + c.Gap) - 2 * pad - title.Count * _fonts.HeaderRow; // above the buttons
        if (!Assert(line > 0)) return;
        if (lines.Count * line > room) // the room above the buttons: the last line says there is more
        {
            lines = [.. lines.Take(Math.Max(1, (int)(room / line)))];
            lines[^1] = Fit(lines[^1].TrimEnd() + " …", c.Desc - 2 * pad);
        }

        _canvas.Fill(x, c.Top, c.Desc, 2 * pad + (title.Count * _fonts.HeaderRow) + lines.Count * line, Panel);
        var y = c.Top + pad;
        foreach (var text in title.Bounded(MaxLines))
        {
            _canvas.Text(x + pad, y, _fonts.HeaderRow, _fonts.Header, text);
            y += _fonts.HeaderRow;
        }

        foreach (var text in lines.Bounded(MaxLines * 4))
        {
            _canvas.Text(x + pad, y, line, _fonts.Text, text, Soft);
            y += line;
        }
    }

    private void Buttons(Columns c)
    {
        double w = c.Desc > 0 ? Math.Min(c.Desc, S(ButtonWidth)) : c.Side, h = S(ButtonHeight);
        var x = c.Desc > 0 ? c.Width - w : 0;
        if (!Assert(w > 0) || !Assert(h > 0)) return;
        var y = c.Height - h;
        Button(x, y, w, h, T("done"), true, () =>
        {
            Apply();
            _ = Close();
        });
        y -= h + c.Gap;
        var count = _staged.Count;
        Button(x, y, w, h, count > 0 ? T("apply") + " (" + count + ")" : T("apply"), count > 0, Apply);
        if (count == 0) return;
        y -= h + c.Gap;
        Button(x, y, w, h, T("undo"), true, () =>
        {
            _staged.Clear();
            _dirty = true;
        });
    }

    private void Button(double x, double y, double w, double h, string text, bool enabled, Action click)
    {
        if (!NotNull(click) || !Assert(w > 0)) return;
        _canvas.Fill(x, y, w, h, HeaderBack);
        _canvas.Text(x + (w - _canvas.TextWidth(_fonts.Text, text)) / 2, y, h, _fonts.Text, text, enabled ? null : Faint);
        if (enabled) _hits.Insert(0, new Hit(x, y, w, h, (_, _) => click(), Glow: StrongGlow, Group: ButtonGroup));
    }
}
