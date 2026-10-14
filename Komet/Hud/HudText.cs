using System.Globalization;

namespace Komet.Hud;

// Numbers are metric and culture-invariant.
internal static class HudText
{
    private const int MaxCached = 256;
    private static readonly Dictionary<(string Prefix, string Name), string> Cache = [];
    private static string? _locale;

    // NaN (not measured) and infinity (empty window) print as nothing.
    public static string Format(double value, string format, string none = "") =>
        double.IsFinite(value) && Assert(format.Length is 2 or 3)
            ? value.ToString(format, CultureInfo.InvariantCulture)
            : none;

    public static string Num(double value, int decimals = 0)
    {
        if (!double.IsFinite(value) || !Assert(decimals is >= 0 and <= 4)) return "–";
        var culture = Lang.CurrentLocale is { } locale && locale.StartsWith("de", StringComparison.Ordinal) ? German : CultureInfo.InvariantCulture;
        return value.ToString(Grouped[decimals], culture);
    }

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly string[] Grouped = ["N0", "N1", "N2", "N3", "N4"];

    // Lang.Get hands the key back when the translation is missing
    public static string Translate(string key, params object[] args)
    {
        var full = "komet:" + key;
        var text = Lang.Get(full, args);
        return Assert(key.Length > 0) && Assert(text != full) ? text : key;
    }

    // Lang.CurrentLocale is another string after a change of language
    public static Func<string> Once(string key, params object[] args)
    {
        if (!Assert(key.Length > 0) || !NotNull(args)) return static () => "";
        string? locale = null, text = null;
        return () =>
        {
            if (text is null || !ReferenceEquals(locale, Lang.CurrentLocale))
                (locale, text) = (Lang.CurrentLocale, Translate(key, args));
            return text;
        };
    }

    public static string Cached(string prefix, string name)
    {
        if (!ReferenceEquals(_locale, Lang.CurrentLocale) || Cache.Count >= MaxCached)
        {
            Cache.Clear();
            _locale = Lang.CurrentLocale;
        }

        if (!Cache.TryGetValue((prefix, name), out var text))
            Cache[(prefix, name)] = text = Translate(prefix + name.TrimStart('~'));
        return Assert(Cache.Count <= MaxCached) ? text : "";
    }

    public static string LocalTime(string iso)
    {
        if (iso.Length == 0 || !Assert(iso.Length <= 40)) return "";
        var format = Translate("time-format");
        var ok = DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var time);
        return ok && Assert(time.Year > 2000) && Assert(format.Length > 0)
            ? time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture)
            : "";
    }
}
