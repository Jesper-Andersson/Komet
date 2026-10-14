using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Komet.Diagnostics;

// glthread: "gl0" executes GL calls, "gdrv0" is the gallium driver, "cs0" submits (Mesa process:name, cut to 15 chars).
// A driver thread near a whole core is what Vulkan would lift.
internal static partial class RenderCost
{
    public const int Kinds = 4;
    private const int MaxThreads = 16, MaxTasks = 1024, MaxCommBytes = 128, RescanEvery = 40;

    private static readonly string[] KindKeys = ["main", "glthread", "gdrv", "cs"];
    private static readonly long[] KindNs = new long[Kinds];
    private static readonly bool[] KindSeen = new bool[Kinds];
    private static readonly List<(int Tid, int Kind, SafeFileHandle File, long Last)> Tracked = [];

    // Threads of no kind, not read again until the set is dropped every ForgetEvery scans (a thread named late is found then)
    private static readonly HashSet<int> Kindless = [];
    private const int ForgetEvery = 25, MaxKindless = 4096;
    private static int _untilScan, _scans, _main;

    public static string KindKey(int kind) =>
        Index(kind, Kinds) && Assert(KindKeys.Length == Kinds) ? KindKeys[kind] : "";

    // Unseen kinds show nothing rather than an idle zero
    public static long ThreadNs(int kind) => Index(kind, Kinds) && Assert(KindNs[kind] >= 0) ? KindNs[kind] : 0;

    public static bool Seen(int kind) => Index(kind, Kinds) && Assert(KindSeen.Length == Kinds) && KindSeen[kind];

    public static bool DriverThreads => KindSeen[1] || KindSeen[2] || KindSeen[3];

    private static void ClearThreads()
    {
        foreach (var thread in Tracked.Bounded(MaxThreads)) thread.File.Dispose();
        Tracked.Clear();
        Array.Clear(KindNs);
        Array.Clear(KindSeen);
        Kindless.Clear();
        (_untilScan, _scans, _main) = (0, 0, 0);
        _ = Assert(Tracked.Count == 0) && Assert(!DriverThreads);
    }

    private static void SampleThreads()
    {
        if (!OperatingSystem.IsLinux() || !Assert(Tracked.Count <= MaxThreads)) return;
        if (--_untilScan <= 0) Scan();
        for (var i = 0; i < Math.Min(Tracked.Count, MaxThreads); i++)
        {
            var (tid, kind, file, last) = Tracked[i];
            var now = SchedStat.Read(file, SchedStat.Run);
            if (now < 0) file.Dispose(); // the thread ended
            else if (Assert(now >= last)) KindNs[kind] += now - last;
            Tracked[i] = (tid, kind, file, now);
        }

        _ = Tracked.RemoveAll(static t => t.File.IsClosed);
    }

    private static void Scan()
    {
        _untilScan = RescanEvery;
        if (++_scans % ForgetEvery == 0 || Kindless.Count >= MaxKindless) Kindless.Clear();
        var onMain = Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId;
        if (onMain && _main == 0) _main = Gettid(); // the main thread's id holds for the process
        var main = onMain ? _main : -1;
        if (!Assert(main != 0)) return;
        try
        {
            foreach (var task in Directory.EnumerateDirectories("/proc/self/task").Bounded(MaxTasks))
            {
                if (!int.TryParse(Path.GetFileName(task), NumberStyles.None, CultureInfo.InvariantCulture, out var tid) ||
                    Tracking(tid) || Kindless.Contains(tid)) continue;
                var kind = tid == main ? 0 : Classify(File.ReadAllText(Path.Combine(task, "comm")));
                if (kind >= 0) Track(tid, kind, Path.Combine(task, "schedstat"));
                else _ = Kindless.Add(tid);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a thread that ended between the listing and the read
        }
    }

    private static bool Tracking(int tid)
    {
        _ = Assert(tid >= 0) && Assert(Tracked.Count <= MaxThreads);
        foreach (var thread in Tracked.Bounded(MaxThreads))
            if (thread.Tid == tid)
                return true;
        return false;
    }

    private static void Track(int tid, int kind, string path)
    {
        if (!Index(kind, Kinds) || Tracked.Count >= MaxThreads) return;
        var file = File.OpenHandle(path);
        var now = SchedStat.Read(file, SchedStat.Run);
        if (!Assert(tid > 0) || now < 0)
        {
            file.Dispose();
            return;
        }

        Tracked.Add((tid, kind, file, now)); // counted from here, not from the thread's start
        KindSeen[kind] = true;
    }

    // Mesa util_queue threads: name + index after the last ':'; -1 otherwise.
    internal static int Classify(string comm)
    {
        if (!NotNull(comm) || !Assert(comm.Length <= MaxCommBytes)) return -1;
        var name = comm.AsSpan(comm.LastIndexOf(':') + 1).TrimEnd();
        if (!comm.Contains(':', StringComparison.Ordinal) || name.Length < 2 || !char.IsAsciiDigit(name[^1])) return -1;
        var bare = name.TrimEnd("0123456789");
        return bare switch
        {
            "gl" => 1,
            "gdrv" => 2,
            "cs" => 3,
            _ => -1
        };
    }

    // /proc/thread-self links to /proc/<pid>/task/<tid>
    private static int Gettid()
    {
        var link = new DirectoryInfo("/proc/thread-self").LinkTarget;
        return NotNull(link) &&
               int.TryParse(Path.GetFileName(link), NumberStyles.None, CultureInfo.InvariantCulture, out var tid) &&
               Assert(tid > 0)
            ? tid
            : -1;
    }
}
