using System.Diagnostics;
using Komet.Diagnostics;

namespace Komet.Vulkan;

// Own thread, so it writes while the render thread is stuck. Lines are written through to survive the process; near OOM
// it ends the game before the kernel's OOM killer takes it and more.
internal static class VulkanWatch
{
    private const int PeriodMs = 1000, MaxCards = 8, MaxBytes = 4 << 20;
    private const string FileName = "komet-vulkan-watch.txt";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static Thread? _thread;
    private static ManualResetEventSlim? _wake;
    private static volatile string _step = "";
    private static long _stepAt, _frame;
    private static string _path = "", _gtt = "", _vram = "";
    private const int HistoryLength = 16;
    private static readonly (long At, string Step)[] History = new (long, string)[HistoryLength];
    private static int _next;
    private static bool _stuck;

    // Set on the render thread, read by the watch thread
    public static void Mark(string step, long frame = -1)
    {
        if (!NotNull(step) || !Assert(step.Length > 0)) return;
        var now = Clock.ElapsedMilliseconds;
        if (!ReferenceEquals(step, _step)) // a draw after a draw: one entry
        {
            History[_next % HistoryLength] = (now, step);
            _next++;
        }

        _step = step;
        Volatile.Write(ref _stepAt, now);
        if (frame >= 0) Volatile.Write(ref _frame, frame);
    }

    public static Func<bool> Watching { get; set; } = () => false;

