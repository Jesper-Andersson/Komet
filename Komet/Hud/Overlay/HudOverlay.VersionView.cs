namespace Komet.Hud;

// Version page (no tab). The build decides the edition from modinfo.json's version and the configuration.
internal sealed partial class HudOverlay
{
    private static string? _editionStamp; // the overlay asks on every composition

    // Builds before the Edition stamp fall back to configuration and version suffix
    private static string Edition(string version)
    {
        var stamped = _editionStamp ??= Metadata(Self, "Edition");
        if (stamped is "release" or "pre" or "dev") return stamped;
        if (DebugBuild) return "dev";
        return NotNull(version) && version.Contains('-', StringComparison.Ordinal) ? "pre" : "release";
    }

    private (string Text, Rgba Fill, Rgba Color) EditionBadge()
    {
        var edition = Edition(_build.Version);
        var text = T("hud-v-edition-" + edition, _build.Version);
        var (fill, color) = edition switch
        {
            "release" => (HudUi.Ok with { A = 0.25 }, HudUi.Ok),
            "pre" => (HudUi.Warn with { A = 0.22 }, HudUi.Warn),
            _ => (HudUi.Accent, Rgba.White(1))
        };
        return Assert(text.Length > 0) ? (text, fill, color) : ("Komet", fill, color);
    }

    private (string Text, Rgba Fill, Rgba Color)? CheckBadge()
    {
        if (_update is not { } check) return null;
        var r = check.Report;
        (string Key, Rgba Color)? badge = r.State switch
        {
            UpdateState.Verified => ("hud-v-check-verified", HudUi.Ok),
            UpdateState.Mismatch => ("hud-v-check-mismatch", HudUi.Err),
            UpdateState.Outdated => ("hud-v-check-outdated", HudUi.Warn),
            UpdateState.Checking => ("hud-v-check-running", HudUi.Dim),
            UpdateState.Unverified or UpdateState.NoRelease => ("hud-v-check-none", HudUi.Dim),
            _ => ("hud-v-check-failed", HudUi.Dim)
        };
        if (badge is not { } b || !Assert(b.Key.Length > 0)) return null;
        return (T(b.Key, r.Newest), b.Color with { A = 0.22 }, b.Color);
    }

    private void VersionView(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (text, fill, color) = EditionBadge();
        ui.Space(8);
        ui.Text(ui.X, ui.Y, ui.F.HugeRow, ui.F.Huge, "Komet " + _build.Version);
        var w = ui.TextW(ui.F.Small, text) + ui.Px(12);
        ui.Fill(ui.X + ui.W - w, ui.Y + (ui.F.HugeRow - ui.F.SmallRow - ui.Px(4)) / 2, w, ui.F.SmallRow + ui.Px(4), fill, ui.Px(3));
        ui.Text(ui.X + ui.W - w + ui.Px(6), ui.Y + (ui.F.HugeRow - ui.F.SmallRow) / 2, ui.F.SmallRow, ui.F.Small, text, color);
        ui.Y += ui.F.HugeRow;
        ui.Line(T("hud-v-edition-about-" + Edition(_build.Version)), HudUi.Dim, ui.F.Small);
        var built = HudText.LocalTime(Metadata(Self, "Built"));
        ui.Space(6);
        ui.Grid(3, 3, 5, (i, x, y, cw) => i switch
        {
            0 => ui.Stat(x, y, cw, T("hud-v-ver-commit"), _build.Commit.Length > 0 ? _build.Commit : "–"),
            1 => ui.Stat(x, y, cw, T("hud-v-ver-built"), built.Length > 0 ? built : T("hud-v-ver-local")),
            _ => ui.Stat(x, y, cw, T("hud-v-ver-file"), Path.GetFileName(_build.SourcePath) is { Length: > 0 } file ? file : "–")
        });
        CheckSection(ui);
    }

    private void CheckSection(HudUi ui)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        ui.Header(T("hud-v-ver-check"));
        if (_update is not { } check)
        {
            ui.Wrap(T("hud-v-ver-check-off"), HudUi.Dim, ui.F.Small);
            ui.Space(6);
            _ = ui.Go(ui.X, ui.Y, T("hud-v-ver-check-now"), RunUpdateCheck);
            ui.Y += ui.GoHeight;
            return;
        }

        var r = check.Report;
        var (notice, noticeColor) = r.Notice();
        var dot = ui.Dot(ui.X, ui.Y, ui.F.BodyRow, noticeColor ?? HudUi.Dim) + ui.Px(6);
        ui.Text(ui.X + dot, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, HudText.Translate(notice, r.Detail), ui.W - dot), noticeColor);
        ui.Y += ui.F.BodyRow + ui.Px(2);
        var none = HudText.Translate("verify-none");
        ui.Row(HudText.Translate("verify-build"), check.BuildTag, HudUi.Dim);
        ui.Row(HudText.Translate("verify-release"), r.Tag.Length > 0 ? r.Tag : none, HudUi.Dim);
        ui.Row(HudText.Translate("verify-released"), r.Released.Length > 0 ? r.Released : none, HudUi.Dim);
        ui.Row(HudText.Translate("verify-newest"), r.Newest.Length > 0 ? r.Newest : none, HudUi.Dim);
        Hashes(ui, r);
        var verdict = r.Verdict();
        var named = r.Tag.Length > 0 ? r.Tag : check.BuildTag;
        var verdictColor = r.State == UpdateState.Mismatch ? HudUi.Err : HudUi.Soft;
        if (r.Match) verdictColor = HudUi.Ok;
        ui.Space(4);
        ui.Wrap(HudText.Translate(verdict, r.State == UpdateState.Failed ? r.Detail : named), verdictColor, ui.F.Small);
        ui.Space(6);
        _ = ui.Go(ui.X, ui.Y, HudText.Translate("verify-recheck"), check.Start, enabled: r.State != UpdateState.Checking);
        ui.Y += ui.GoHeight;
    }

    private static void Hashes(HudUi ui, UpdateReport r)
    {
        if (!NotNull(ui) || !NotNull(r)) return;
        ui.Label(HudText.Translate("verify-sha256"));
        var cell = ui.TextW(ui.F.Mono, "0");
        var label = Math.Max(ui.TextW(ui.F.Small, HudText.Translate("verify-installed")), ui.TextW(ui.F.Small, HudText.Translate("verify-github")))
                    + ui.Px(10);
        foreach (var (key, hash, other) in (ReadOnlySpan<(string, string, string)>)[("verify-installed", r.Installed, r.Published),
                     ("verify-github", r.Published, r.Installed)])
        {
            ui.Text(ui.X, ui.Y, ui.F.MonoRow, ui.F.Small, HudText.Translate(key), HudUi.Dim);
            if (hash.Length != UpdateReport.HashLength || cell * hash.Length > ui.W - label)
                ui.Text(ui.X + label, ui.Y, ui.F.MonoRow, ui.F.Mono, hash.Length > 0 ? ui.Fit(ui.F.Mono, hash, ui.W - label) : "–", HudUi.Dim);
            else
                for (var i = 0; i < UpdateReport.HashLength; i++)
                {
                    Rgba? color = null;
                    if (other.Length == UpdateReport.HashLength) color = hash[i] == other[i] ? HudUi.Ok : HudUi.Err;
                    ui.Text(ui.X + label + i * cell, ui.Y, ui.F.MonoRow, ui.F.Mono, hash[i..(i + 1)], color);
                }

            ui.Y += ui.F.MonoRow;
        }
    }
}
