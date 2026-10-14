using System.Text;

namespace Komet.Hud;

// One HUD report section: rows read every interval, the bench averages them, the protocol quotes the means
internal sealed class HudPanel
{
    private const int MaxRows = 64, MaxLines = 4 * MaxRows;

    private readonly List<HudLine> _lines = [];
    private Action? _peaks; // the features' maxima, reset at the end of an interval in which the rows were read
    private bool _peaksRead;

    public HudPanel Title(string key, Func<string> badge) =>
        Assert(key.Length > 0) && NotNull(badge) ? Add(new HudLine { Kind = HudLineKind.Title, Label = HudText.Once("hud-" + key), Badge = badge }) : this;

    public HudPanel Bar(string key, Func<double> percent, Func<double>? value = null, string unit = "") => Assert(key.Length > 0) && NotNull(percent)
        ? Line(HudText.Once("hud-" + key), value, unit, percent)
        : this;

    public HudPanel Value(string key, Func<double> value, string unit = "", bool sub = false, bool final = false) => Assert(key.Length > 0) && NotNull(value)
        ? Line(HudText.Once("hud-" + key), value, unit, sub: sub, final: final)
        : this;

    public HudPanel Line(Func<string> label, Func<double>? value = null, string unit = "", Func<double>? percent = null, bool sub = false,
        bool final = false) =>
        Assert(unit.Length <= HudLine.MaxUnit) && NotNull(label)
            ? Add(new HudLine { Label = label, Value = value, Unit = unit, Percent = percent, Sub = sub, Final = final })
            : this;

    public HudPanel Section(string key, params object[] args)
    {
        if (!Assert(key.Length > 0) || !NotNull(args)) return this;
        return Add(new HudLine { Kind = HudLineKind.Header, Label = HudText.Once("hud-" + key, args) });
    }

    public HudPanel Warn(string key, Func<bool> when)
    {
        var text = HudText.Once("hud-" + key);
        return Assert(key.Length > 0) && NotNull(when) ? Line(() => when() ? text() : "", sub: true) : this;
    }

    public HudPanel Peaks(Action reset)
    {
        if (NotNull(reset) && Assert(_lines.Count > 0)) _peaks += reset;
        return this;
    }

    public HudPanel Rows(int count, Action<int> add)
    {
        if (!Assert(count > 0) || !Assert(count <= MaxRows)) return this;
        for (var i = 0; i < Math.Min(count, MaxRows); i++) add(i);
        return this;
    }

    private HudPanel Add(HudLine line)
    {
        if (Assert(_lines.Count < MaxLines) && NotNull(line)) _lines.Add(line);
        return this;
    }

    public void Accumulate()
    {
        _ = Assert(_lines.Count <= MaxLines);
        foreach (var line in _lines.Bounded(MaxLines)) line.Accumulate();
        _peaksRead = true;
    }

    public void ResetPeaks()
    {
        if (!_peaksRead || !Assert(_lines.Count > 0)) return;
        _peaksRead = false;
        _peaks?.Invoke();
    }

    public void ResetBench()
    {
        _ = Assert(_lines.Count <= MaxLines);
        foreach (var line in _lines.Bounded(MaxLines)) line.ResetBench();
    }

    public void Prime()
    {
        _ = Assert(_lines.Count <= MaxLines);
        foreach (var line in _lines.Bounded(MaxLines)) line.Prime();
    }

    public void AppendMeans(StringBuilder text)
    {
        if (!Assert(_lines.Count > 0) || !NotNull(text)) return;
        var first = true;
        foreach (var line in _lines.Bounded(MaxLines))
        {
            var row = line.MeanText();
            if (row.Length == 0) continue;
            if (!first) _ = text.Append('\n');
            else if (text.Length > 0) _ = text.Append("\n\n");
            _ = text.Append(row);
            first = false;
        }
    }
}