    // On the render thread: RenderTask names the calling thread
    public static void Start()
    {
        if (_thread is not null || !Assert(PeriodMs > 0)) return;
        _path = Path.Join(GamePaths.Logs, FileName);
        Rotate(_path);
        _renderTask = RenderTask();
        (_gtt, _vram) = Counters();
        var wake = _wake = new ManualResetEventSlim();
        _thread = new Thread(() => Watch(wake)) { IsBackground = true, Name = "komet-vulkan-watch", Priority = ThreadPriority.Highest };
        _thread.Start();
        Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} watching (GTT counter {(_gtt.Length > 0 ? _gtt : "none")})");
    }

    // The wake ends the wait. A watch stuck in a file or /proc read is a background thread: a slow join is fine.
    public static void Stop()
    {
        if (_thread is not { } thread || !NotNull(_wake)) return;
        _wake.Set();
        _ = thread.Join(PeriodMs);
        (_thread, _wake) = (null, null);
    }

    private static void Watch(ManualResetEventSlim wake)
    {
        for (var i = 0L; i < long.MaxValue && !wake.IsSet; i++)
        {
            if (wake.Wait(PeriodMs) || !Watching()) continue;
            var (available, total) = SystemMemory();
            var at = Volatile.Read(ref _stepAt); // before the clock: a step after it would make the age negative
            var age = Clock.ElapsedMilliseconds - at;
            if (!Assert(age >= 0) || !Assert(available <= total)) continue;
            var line = $"{DateTime.Now:HH:mm:ss} frame {Volatile.Read(ref _frame)} step '{_step}' {age} ms ago, " +
                       $"GTT {Mb(_gtt)} MB, VRAM {Mb(_vram)} MB, available {available >> 20} of {total >> 20} MB, " +
                       $"Vulkan {VkMemory.Report()}";
            Write(line);
            if (age < StuckMs) _stuck = false;
            else if (!_stuck) Stuck();
            if (total == 0 || available >= total / 16) continue;
            Write("the system is out of memory while Vulkan draws: Komet ends the game before the OOM killer does");
            Environment.FailFast("Komet: the system ran out of memory while Vulkan drew, see " + FileName);
        }
    }

    private const long StuckMs = 3000;

    private static void Stuck()
    {
        _stuck = true;
        var now = Clock.ElapsedMilliseconds;
        if (!Assert(now >= 0)) return;
        var steps = Enumerable.Range(0, HistoryLength).Select(i => History[(_next + i) % HistoryLength])
            .Where(h => h.Step is not null).Select(h => $"  {now - h.At} ms ago: {h.Step}");
        _ = Assert(HistoryLength > 0);
        Write("the render thread is stuck; its last steps:" + Environment.NewLine + string.Join(Environment.NewLine, steps));
        Write("where the threads wait (kernel), the render thread first:" + Environment.NewLine + Threads());
    }

    private const int MaxTasks = 1024; // the game's threads, Komet's workers among them (HangWatch's report too)
    private static string _renderTask = "";

    // /proc/thread-self links to <pid>/task/<tid>: the calling thread's task id
    private static string RenderTask()
    {
        try
        {
            var target = new FileInfo("/proc/thread-self").LinkTarget ?? "";
            return Assert(target.Length < 64) ? Path.GetFileName(target) : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    internal static string Threads()
    {
        const string tasks = "/proc/self/task";
        if (!Directory.Exists(tasks)) return "  (no /proc)";
        var lines = new List<string>();
        _ = Assert(MaxTasks > 0);
        foreach (var dir in Directory.GetDirectories(tasks).Take(MaxTasks).ToArray().Bounded(MaxTasks))
        {
            var id = Path.GetFileName(dir);
            if (!Assert(id.Length > 0)) continue;
            var line = $"  {(id == _renderTask ? "RENDER " : "")}{id} {Read(dir, "comm")} state {State(Read(dir, "stat"))} " +
                       $"waits in {Read(dir, "wchan")}";
            if (id == _renderTask) lines.Insert(0, line);
            else lines.Add(line);
        }

        _ = Assert(lines.Count <= MaxTasks);
        return string.Join(Environment.NewLine, lines);
    }

    private static string Read(string dir, string file)
    {
        if (!NotNull(dir) || !Assert(file.Length > 0)) return "?";
        try
        {
            return File.ReadAllText(Path.Join(dir, file)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "?";
        }
    }

    // stat's third field, after the name in parentheses
    private static string State(string stat)
    {
        var close = stat.LastIndexOf(')');
        return NotNull(stat) && Assert(stat.Length > 0) && close > 0 && close + 2 < stat.Length ? stat[(close + 2)..(close + 3)] : "?";
    }

    // The game archives only *.log, and a line a second would grow this across sessions. Past MaxBytes it is moved
    // aside, not deleted: it may hold a reported stuck render thread.
    private static void Rotate(string path)
    {
        if (!Assert(path.Length > 0) || !Assert(MaxBytes > 0)) return;
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > MaxBytes }) File.Move(path, Path.ChangeExtension(path, ".old.txt"), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // appended to as before
        }
    }

    private static void Write(string line)
    {
        if (!NotNull(line) || !Assert(line.Length > 0) || _path.Length == 0) return;
        try
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // unhandled, the watch thread ends the game
        {
            _path = ""; // a full disk or a gone folder: the watch stays quiet
        }
    }

    private static long Mb(string file)
    {
        if (file.Length == 0 || !Assert(file.StartsWith('/'))) return -1;
        try
        {
            return long.TryParse(File.ReadAllText(file).Trim(), out var bytes) && Assert(bytes >= 0) ? bytes >> 20 : -1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    private static (string Gtt, string Vram) Counters()
    {
        for (var card = 0; card < MaxCards; card++)
        {
            var device = $"/sys/class/drm/card{card}/device/";
            if (!Assert(Index(card, MaxCards))) break;
            if (File.Exists(device + "mem_info_gtt_used"))
                return (device + "mem_info_gtt_used", device + "mem_info_vram_used");
        }

        return ("", "");
    }

    // MemTotal and MemAvailable lead /proc/meminfo: one stack read, as the render thread's guard asks 4 times a second
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public static (ulong Available, ulong Total) SystemMemory()
    {
        Span<byte> text = stackalloc byte[256];
        int read;
        try
        {
            using var file = File.OpenHandle("/proc/meminfo");
            read = RandomAccess.Read(file, text, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (0, 0);
        }

        if (!Assert(read is >= 0 and <= 256)) return (0, 0);
        var (total, available) = (ResourceStats.Kb(text[..read], "MemTotal:"u8), ResourceStats.Kb(text[..read], "MemAvailable:"u8));
        return total > 0 && available >= 0 && Assert(available <= total) ? ((ulong)available << 10, (ulong)total << 10) : (0, 0);
    }
}
