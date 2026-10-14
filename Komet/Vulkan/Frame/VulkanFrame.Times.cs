namespace Komet.Vulkan;

// Bottom-of-pipe timestamps: an interval is its earlier stamp's section, a setup's the transfers'. Rest: GL or idle.
internal sealed unsafe partial class VulkanFrame
{
    private const int QueriesPerSlot = 1024, MaxSections = 128, ReportSections = 14;
    private const int SetupEnds = -3, SetupBegins = -2, Closing = -1, Transferring = 0, Unnamed = 1;

    private static readonly List<string> SectionNames = ["transfers", "unnamed"];
    private static readonly Dictionary<string, int> SectionIds =
        new(StringComparer.Ordinal) { ["transfers"] = Transferring, ["unnamed"] = Unnamed };

    private readonly double[] _sectionNs = new double[MaxSections], _lastNs = new double[MaxSections];
    private ulong _stamps;
    private int _section = -1;
    private double _busyNs, _spanNs;
    private long _timed;

    private readonly record struct Stamp(uint Query, int Section, long Serial);

    public static bool Measuring { get; set; } = true;

    // Last timed frame (Slots frames back), for the HUD: a section's time and Vulkan's busy time; NaN before the first
    public double LastMs(int section) =>
        _lastBusyMs >= 0 && Index(section, MaxSections) && Assert(_lastNs[section] >= 0) ? _lastNs[section] / 1e6 : double.NaN;

    public double LastBusyMs => _lastBusyMs >= 0 ? _lastBusyMs : double.NaN;

    private double _lastBusyMs = -1;

    public static int SectionOf(string name)
    {
        if (!NotNull(name)) return Unnamed;
        if (SectionIds.TryGetValue(name, out var known)) return known;
        if (SectionNames.Count >= MaxSections) return Unnamed;
        var id = SectionNames.Count;
        SectionIds[name] = id;
        SectionNames.Add(name);
        return Assert(id < MaxSections) ? id : Unnamed;
    }

    public void Section(int section)
    {
        if (section == _section || !Index(section, MaxSections)) return;
        _section = section;
        if (_open is not null && Measuring) Stamped(section, posted: true);
    }

    private void TimingBegins(Segment segment)
    {
        if (!Measuring || _slot is not { } slot || !NotNull(segment) || (_stamps == 0 && !MadeStamps())) return;
        if (slot.Stamps.Count == 0)
            VkApi.CmdResetQueryPool(segment.Setup, _stamps, (uint)(slot.Index * QueriesPerSlot), QueriesPerSlot);
        Stamped(SetupBegins, posted: false, segment.Setup);
        Stamped(_section >= 0 ? _section : Unnamed, posted: true);
    }

    private void TimingEnds(Segment segment)
    {
        if (!Measuring || _stamps == 0 || _slot is not { Stamps.Count: > 0 } || !NotNull(segment)) return;
        Stamped(Closing, posted: true);
        Stamped(SetupEnds, posted: false, segment.Setup);
    }

    private void Stamped(int section, bool posted, IntPtr setup = 0)
    {
        if (_slot is not { } slot || _stamps == 0 || slot.Stamps.Count >= QueriesPerSlot || !Index(slot.Index, Slots)) return;
        var query = (uint)(slot.Index * QueriesPerSlot + slot.Stamps.Count);
        slot.Stamps.Add(new Stamp(query, section, Serial));
        if (posted) Post(new Op { Kind = OpKind.Timestamp, Pipeline = _stamps, Count = query, First = Vk.StageBottom });
        else if (Assert(setup != IntPtr.Zero)) VkApi.CmdWriteTimestamp(setup, Vk.StageBottom, _stamps, query);
    }

    private bool MadeStamps()
    {
        if (_device.TimestampPeriod <= 0 || !Assert(_stamps == 0)) return false;
        _stamps = _device.QueryPool(Vk.QueryTimestamp, QueriesPerSlot * Slots);
        return _stamps != 0 && Assert(_device.TimestampPeriod > 0);
    }

    // Once the slot's fence passed
    private void Timed(Slot slot, bool finished)
    {
        if (!NotNull(slot)) return;
        var count = slot.Stamps.Count;
        if (count == 0 || !Assert(count <= QueriesPerSlot)) return;
        var ticks = stackalloc ulong[count]; // not QueriesPerSlot: stackalloc zeroes it
        var read = finished && _stamps != 0 && VkApi.GetQueryPoolResults(_device.Handle, _stamps,
            (uint)(slot.Index * QueriesPerSlot), (uint)count, (nuint)(8 * count), ticks, 8, Vk.Result64) == Vk.Success;
        if (read) Summed(slot.Stamps, new ReadOnlySpan<ulong>(ticks, count), _device.TimestampPeriod);
        slot.Stamps.Clear();
    }

    private void Summed(List<Stamp> stamps, ReadOnlySpan<ulong> ticks, double period)
    {
        ulong first = ulong.MaxValue, last = 0, setup = 0, previous = 0;
        var busy = 0.0;
        Array.Clear(_lastNs);
        var open = Closing; // the section the last timestamp of a command buffer opened, Closing between them
        for (var i = 0; i < Math.Min(stamps.Count, QueriesPerSlot); i++)
        {
            var t = ticks[i];
            var section = stamps[i].Section;
            first = Math.Min(first, t);
            last = Math.Max(last, t);
            if (section == SetupBegins) setup = t;
            else if (section == SetupEnds) busy += Add(Transferring, t - setup, period);
            else
            {
                if (open >= 0 && t >= previous) busy += Add(open, t - previous, period);
                open = section;
                previous = t;
            }
        }

        if (last < first) return;
        (_busyNs, _spanNs, _lastBusyMs) = (_busyNs + busy, _spanNs + (last - first) * period, busy / 1e6);
        _timed++;
        _ = Assert(_timed > 0);
    }

    private double Add(int section, ulong ticks, double period)
    {
        if (!Index(section, MaxSections) || ticks > 1UL << 40) return 0;
        var ns = ticks * period;
        _sectionNs[section] += ns;
        _lastNs[section] += ns;
        return Assert(ns >= 0) ? ns : 0;
    }

    public string Times()
    {
        if (_timed == 0 || !Assert(SectionNames.Count <= MaxSections)) return "";
        var frames = (double)_timed;
        _ = Assert(frames > 0);
        var order = Enumerable.Range(0, SectionNames.Count).Where(s => _sectionNs[s] > 0)
            .OrderByDescending(s => _sectionNs[s]).Take(ReportSections);
        var sections = string.Join(", ", order.Select(s => $"{SectionNames[s]} {_sectionNs[s] / frames / 1e6:0.00}"));
        var line = $"; GPU per frame in ms ({_timed} frames): {sections}; Vulkan busy {_busyNs / frames / 1e6:0.00} of " +
                   $"{_spanNs / frames / 1e6:0.00} ms from the frame's first timestamp to its last" + StatisticsReport();
        Array.Clear(_sectionNs);
        (_busyNs, _spanNs, _timed) = (0, 0, 0);
        return line;
    }
}
