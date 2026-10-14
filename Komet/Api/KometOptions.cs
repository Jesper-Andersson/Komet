using System.Globalization;
using Komet.Hud;

namespace Komet.Api;

// Komet's options window (replaces the game's graphics settings). Mods add pages in StartClientSide from an own class
// called only when IsModEnabled("komet"): Page(), then rows in order (README). Getters, setters, saving and translation
// are the mod's. Dropped when the world closes. Main thread only.
public static class KometOptions
{
    public const int MaxPages = 32;
    private static readonly List<OptionPage> Registered = [];

    internal static int Count => Registered.Count;

    // After Apply/Done, for acting on several options at once; a throwing handler is logged once and unsubscribed
    public static event EventHandler? Applied;

    // New, empty page; reusing an id starts it over (no doubled rows on rejoin). section: sidebar group, default title.
    public static OptionPage Page(string id, string title, string? section = null)
    {
        if (!NotNull(id) || !NotNull(title) || !Assert(id.Length > 0) || !Assert(title.Length > 0))
            return new OptionPage("invalid", "?", "?");
        var page = new OptionPage(id, title, section ?? title);
        var at = Registered.FindIndex(p => p.Id == id);
        if (at >= 0) Registered[at] = page;
        else if (Assert(Registered.Count < MaxPages)) Registered.Add(page);
        return page;
    }

    internal static OptionPage At(int index) =>
        Index(index, Registered.Count) ? Registered[index] : new OptionPage("invalid", "?", "?");

    internal static void RaiseApplied()
    {
        if (!Assert(Registered.Count <= MaxPages)) return;
        ApiEvents.Raise(Applied, h => h(null, EventArgs.Empty), h => Applied -= h, "KometOptions.Applied");
    }

    internal static void Clear()
    {
        Registered.Clear();
        Applied = null;
        _ = Assert(Registered.Count == 0);
    }
}

public enum OptionKind
{
    Group,
    Switch,
    Slider,
    Choice,
    Button,
    Key // one of the game's hotkeys, only on Komet's controls page
}

// One row of a page. Values travel as doubles: a switch is 0 or 1, a choice its index
public sealed class OptionRow
{
    internal OptionRow(OptionKind kind, string label, string? hint)
    {
        (Kind, Label, Hint) = (kind, label, hint);
        _ = NotNull(label);
    }

    public OptionKind Kind { get; }
    public string Label { get; }
    public string? Hint { get; }
    internal Func<double> Get { get; init; } = static () => 0;
    internal Action<double> Set { get; init; } = static _ => { };
    internal double Min { get; init; }
    internal double Max { get; init; } = 1;
    internal double Step { get; init; } = 1;
    internal string Unit { get; init; } = "";
    internal string[] Names { get; init; } = [];
    internal Func<string>? Shows { get; init; } // what a key's row or a button shows, asked each time it is drawn
    internal string Code { get; init; } = ""; // a key's hotkey code
    internal Func<bool>? Warn { get; init; } // a key bound like another: its row says so in the warning colour
    internal System.Func<double, string>? Format { get; set; }
    internal Func<bool>? Enabled { get; set; }
    internal Func<string?>? Locked { get; set; } // why the row takes no input, shown above its hint; null: it does
    internal double Default { get; set; } = double.NaN; // a slider's shipped value, marked on its track and taken back by Delete

    internal bool IsEnabled => (Enabled?.Invoke() ?? true) && Locked?.Invoke() is null;

    internal string ValueText(double value) => Finite(value) && Assert(Unit.Length < 64)
        ? Format?.Invoke(value) ?? value.ToString("0.##", CultureInfo.InvariantCulture) + Unit
        : "";

    internal double At(double fraction)
    {
        if (!Assert(Max > Min) || !Assert(Step > 0)) return Min;
        var value = Min + Math.Round(Math.Clamp(fraction, 0, 1) * (Max - Min) / Step) * Step;
        return Math.Round(Math.Clamp(value, Min, Max), 6);
    }
}

public sealed class OptionPage
{
    public const int MaxOptions = 40;
    internal const int MaxRows = 256; // Komet's own long pages (the controls)
    private readonly List<OptionRow> _options = [];

    internal OptionPage(string id, string title, string section)
    {
        (Id, Title, Section) = (id, title, section);
        _ = Assert(id.Length > 0 && section.Length > 0);
    }

    public string Id { get; }
    public string Title { get; }
    public string Section { get; }
    internal int Count => _options.Count;
    internal int Capacity { get; init; } = MaxOptions;

