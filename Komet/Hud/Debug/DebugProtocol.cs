using Komet.Diagnostics;

namespace Komet.Hud;

// Plain-text report of everything Komet measures, in sections findings point into; built on a pool thread.
internal static partial class DebugProtocol
{
    public const int MaxFindings = 16, MaxRows = 512;

    // A finding refers to its section by index + 1
    internal static readonly string[] Sections =
    [
        "hud-dbg-s-findings", "hud-dbg-s-capture", "hud-dbg-s-system", "hud-dbg-s-game", "hud-dbg-s-komet", "hud-dbg-s-frames",
        "hud-dbg-s-spikes", "hud-dbg-s-memory", "hud-dbg-s-runtime", "hud-dbg-s-world", "hud-dbg-s-render", "hud-dbg-s-mods",
        "hud-dbg-s-harmony", "hud-dbg-s-log", "hud-dbg-s-hud", "hud-dbg-s-compare", "hud-dbg-s-files"
    ];

    internal enum Severity { High, Medium, Low, Info }

    internal readonly record struct Finding(Severity Level, string Where, string Key, object[] Args);

    public static string Build(DebugInput input, DebugExtra extra, DebugSummary summary, string[] files)
    {
        if (!NotNull(input) || !NotNull(extra) || !NotNull(files)) return "";
        var text = new DebugText(input.Code);
        Head(text, input, summary);
        var findings = Findings(input, extra, summary);
        Section(text, "hud-dbg-s-findings");
        foreach (var finding in findings.Bounded(MaxFindings))
            _ = text.Line($"{text.T("hud-dbg-level-" + finding.Level.ToString().ToLowerInvariant()),-7} " +
                          $"{text.T(finding.Key, finding.Args)}  > {Number(finding.Where)}");
        Capture(text, input, summary);
        Environment(text, input);
        Komet(text, input);
        Frames(text, input, summary);
        Spikes(text, input);
        Memory(text, input, summary);
        Rows(text, "hud-dbg-s-runtime", input.Runtime);
        World(text, input);
        Rows(text, "hud-dbg-s-render", input.Render);
        Mods(text, input);
        Harmony(text, extra.Audit);
        Log(text, extra);
        Section(text, "hud-dbg-s-hud");
        _ = text.Line(input.HudMeans.Length > 0 ? input.HudMeans.Replace("\n", "\n  ", StringComparison.Ordinal) : text.T("hud-dbg-none"));
        Compare(text, extra, input, summary);
        Section(text, "hud-dbg-s-files");
        foreach (var file in files.Bounded(MaxRows)) _ = text.Line(file);
        Extra(text, input);
        return Anonymize(text.ToString());
    }

    internal static int Number(string section) =>
        Array.IndexOf(Sections, section) is var at && Index(at, Sections.Length) && Assert(section.Length > 0) ? at + 1 : 0;

    private static void Section(DebugText text, string key)
    {
        if (!NotNull(text) || !Assert(Number(key) > 0)) return;
        _ = text.Section(Number(key), key);
    }

    private static void Rows(DebugText text, string section, (string Key, string Value)[] rows)
    {
        if (!NotNull(rows) || !Assert(rows.Length <= MaxRows)) return;
        Section(text, section);
        foreach (var (key, value) in rows.Bounded(MaxRows)) _ = text.Row(key, value);
    }

    private static void Head(DebugText text, DebugInput input, DebugSummary summary)
    {
        if (!NotNull(input) || !Assert(input.Komet.Length > 0)) return;
        _ = text.Head(text.T("hud-dbg-title"))
            .Head($"Komet {input.Komet} | Vintage Story {GameVersion.OverallVersion} | {input.Started:yyyy-MM-dd HH:mm:ss zzz}")
            .Head(text.T("hud-dbg-head", input.Mode, DebugText.Num(summary.Seconds, "F1"), summary.Frames,
                DebugText.Num(summary.Fps, "F1"), DebugText.Num(summary.Low1Fps, "F1"), DebugText.Num(summary.WorstMs, "F1")));
        if (input.Trigger.Length > 0) _ = text.Head(input.Trigger);
    }

    private static void Capture(DebugText text, DebugInput input, DebugSummary summary)
    {
        if (!NotNull(input) || !Assert(summary.Frames >= 0)) return;
        Section(text, "hud-dbg-s-capture");
        _ = text.Row("hud-dbg-mode", input.Mode).Row("hud-dbg-trigger", input.Trigger)
            .Row("hud-dbg-duration", summary.Seconds, "F1", "s").Row("hud-dbg-frames", DebugText.Int(summary.Frames))
            .Row("hud-dbg-steady", $"{summary.Steady} ({DebugText.Num(Share(summary.Steady, summary.Frames), "F0")} %)")
            .Row("hud-dbg-spikes-kept", DebugText.Int(input.Spikes.Length))
            .Row("hud-dbg-language", input.Code);
    }

    internal static double Share(double part, double whole) =>
        Assert(part >= 0 || double.IsNaN(part)) && whole > 0 ? 100 * part / whole : double.NaN;

    internal static string Anonymize(string text)
    {
        if (!NotNull(text) || text.Length == 0) return "";
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) text = text.Replace(home, "~", StringComparison.Ordinal);
        var user = System.Environment.UserName;
        if (user.Length < 3) return text;
        return Assert(text.Length > 0) ? text.Replace("/" + user + "/", "/<user>/", StringComparison.Ordinal)
            .Replace("\\" + user + "\\", "\\<user>\\", StringComparison.Ordinal) : text;
    }

    private static void Extra(DebugText text, DebugInput input)
    {
        if (!NotNull(input.Extra) || !Assert(input.Extra.Length <= MaxRows)) return;
        var number = Sections.Length;
        foreach (var (title, body) in input.Extra.Bounded(MaxRows))
        {
            _ = text.Blank().Head($"[{++number}] {title.ToUpperInvariant()} " + new string('=', Math.Max(4, DebugText.MaxWidth - title.Length - 6)));
            _ = text.Line(body.Replace("\n", "\n  ", StringComparison.Ordinal));
        }
    }
}
