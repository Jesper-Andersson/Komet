using Komet.Diagnostics;

namespace Komet.Hud;

// Mods tab: the list, the conflicts and one mod's method timings.
internal sealed partial class HudOverlay
{
    private const int RowsPerPage = 9, MaxModRows = 1024, MaxConflictRows = 24, MaxQuery = 32, ProfileShown = 12;
    private static readonly Rgba[] TopColors =
    [
        new(220 / 255.0, 150 / 255.0, 50 / 255.0, 1), new(200 / 255.0, 170 / 255.0, 60 / 255.0, 1),
        new(170 / 255.0, 160 / 255.0, 70 / 255.0, 1), new(140 / 255.0, 150 / 255.0, 80 / 255.0, 1)
    ];

    private readonly record struct ModRow(string Id, string Name, string Version, bool Code);

    private ModRow[] _modRows = [];
    private Dictionary<string, (int Prefix, int Postfix, int Transpiler, int Finalizer)> _patchesBy = [];
    private HarmonyAudit? _patchesOf;
    private string _modQuery = "", _modFilter = "code", _modSort = "ms", _modSelected = "", _profileMod = "";
    private bool _modTyping, _byPair = true;
    private int _modView, _modPage; // 0 list, 1 conflicts, 2 timing
    private readonly HashSet<int> _confOpen = [0];
    private ModProfiler? _profileResult; // the last finished profile, kept for its rows in any order
    private (string Field, string Kind, long Count)[] _profileHoldings = [];
    private string _profileSort = "self", _profileOpen = "";

    private void ModsView(HudUi ui)
    {
        _ = Assert(_modView is >= 0 and <= 2);
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        if (_modRows.Length == 0) _modRows = LoadedMods();
        Patches();
        if (_modView == 1) ConflictsView(ui);
        else if (_modView == 2) ProfileView(ui);
        else ModList(ui);
    }

    private ModRow[] LoadedMods()
    {
        var rows = new List<ModRow>();
        foreach (var mod in _capi.ModLoader.Mods.Bounded(MaxModRows))
            if (mod.Info is { } info)
                rows.Add(new ModRow(info.ModID, info.Name, info.Version, mod is Vintagestory.Common.ModContainer { Assembly: not null }));
        return Assert(rows.Count <= MaxModRows) ? [.. rows] : [];
    }

    private void Patches()
    {
        if (_audit is not { } audit || ReferenceEquals(audit, _patchesOf)) return;
        var by = new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal);
        foreach (var method in audit.Methods.Bounded(HarmonyAudit.MaxMethods))
            foreach (var link in method.Chain.Bounded(HarmonyAudit.MaxPatches * 4))
            {
                var (a, b, c, d) = by.GetValueOrDefault(link.Owner);
                by[link.Owner] = link.Kind switch
                {
                    PatchKind.Prefix => (a + 1, b, c, d), PatchKind.Postfix => (a, b + 1, c, d), PatchKind.Transpiler => (a, b, c + 1, d),
                    _ => (a, b, c, d + 1)
                };
            }