    internal OptionRow this[int index] =>
        Index(index, _options.Count) ? _options[index] : new OptionRow(OptionKind.Group, "?", null);

    public OptionPage Group(string title) =>
        NotNull(title) && Assert(title.Length > 0) ? Add(new OptionRow(OptionKind.Group, title, null)) : this;

    public OptionPage Switch(string label, Func<bool> get, Action<bool> set, string? hint = null) =>
        NotNull(label) && NotNull(get) && NotNull(set)
            ? Add(new OptionRow(OptionKind.Switch, label, hint) { Get = () => get() ? 1 : 0, Set = v => set(v >= 0.5) })
            : this;

    public OptionPage Slider(string label, double min, double max, double step, Func<double> get, Action<double> set,
        string unit = "", string? hint = null) =>
        NotNull(get) && NotNull(set) && Assert(max > min) && Assert(step > 0)
            ? Add(new OptionRow(OptionKind.Slider, label, hint)
                { Get = get, Set = set, Min = min, Max = max, Step = step, Unit = unit ?? "" })
            : this;

    internal OptionPage Slider(string label, HudRange range, Func<double> get, Action<double> set) =>
        Assert(range.Max > range.Min) && Assert(range.Step > 0)
            ? Slider(label, range.Min, range.Max, range.Step, get, set).Format(range.Text)
            : this;

    public OptionPage Choice(string label, IReadOnlyList<string> names, Func<int> get, Action<int> set,
        string? hint = null) =>
        NotNull(names) && NotNull(get) && NotNull(set) && Assert(names.Count is > 0 and <= MaxOptions)
            ? Add(new OptionRow(OptionKind.Choice, label, hint)
                { Get = () => get(), Set = v => set((int)v), Max = names.Count - 1, Names = [.. names] })
            : this;

    public OptionPage Button(string label, string text, Action click, string? hint = null) =>
        NotNull(label) && NotNull(text) && NotNull(click)
            ? Add(new OptionRow(OptionKind.Button, label, hint) { Shows = () => text, Set = _ => click() })
            : this;

    // A button whose caption changes (a reset that asks first)
    internal OptionPage Button(string label, Func<string> text, Action click, string? hint = null) =>
        NotNull(label) && NotNull(text) && NotNull(click)
            ? Add(new OptionRow(OptionKind.Button, label, hint) { Shows = text, Set = _ => click() })
            : this;

    // A game hotkey by code: the row shows its binding, warn tells whether another key shares it
    internal OptionPage Key(string label, string code, Func<string> shows, Func<bool> warn, string? hint = null) =>
        NotNull(label) && NotNull(code) && NotNull(shows) && NotNull(warn) && Assert(code.Length > 0)
            ? Add(new OptionRow(OptionKind.Key, label, hint) { Code = code, Shows = shows, Warn = warn })
            : this;

    // How the option added last shows its value (a slider's "unlimited" at its end, say)
    public OptionPage Format(System.Func<double, string> format)
    {
        if (NotNull(format) && Assert(_options.Count > 0)) _options[^1].Format = format;
        return this;
    }

    // The option added last takes input only while enabled() holds; otherwise it is drawn dimmed
    public OptionPage EnabledWhen(Func<bool> enabled)
    {
        if (NotNull(enabled) && Assert(_options.Count > 0)) _options[^1].Enabled = enabled;
        return this;
    }

    // The slider added last marks its shipped value; Delete and its ↺ take it back
    internal OptionPage Standard(double value)
    {
        if (Finite(value) && Assert(_options.Count > 0)) _options[^1].Default = value;
        return this;
    }

    // Last option locked while locked() gives a reason (held feature knob): dimmed, reason above its hint
    internal OptionPage LockedWhen(Func<string?> locked)
    {
        if (NotNull(locked) && Assert(_options.Count > 0)) _options[^1].Locked = locked;
        return this;
    }

    // The page with other mods' feature rows added (Features.Placed): a copy, so the page stays as its mod built it
    internal OptionPage Copy()
    {
        var copy = new OptionPage(Id, Title, Section) { Capacity = MaxRows };
        copy._options.AddRange(_options);
        return Assert(copy.Count == Count) && Assert(Count <= MaxRows) ? copy : this;
    }

    private OptionPage Add(OptionRow option)
    {
        if (NotNull(option.Label) && Assert(option.Label.Length > 0) &&
            Assert(_options.Count < Math.Min(Capacity, MaxRows))) _options.Add(option);
        return this;
    }
}
