using Komet.Core;

namespace Komet.Hud;

internal enum HudLineKind
{
    Text,
    Header,
    Title
}

// One HUD report row; the bench means and the protocol's HUD section read every row each interval
internal sealed class HudLine
{
    public const int MaxUnit = 2;

    private double _valueSum, _percentSum;
    private bool _fresh;

    private double _read = double.NaN, _readPercent = double.NaN;

    // A share can be missing where its time is not
    private int _samples, _percentSamples;

    public HudLineKind Kind { get; init; } = HudLineKind.Text;
    public Func<string> Label { get; init; } = static () => "";
    public bool Sub { get; init; }
    public Func<double>? Value { get; init; } // NaN = nothing measured
    public bool Final { get; init; } // already covers the whole bench, so the report takes it as it stands
    public string Unit { get; init; } = "";
    public Func<double>? Percent { get; init; } // 0..100
    public Func<string>? Badge { get; init; } // a title's

    private string NumberFormat => Unit switch { "ms" => "F2", "/s" => "F1", _ => "N0" };

    public void Read()
    {
        (_read, _readPercent, _fresh) = (Value?.Invoke() ?? double.NaN, Percent?.Invoke() ?? double.NaN, true);
        _ = Assert(Kind == HudLineKind.Text || (Value == null && Percent == null)); // only text rows carry numbers
    }

    public void Prime()
    {
        Read();
        _fresh = false;
        _ = Assert(!_fresh);
    }

    // No window yet is not a sample; value and share average over their own windows.
    public void Accumulate()
    {
        if (!Assert(_samples >= 0) || !Assert(_percentSamples >= 0)) return;
        if (!_fresh) Read();
        _fresh = false;
        if (double.IsFinite(_read)) (_valueSum, _samples) = (_valueSum + _read, _samples + 1);
        if (double.IsFinite(_readPercent))
            (_percentSum, _percentSamples) = (_percentSum + _readPercent, _percentSamples + 1);
    }

    public void ResetBench()
    {
        _ = Assert(_samples >= 0) && Assert(_percentSamples >= 0);
        (_valueSum, _percentSum, _samples, _percentSamples, _fresh) = (0, 0, 0, 0, false);
    }

    // Label read once: a log row changes on a pool thread.
    public string MeanText()
    {
        if (!Assert(_samples >= 0) || !Assert(_percentSamples >= 0)) return "";
        var label = Label();
        if (Kind != HudLineKind.Text) return Text(label, null, null);
        double mean = _samples > 0 ? _valueSum / _samples : double.NaN,
            share = _percentSamples > 0 ? _percentSum / _percentSamples : double.NaN;
        if (Final) (mean, share) = (Value?.Invoke() ?? double.NaN, Percent?.Invoke() ?? double.NaN);
        return Text(label, Value is null ? null : HudText.Format(mean, NumberFormat),
            Percent is null ? null : HudText.Format(share, "F1"));
    }

    private string Text(string label, string? value, string? percent) => Kind switch
    {
        HudLineKind.Header => $"[{label}]",
        HudLineKind.Title => Titled(label),
        HudLineKind.Text when label.Length > 0 => RowText(label, Sub, value, percent, Unit),
        _ => ""
    };

    private string Titled(string label) => Badge is { } badge && NotNull(label) ? $"{label} – {badge()}" : label;

    // "label: 12.5 % 3.20 ms"; null: no such column, "": measured nothing; a row with no number at all is left out.
    internal static string RowText(string label, bool sub, string? value, string? percent, string unit)
    {
        if (!Assert(label.Length > 0) || !Assert(unit.Length <= MaxUnit)) return "";
        var text = (sub ? "  " : "") + label;
        if (value == null && percent == null) return text;
        bool hasValue = value is { Length: > 0 }, hasPercent = percent is { Length: > 0 };
        if (!hasValue && !hasPercent) return "";
        text += ":";
        if (hasPercent) text += $" {percent} %";
        if (hasValue) text += $" {value} {unit}";
        return text.TrimEnd();
    }
}

// Growth since this instance's last read; NaN on first read, across a counting pause (Counting.Epoch) or a fall (new
// world).
internal sealed class Growth(Func<double> total)
{
    private int _epoch = -1;
    private double _last = double.NaN;

    public double Next()
    {
        var now = total();
        var grown = _epoch == Counting.Epoch && now >= _last ? now - _last : double.NaN;
        (_last, _epoch) = (now, Counting.Epoch);
        return Finite(now) && Assert(_epoch >= 0) ? grown : double.NaN;
    }
}