        (_patchesBy, _patchesOf) = (by, audit);
        _ = Assert(by.Count <= audit.Owners.Length + 1);
    }

    private int PatchCount(string id) => _patchesBy.TryGetValue(id, out var p) && Assert(p.Prefix >= 0) ? p.Prefix + p.Postfix + p.Transpiler + p.Finalizer : 0;

    private string ModName(string id) =>
        Array.Find(_modRows, r => r.Id == id) is { Name.Length: > 0 } row ? row.Name : _capi.ModLoader.GetMod(id)?.Info.Name ?? id;

    private PatchedMethod? ConflictOf(string id) =>
        _audit?.Methods.FirstOrDefault(m => m.Risk != PatchRisk.Low && m.Chain.Any(l => l.Owner == id) && id != KometModSystem.ModId);

    private void ModList(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var code = _modRows.Count(static r => r.Code);
        (ModRow Row, double Ms)[] top = [.. _modRows.Where(static r => r.Code && r.Id != KometModSystem.ModId)
            .Select(r => (Row: r, Ms: _timings.MsOf(r.Id))).Where(static t => t.Ms > 0).OrderByDescending(static t => t.Ms).Take(4)];
        var total = _timings.TotalExcept(""); // every row listed, Essentials (modid game) too
        ui.Space(6);
        ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Strong, T("hud-v-mods-count", _modRows.Length));
        var x = ui.X + ui.TextW(ui.F.Strong, T("hud-v-mods-count", _modRows.Length)) + ui.Px(5);
        ui.Text(x, ui.Y, ui.F.BodyRow, ui.F.Body, T("hud-v-mods-split", code, _modRows.Length - code), HudUi.Dim);
        var right = T("hud-v-per-frame");
        ui.TextRight(ui.X + ui.W, ui.Y, ui.F.BodyRow, ui.F.Body, right, HudUi.Dim);
        ui.TextRight(ui.X + ui.W - ui.TextW(ui.F.Body, right) - ui.Px(4), ui.Y, ui.F.BodyRow, ui.F.Strong, N(total, 2) + " ms");
        ui.Y += ui.F.BodyRow + ui.Px(4);
        Span<(double, Rgba)> parts = stackalloc (double, Rgba)[5];
        for (var i = 0; i < Math.Min(top.Length, 4); i++) parts[i] = (top[i].Ms, TopColors[i]);
        parts[top.Length] = (Math.Max(0, total - top.Sum(static t => t.Ms)), Rgba.White(0.22));
        ui.Segments(ui.Y, ui.Px(8), parts[..(top.Length + 1)], Math.Max(total, 1e-9));
        ui.Y += ui.Px(10);
        TopNames(ui, [.. top.Select(static t => t.Row)], code);
        ConflictBanner(ui);
        SearchRow(ui);
        ModTable(ui);
    }

    private void TopNames(HudUi ui, ModRow[] top, int code)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var x = ui.X;
        for (var i = 0; i < Math.Min(top.Length, 4); i++)
        {
            var (row, name) = (top[i], top[i].Name);
            ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, name, TopColors[i]);
            ui.Click(x, ui.Y, ui.TextW(ui.F.Small, name), ui.F.SmallRow, () => (_modSelected, _modQuery) = (row.Id, ""));
            x += ui.TextW(ui.F.Small, name) + ui.Px(4);
            ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, "·", HudUi.Dim);
            x += ui.TextW(ui.F.Small, "·") + ui.Px(4);
        }

        ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-v-more-mods", Math.Max(0, code - top.Length)), HudUi.Dim);
        ui.Y += ui.F.SmallRow;
    }

    private void ConflictBanner(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var risky = _audit?.Methods.Count(static m => m.Risk != PatchRisk.Low) ?? 0;
        if (risky == 0) return;
        ui.Space(6);
        var h = ui.F.BodyRow + ui.Px(6);
        ui.Fill(ui.X, ui.Y, ui.W, h, HudUi.Warn with { A = 0.1 }, ui.Px(3));
        ui.Text(ui.X + ui.Px(6), ui.Y + ui.Px(3), ui.F.BodyRow, ui.F.Body, T("hud-v-risky", risky), HudUi.Warn);
        var link = T("hud-v-look-more");
        _ = ui.LinkText(ui.X + ui.W - ui.Px(6) - ui.TextW(ui.F.Body, link), ui.Y + ui.Px(3), link, ShowConflicts);
        ui.Y += h + ui.Px(6);
    }

    private void SearchRow(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var chipsW = 0.0;
        foreach (var text in (string[])[T("hud-v-filter-code"), T("hud-v-all"), T("hud-v-filter-conflict")])
            chipsW += ui.TextW(ui.F.Small, text) + ui.Px(18);
        var (h, boxW) = (ui.Px(20), ui.W - chipsW - ui.Px(6));
        ui.Fill(ui.X, ui.Y, boxW, h, _modTyping ? Rgba.White(0.14) : HudUi.ChipBack, ui.Px(3));
        var caret = _modTyping ? "|" : "";
        var shown = _modQuery.Length > 0 || _modTyping ? _modQuery + caret : T("hud-v-search");
        Rgba? color = _modQuery.Length > 0 || _modTyping ? null : HudUi.Dim;
        ui.Text(ui.X + ui.Px(7), ui.Y, h, ui.F.Body, ui.Fit(ui.F.Body, shown, boxW - ui.Px(14)), color);
        ui.Click(ui.X, ui.Y, boxW, h, () => _modTyping = true);
        _ = ui.Chips(ui.X + boxW + ui.Px(6), ui.Y + (h - ui.ChipHeight) / 2,
        [
            (T("hud-v-filter-code"), _modFilter == "code", () => (_modFilter, _modPage) = ("code", 0)),
            (T("hud-v-all"), _modFilter == "all", () => (_modFilter, _modPage) = ("all", 0)),
            (T("hud-v-filter-conflict"), _modFilter == "conf", () => (_modFilter, _modPage) = ("conf", 0))
        ]);
        ui.Y += h + ui.Px(4);
    }

    private ModRow[] Filtered()
    {
        var q = _modQuery.Trim();
        IEnumerable<ModRow> rows = _modRows.Where(r => q.Length == 0
            ? _modFilter == "all" || (_modFilter == "code" && r.Code) || (_modFilter == "conf" && ConflictOf(r.Id) is not null)
            : r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
        rows = _modSort switch
        {
            "name" => rows.OrderBy(static r => r.Name, StringComparer.OrdinalIgnoreCase),
            "p" => rows.OrderByDescending(r => PatchCount(r.Id)).ThenBy(static r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => rows.OrderByDescending(r => _timings.MsOf(r.Id)).ThenBy(static r => r.Name, StringComparer.OrdinalIgnoreCase)
        };
        return Assert(_modRows.Length <= MaxModRows) ? [.. rows] : [];
    }

    private void ModTable(HudUi ui)
    {
        if (!Assert(RowsPerPage > 0)) return;
        var rows = Filtered();
        var pages = Math.Max(1, (rows.Length + RowsPerPage - 1) / RowsPerPage);
        _modPage = Math.Clamp(_modPage, 0, pages - 1);
        TableHead(ui);
        foreach (var row in rows.AsSpan(_modPage * RowsPerPage, Math.Min(RowsPerPage, rows.Length - _modPage * RowsPerPage)).Bounded(RowsPerPage))
        {
            ModLine(ui, row);
            if (row.Id == _modSelected) Detail(ui, row);
        }

        if (rows.Length == 0) ui.Line(T("hud-v-no-match"), HudUi.Dim);
        ui.Space(4);
        var content = _modRows.Length - _modRows.Count(static r => r.Code);
        if (_modFilter == "code" && _modQuery.Length == 0 && content > 0)
            _ = ui.LinkText(ui.X, ui.Y, T("hud-v-content-mods", content), () => (_modFilter, _modPage) = ("all", 0), ui.F.Small);
        else ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-v-mods-count", rows.Length), HudUi.Dim);
        Pager(ui, pages);
        ui.Y += ui.F.SmallRow;
    }

    private void Pager(HudUi ui, int pages)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var text = $"{_modPage + 1} / {pages}";
        var right = ui.X + ui.W;
        var nextW = ui.TextW(ui.F.Body, "›");
        ui.Text(right - nextW, ui.Y, ui.F.SmallRow, ui.F.Body, "›", Rgba.White(0.85));
        ui.Click(right - nextW - ui.Px(4), ui.Y, nextW + ui.Px(8), ui.F.SmallRow, () => _modPage = Math.Min(pages - 1, _modPage + 1));
        right -= nextW + ui.Px(6);
        ui.TextRight(right, ui.Y, ui.F.SmallRow, ui.F.Small, text, HudUi.Dim);
        right -= ui.TextW(ui.F.Small, text) + ui.Px(6);
        ui.Text(right - nextW, ui.Y, ui.F.SmallRow, ui.F.Body, "‹", Rgba.White(0.85));
        ui.Click(right - nextW - ui.Px(4), ui.Y, nextW + ui.Px(8), ui.F.SmallRow, () => _modPage = Math.Max(0, _modPage - 1));
        _ = Assert(pages > 0);
    }

    private static (double Bar, double Patches) Columns(HudUi ui) =>
        (ui.X + ui.W - ui.Px(92 + 40 + 14 + 24), ui.X + ui.W - ui.Px(14 + 8));

    private void TableHead(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (barX, patchesRight) = Columns(ui);
        var h = ui.F.SmallRow + ui.Px(2);
        (string Key, string Text, double X)[] heads = [("name", T("hud-v-col-name"), ui.X + ui.Px(4)), ("ms", T("hud-v-col-ms"), barX),
            ("p", T("hud-v-col-patches"), patchesRight - ui.TextW(ui.F.Small, T("hud-v-col-patches")) - ui.Px(11))];
        foreach (var (key, text, x) in heads.Bounded(3))
        {
            var w = ui.TextW(ui.F.Small, text);
            ui.Text(x, ui.Y, h, ui.F.Small, text, _modSort == key ? null : HudUi.Dim);
            if (_modSort == key) w += ui.Px(4) + ui.Caret(x + w + ui.Px(4), ui.Y, h, true);
            ui.Click(x, ui.Y, w, h, () => _modSort = key);
        }

        ui.Y += h;
    }

    private void ModLine(HudUi ui, ModRow row)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (barX, patchesRight) = Columns(ui);
        var h = ui.F.BodyRow + ui.Px(4);
        if (row.Id == _modSelected) ui.Fill(ui.X, ui.Y, ui.W, h, HudUi.Accent with { A = 0.3 }, ui.Px(2));
        var nameX = ui.X + ui.Px(4);
        if (_settings.PinnedMods.Contains(row.Id))
        {
            ui.Pin(nameX, ui.Y, h);
            nameX += ui.Px(10);
        }

        ui.Text(nameX, ui.Y, h, ui.F.Body, ui.Fit(ui.F.Body, row.Name, barX - nameX - ui.Px(8)));
        var ms = _timings.MsOf(row.Id);
        if (row.Code)
        {
            ui.Bar(barX, ui.Y, ui.Px(40), h, ms / 0.45 * 100);
            ui.Text(barX + ui.Px(46), ui.Y, h, ui.F.Body, N(ms, 2));
        }
        else ui.Text(barX, ui.Y, h, ui.F.Body, T("hud-v-content"), HudUi.Dim);

        var patches = PatchCount(row.Id);
        ui.TextRight(patchesRight, ui.Y, h, ui.F.Body, patches > 0 ? N(patches) : "–", patches > 0 ? null : HudUi.Dim);
        if (ConflictOf(row.Id) is not null) ui.Warning(ui.X + ui.W - ui.Px(12), ui.Y, h);
        ui.Click(ui.X, ui.Y, ui.W, h, () => _modSelected = _modSelected == row.Id ? "" : row.Id);
        ui.Y += h;
    }

    private void Detail(HudUi ui, ModRow row)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var top = ui.Y;
        var (x, w) = (ui.X + ui.Px(14), ui.W - ui.Px(14));
        ui.Y += ui.Px(3);
        var version = "v" + row.Version;
        ui.Text(x, ui.Y, ui.ChipHeight, ui.F.Small, version, HudUi.Dim);
        if (!row.Code)
        {
            ui.Text(x + ui.TextW(ui.F.Small, version) + ui.Px(6), ui.Y, ui.ChipHeight, ui.F.Small, T("hud-v-content-only"), HudUi.Dim);
            ui.Y += ui.ChipHeight;
        }
        else CodeDetail(ui, row, x, w);

        ui.Y += ui.Px(5);
        ui.Fill(ui.X, top, ui.Px(2), ui.Y - top, HudUi.Accent);
        ui.Fill(ui.X + ui.Px(2), top, ui.W - ui.Px(2), ui.Y - top, Rgba.White(0.03));
        ui.Space(2);
    }

    private void CodeDetail(HudUi ui, ModRow row, double x, double w)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var pinned = _settings.PinnedMods.Contains(row.Id);
        var pin = T(pinned ? "hud-v-pinned-chip" : "hud-v-pin");
        _ = ui.Chip(x + w - ui.TextW(ui.F.Small, pin) - ui.Px(14), ui.Y, pin, pinned, () => _settings.TogglePin(row.Id));
        ui.Y += ui.ChipHeight + ui.Px(2);
        var place = Enumerable.Range(0, ModTimes.MaxMods).FirstOrDefault(i => _timings.ModName(i) == row.Id, -1);
        for (var r = 0; place >= 0 && r < ModTimes.DetailCount; r++)
            if (_timings.DetailName(place, r) is { Length: > 0 } name)
            {
                ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, name, w - ui.Px(60)));
                ui.TextRight(x + w, ui.Y, ui.F.SmallRow, ui.F.Small, N(_timings.DetailMs(place, r), 2) + " ms");
                ui.Y += ui.F.SmallRow;
            }

        if (_patchesBy.TryGetValue(row.Id, out var p))
        {
            ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-v-patches-split", p.Prefix + p.Postfix + p.Transpiler + p.Finalizer, p.Prefix,
                p.Postfix, p.Transpiler), HudUi.Dim);
            ui.Y += ui.F.SmallRow;
        }

        if (ConflictOf(row.Id) is { } conflict)
        {
            var text = T("hud-v-conflict-in", Describe(conflict.Name));
            ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, text, w - ui.Px(60)), HudUi.Warn);
            _ = ui.LinkText(x + w - ui.TextW(ui.F.Small, T("hud-v-look")), ui.Y, T("hud-v-look"), ShowConflicts, ui.F.Small);
            ui.Y += ui.F.SmallRow;
        }

        ui.Space(3);
        _ = ui.LinkText(x, ui.Y, T("hud-v-measure-functions"), () => ShowProfile(row.Id, false));
        ui.Y += ui.F.BodyRow;
    }

    private void ShowConflicts() => _ = Select(1);

    private bool Select(int view)
    {
        _modView = view;
        _window.Select(HudWindow.Mods);
        return Assert(view is >= 0 and <= 2);
    }

    private void ShowProfile(string mod, bool start)
    {
        if (!Assert(mod.Length > 0)) return;
        (_profileMod, _profileResult) = (mod, _profileResult?.ModId == mod ? _profileResult : null);
        _ = Select(2);
        if (start && _profile is null) _capi.ShowChatMessage(ProfileCommand(mod, null).StatusMessage);
    }

    private void ConflictsView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Space(6);
        _ = ui.LinkText(ui.X, ui.Y, T("hud-v-back"), () => _modView = 0);
        var chipsW = ui.TextW(ui.F.Small, T("hud-v-by-pair")) + ui.TextW(ui.F.Small, T("hud-v-by-method")) + ui.Px(32);
        _ = ui.Chips(ui.X + ui.W - chipsW, ui.Y, [(T("hud-v-by-pair"), _byPair, () => _byPair = true),
            (T("hud-v-by-method"), !_byPair, () => _byPair = false)]);
        ui.Y += Math.Max(ui.F.BodyRow, ui.ChipHeight) + ui.Px(4);
        var shared = _audit?.Methods.Where(static m => m.Owners > 1).ToArray() ?? [];
        if (_audit is null) ui.Line(T("hud-v-walking"), HudUi.Dim);
        var groups = _byPair ? PairGroups(shared) : [.. shared.Select(m => (Title: Describe(m.Name), Other: Owners(m), Method: m))];
        for (var i = 0; i < Math.Min(groups.Length, MaxConflictRows); i++) ConflictGroup(ui, i, groups[i]);
        ui.Rule(3);
        ui.Wrap(T("hud-v-conflicts-footer", shared.Length, shared.Count(static m => m.Risk == PatchRisk.Low)), HudUi.Dim, ui.F.Small);
    }

    private static string Owners(PatchedMethod method) =>
        NotNull(method) ? string.Join(" × ", method.Chain.Select(static l => l.Owner).Distinct()) : "";

    private static (string Title, string Other, PatchedMethod Method)[] PairGroups(PatchedMethod[] shared)
    {
        var pairs = new Dictionary<string, PatchedMethod>(StringComparer.Ordinal);
        foreach (var method in shared.Bounded(HarmonyAudit.MaxMethods))
        {
            var owners = method.Chain.Select(static l => l.Owner).Distinct().OrderBy(static o => o, StringComparer.Ordinal).ToArray();
            for (var a = 0; a < Math.Min(owners.Length, 8); a++)
                for (var b = a + 1; b < Math.Min(owners.Length, 8); b++)
                {
                    var key = owners[a] + " × " + owners[b];
                    if (!pairs.TryGetValue(key, out var seen) || seen.Risk < method.Risk) pairs[key] = method;
                }
        }

        return Assert(pairs.Count <= HarmonyAudit.MaxMethods * 28) ? [.. pairs.OrderByDescending(static p => p.Value.Risk).Select(p => (p.Key, Describe(p.Value.Name), p.Value))] : [];
    }

    private void ConflictGroup(HudUi ui, int index, (string Title, string Other, PatchedMethod Method) group)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var open = _confOpen.Contains(index);
        ui.Rule(2);
        var (risk, color) = group.Method.Risk switch
        {
            PatchRisk.High => (T("hud-v-risk-high"), HudUi.Err), PatchRisk.Medium => (T("hud-v-risk-medium"), HudUi.Warn),
            _ => (T("hud-v-risk-info"), HudUi.Dim)
        };
        var (h, indent) = (ui.F.BodyRow, ui.Px(13));
        _ = ui.Caret(ui.X + ui.Px(1), ui.Y, h, open);
        ui.Text(ui.X + indent, ui.Y, h, ui.F.Body, ui.Fit(ui.F.Body, group.Title, ui.W - indent - ui.TextW(ui.F.Body, risk) - ui.Px(10)));
        ui.TextRight(ui.X + ui.W, ui.Y, h, ui.F.Body, risk, color);
        ui.Click(ui.X, ui.Y, ui.W, h, () => _ = _confOpen.Add(index) || _confOpen.Remove(index));
        ui.Y += h;
        if (!open) return;
        var x = ui.X + ui.Px(14);
        ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, group.Other, ui.W - ui.Px(14)), HudUi.Dim);
        ui.Y += ui.F.SmallRow + ui.Px(3);
        Chain(ui, x, group.Method);
        var what = T(group.Method.Risk switch
        {
            PatchRisk.High => DebugProtocol.Effect(group.Method),
            PatchRisk.Medium => "hud-dbg-effect-skip",
            _ => "hud-v-effect-none"
        });
        ui.Wrap(what, null, ui.F.Small, ui.Px(14));
        ui.Space(3);
    }

    private static void Chain(HudUi ui, double left, PatchedMethod method)
    {
        var (x, h) = (left, ui.F.SmallRow + ui.Px(4));
        foreach (var link in method.Chain.Bounded(HarmonyAudit.MaxPatches))
        {
            var kind = link.Skips ? T("hud-v-skips", link.Kind) : link.Kind.ToString();
            var w = ui.TextW(ui.F.Small, link.Owner) + ui.TextW(ui.F.Small, kind) + ui.Px(16);
            if (x + w > ui.X + ui.W && x > left) (x, ui.Y) = (left, ui.Y + h + ui.Px(3));
            ui.Fill(x, ui.Y, w, h, HudUi.StatBack, ui.Px(3));
            ui.Text(x + ui.Px(6), ui.Y + ui.Px(2), ui.F.SmallRow, ui.F.Small, link.Owner);
            ui.Text(x + ui.Px(10) + ui.TextW(ui.F.Small, link.Owner), ui.Y + ui.Px(2), ui.F.SmallRow, ui.F.Small, kind, HudUi.Dim);
            x += w + ui.Px(3);
            ui.Text(x, ui.Y + ui.Px(2), ui.F.SmallRow, ui.F.Small, "›", HudUi.Dim);
            x += ui.TextW(ui.F.Small, "›") + ui.Px(3);
        }

        ui.Y += h + ui.Px(4);
        _ = Assert(x >= left);
    }

    private void ProfileView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var running = _profile is { } p && p.ModId == _profileMod;
        ui.Space(6);
        _ = ui.LinkText(ui.X, ui.Y, T("hud-v-back"), () => _modView = 0);
        if (running)
        {
            var state = T("hud-v-measuring", N(_profile!.Elapsed), N(_profile.Seconds));
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.BodyRow, ui.F.Body, state);
            ui.Fill(ui.X + ui.W - ui.TextW(ui.F.Body, state) - ui.Px(12), ui.Y + ui.F.BodyRow / 2 - ui.Px(3), ui.Px(6), ui.Px(6), HudUi.Err, ui.Px(3));
        }
        else
        {
            var label = T(_profileResult is not null ? "hud-v-measure-again" : "hud-v-measure-10");
            _ = ui.Go(ui.X + ui.W - ui.TextW(ui.F.Strong, label) - ui.Px(24), ui.Y - ui.Px(2), label,
                () => _capi.ShowChatMessage(ProfileCommand(_profileMod, null).StatusMessage), enabled: _profile is null);
        }

        ui.Y += ui.GoHeight + ui.Px(4);
        var title = ModName(_profileMod);
        ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, title);
        if (_profileResult is { } done && !running)
            ui.Text(ui.X + ui.TextW(ui.F.Strong, title) + ui.Px(6), ui.Y, ui.F.StrongRow, ui.F.Small, done.Version, HudUi.Dim);
        ui.Y += ui.F.StrongRow + ui.Px(4);
        if (running) ProfileRunning(ui);
        else if (_profileResult is not { } result) ui.Wrap(T("hud-v-profile-idle"), HudUi.Dim);
        else
        {
            ProfileSummaryView(ui, result.Summary());
            ProfileTable(ui, result);
            ProfileHoldings(ui);
        }
    }

    private void ProfileRunning(HudUi ui)
    {
        if (!NotNull(ui)) return;
        if (_profile is not { } p || !Assert(p.Seconds > 0)) return;
        ui.Space(4);
        ui.Fill(ui.X, ui.Y, ui.W, ui.Px(4), Rgba.White(0.1), ui.Px(2));
        ui.Fill(ui.X, ui.Y, ui.W * Math.Clamp(p.Elapsed / p.Seconds, 0, 1), ui.Px(4), HudUi.Stop, ui.Px(2));
        ui.Space(10);
        ui.Line(T(p.State == ModProfiler.Phase.Patching ? "hud-v-profile-patching" : "hud-v-profile-progress", N(p.Frames), N(p.Methods)), HudUi.Dim);
    }

    private static void ProfileSummaryView(HudUi ui, ModProfiler.ProfileSummary s)
    {
        if (!NotNull(ui) || !Assert(s.Frames >= 0)) return;
        Rgba? worst = null;
        if (s.WorstMs >= ModProfiler.SlowMs) worst = s.WorstMs >= 8 ? HudUi.Err : HudUi.Warn;
        ui.Grid(4, 4, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, T("hud-v-prof-mean"), N(s.MsPerFrame, 3) + " ms", T("hud-v-prof-p99", N(s.P99Ms, 2))),
            1 => ui.Stat(x, y, w, T("hud-v-prof-worst"), N(s.WorstMs, 2) + " ms", T("hud-v-prof-slow", N(s.SlowFrames), N(ModProfiler.SlowMs)), worst),
            2 => ui.Stat(x, y, w, T("hud-v-prof-alloc"), N(s.KbPerFrame, 1) + " KB", T("hud-v-prof-alloc-share", N(100 * s.AllocShare, 1))),
            _ => ui.Stat(x, y, w, T("hud-v-prof-draws"), N(s.DrawsPerFrame, 1), T("hud-v-prof-per-frame"))
        });
        ui.Label(T("hud-v-prof-graph", N(s.Frames)));
        var series = s.Series;
        var points = Math.Min(series.Length, HudCanvas.GraphFrames);
        if (points > 1)
        {
            var bucket = (double)series.Length / points;
            ui.Graph(ui.Px(56), points, i => BucketMax(series, (int)(i * bucket), (int)((i + 1) * bucket)), false,
                Math.Max(0.25, s.WorstMs * 1.15), false);
        }

        ui.Space(3);
        ui.Line(T("hud-v-prof-gc", s.Collections.Gen0, s.Collections.Gen1, s.Collections.Gen2, N(s.PauseMs, 1)), HudUi.Dim, ui.F.Small);
    }

    // A stutter stays visible when 2000 frames draw as 240 points
    private static double BucketMax(float[] series, int from, int to)
    {
        if (!NotNull(series) || !Assert(from >= 0)) return double.NaN;
        double max = 0;
        for (var i = from; i < Math.Min(Math.Min(Math.Max(to, from + 1), series.Length), ModProfiler.MaxSeries); i++) max = Math.Max(max, series[i]);
        return max;
    }

    private void ProfileTable(HudUi ui, ModProfiler result)
    {
        if (!NotNull(ui) || !NotNull(result)) return;
        ui.Space(6);
        _ = ui.Chips(ui.X, ui.Y, [(T("hud-v-prof-by-time"), _profileSort == "self", () => _profileSort = "self"),
            (T("hud-v-prof-by-peak"), _profileSort == "peak", () => _profileSort = "peak"),
            (T("hud-v-prof-by-alloc"), _profileSort == "alloc", () => _profileSort = "alloc"),
            (T("hud-v-prof-by-calls"), _profileSort == "calls", () => _profileSort = "calls")]);
        ui.Y += ui.ChipHeight + ui.Px(5);
        ProfileLine(ui, T("hud-v-function"), ["ms/F", T("hud-v-prof-peak"), "KB/F", T("hud-v-calls")], true);
        foreach (var row in result.Rows(ProfileShown, _profileSort).Bounded(ProfileShown))
        {
            var open = row.Name == _profileOpen;
            var top = ui.Y;
            ProfileLine(ui, row.Name, [N(row.MsPerFrame, 3), N(row.PeakMs, 2), N(row.KbPerFrame, 1), N(row.Calls)], false, open);
            if (open) ProfileDetail(ui, row);
            var name = row.Name;
            ui.Click(ui.X, top, ui.W, ui.Y - top, () => _profileOpen = _profileOpen == name ? "" : name);
        }
    }

    private static readonly double[] ProfileColumns = [50, 46, 46, 58]; // mockup px, right to left: calls, KB, peak, ms

    private static void ProfileLine(HudUi ui, string name, string[] values, bool head, bool open = false)
    {
        if (!NotNull(ui) || !NotNull(name) || !Assert(values.Length == 4)) return;
        var (h, f, c) = (head ? ui.F.SmallRow : ui.F.BodyRow, head ? ui.F.Small : ui.F.Body, head ? HudUi.Dim : (Rgba?)null);
        if (open) ui.Fill(ui.X - ui.Px(4), ui.Y, ui.W + ui.Px(8), h + ui.Px(2), Rgba.White(0.06), ui.Px(3));
        var right = ui.X + ui.W;
        for (var i = 0; i < 4; i++)
        {
            ui.TextRight(right, ui.Y, h, head ? f : ui.F.Body, values[3 - i], c);
            right -= ui.Px(ProfileColumns[3 - i]);
        }

        ui.Text(ui.X, ui.Y, h, f, ui.Fit(f, name, right - ui.X - ui.Px(8)), c);
        ui.Fill(ui.X, ui.Y + h, ui.W, Math.Max(1, ui.Px(1)), Rgba.White(0.05));
        ui.Y += h + ui.Px(2);
    }

    private static void ProfileDetail(HudUi ui, ModProfiler.ProfileRow row)
    {
        if (!NotNull(ui) || !Assert(row.Calls > 0)) return;
        ui.Fill(ui.X - ui.Px(4), ui.Y - ui.Px(2), ui.W + ui.Px(8), 2 * ui.F.SmallRow + ui.Px(6), Rgba.White(0.06), ui.Px(3));
        ui.Line(T("hud-v-prof-call", N(row.MeanUs, 1), N(row.MaxUs, 0), N(100 * row.MainShare)), HudUi.Soft, ui.F.Small);
        ui.Line(T("hud-v-prof-call2", N(row.BytesPerCall), N(row.DrawsPerFrame, 2), row.Type), HudUi.Dim, ui.F.Small);
        ui.Space(4);
    }

    private void ProfileHoldings(HudUi ui)
    {
        if (!NotNull(ui) || _profileHoldings.Length == 0) return;
        ui.Label(T("hud-v-prof-holdings"));
        foreach (var (field, kind, count) in _profileHoldings.Bounded(ModProfiler.MaxHoldings))
        {
            var value = T("hud-v-prof-elements", N(count));
            var kindW = ui.TextW(ui.F.Small, kind) + ui.Px(10);
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.BodyRow, ui.F.Body, value);
            var right = ui.X + ui.W - ui.TextW(ui.F.Body, value) - ui.Px(10);
            ui.TextRight(right, ui.Y, ui.F.BodyRow, ui.F.Small, kind, HudUi.Dim);
            ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, field, right - kindW - ui.X));
            ui.Y += ui.F.BodyRow;
        }

        ui.Space(4);
        ui.Line(T("hud-v-prof-holdings-sub"), HudUi.Dim, ui.F.Small);
    }

    private bool ModKeys(KeyEvent key, bool press)
    {
        if (!_modTyping || !NotNull(key) || !_window.Shows(HudWindow.Mods) || _modView != 0) return false;
        if (press)
        {
            if (char.IsControl(key.KeyChar) || _modQuery.Length >= MaxQuery) return true;
            (_modQuery, _modPage, _modSelected) = (_modQuery + key.KeyChar, 0, "");
            return true;
        }

        if (key.KeyCode == (int)GlKeys.BackSpace && _modQuery.Length > 0) _modQuery = _modQuery[..^1];
        else if (key.KeyCode is (int)GlKeys.Escape or (int)GlKeys.Enter or (int)GlKeys.KeypadEnter) _modTyping = false;
        return Assert(_modQuery.Length <= MaxQuery);
    }

    private static string RiskySample(HarmonyAudit audit) =>
        audit.Methods.FirstOrDefault(static m => m.Risk == PatchRisk.High) is { } m ? Describe(m.Name) + ": " + Owners(m) : "";
}
