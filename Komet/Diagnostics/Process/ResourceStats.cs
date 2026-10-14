using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace Komet.Diagnostics;

internal sealed partial class ResourceStats
{
    private const long Mb = 1024 * 1024;
    private const int ProcBytes = 8192, MaxStatFields = 32;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private readonly byte[] _proc = new byte[ProcBytes]; // Sample runs on one thread at a time
    private bool _failed;
    private long _lastAllocated;
    private ulong _lastBusy, _lastTotal;
    private TimeSpan _lastCpu, _lastPause;
    private int _lastGen0, _lastGen2;

    public double CpuPercent { get; private set; }
    public long WorkingSetMb { get; private set; }
    public long ManagedUsedMb { get; private set; }
    public long ManagedCommittedMb { get; private set; }
    public long NativeMb { get; private set; }
    public double Gen0PerSec { get; private set; }
    public double Gen2PerSec { get; private set; }
    public double GcPausePercent { get; private set; } // wall time the collector stopped every thread
    public double AllocatedMbPerSec { get; private set; } // what drives that pause, all threads together

    public double SystemCpuPercent { get; private set; }
    public long TotalRamMb { get; private set; }
    public long AvailableRamMb { get; private set; }
    public long UsedRamMb => TotalRamMb - AvailableRamMb;

    public double PercentOfRam(long mb) => Assert(mb >= 0) && TotalRamMb > 0 ? 100.0 * mb / TotalRamMb : 0;

    // Rethrows once; values then freeze
    public void Sample()
    {
        if (_failed) return;
        try
        {
            Measure();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Win32Exception or NotSupportedException)
        {
            _failed = true;
            throw;
        }
    }

    private void Measure()
    {
        var seconds = _wall.Elapsed.TotalSeconds;
        _wall.Restart();
        if (!Assert(seconds > 0)) return;

        var cpu = Environment.CpuUsage.TotalTime; // getrusage, as Process.TotalProcessorTime reads it for this process
        if (Assert(cpu >= _lastCpu))
            CpuPercent = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
        _lastCpu = cpu;

        Gen0PerSec = Rate(GC.CollectionCount(0), ref _lastGen0, seconds);
        Gen2PerSec = Rate(GC.CollectionCount(2), ref _lastGen2, seconds);
        var pause = GC.GetTotalPauseDuration();
        if (Assert(pause >= _lastPause)) GcPausePercent = (pause - _lastPause).TotalSeconds / seconds * 100;
        _lastPause = pause;
        var allocated = GC.GetTotalAllocatedBytes();
        if (Assert(allocated >= _lastAllocated))
            AllocatedMbPerSec = (double)(allocated - _lastAllocated) / Mb / seconds;
        _lastAllocated = allocated;

        WorkingSetMb = WorkingSet();
        ManagedUsedMb = GC.GetTotalMemory(false) / Mb;
        ManagedCommittedMb = GC.GetGCMemoryInfo().TotalCommittedBytes / Mb;
        if (!Assert(WorkingSetMb > 0) || !Assert(ManagedCommittedMb > 0)) return;
        NativeMb = Math.Max(0, WorkingSetMb - ManagedCommittedMb);

        if (OperatingSystem.IsLinux()) SampleLinux();
        else if (OperatingSystem.IsWindows()) SampleWindows();
    }

    // Refresh() parses /proc/self/stat and status into ~27 KB of strings
    private long WorkingSet()
    {
        if (OperatingSystem.IsLinux()) return Kb(Proc("/proc/self/status"), "VmRSS:"u8) / 1024;
        _process.Refresh();
        return Assert(_process.WorkingSet64 >= 0) ? _process.WorkingSet64 / Mb : 0;
    }

    private static double Rate(int now, ref int last, double seconds)
    {
        var perSec = Assert(now >= last) && Assert(seconds > 0) ? (now - last) / seconds : 0;
        last = now;
        return perSec;
    }

    // Parsed in place: StreamReader, strings and Split cost ~17 KB a sample
    private void SampleLinux()
    {
        var meminfo = Proc("/proc/meminfo");
        if (Kb(meminfo, "MemTotal:"u8) is var ram and >= 0) TotalRamMb = ram / 1024;
        if (Kb(meminfo, "MemAvailable:"u8) is var free and >= 0) AvailableRamMb = free / 1024;
        if (!Assert(TotalRamMb > 0) || !Assert(AvailableRamMb <= TotalRamMb)) return;

        var stat = Proc("/proc/stat");
        var end = stat.IndexOf((byte)'\n');
        var line = end < 0 ? stat : stat[..end];
        if (!Assert(line.StartsWith("cpu "u8))) return;
        line = line[3..];
        var (total, idle, fields) = (0UL, 0UL, 1);
        for (var i = 1; i < MaxStatFields; i++)
        {
            line = line.TrimStart((byte)' ');
            if (line.IsEmpty) break;
            var cut = line.IndexOf((byte)' ');
            var field = cut < 0 ? line : line[..cut];
            if (!Assert(Utf8Parser.TryParse(field, out ulong value, out var used) && used == field.Length)) return;
            (total, fields) = (total + value, fields + 1);
            line = line[field.Length..];
            if (i is 4 or 5) idle += value; // idle and iowait
        }

        if (Assert(fields >= 8)) UpdateSystemCpu(total - idle, total);
    }

    private ReadOnlySpan<byte> Proc(string path)
    {
        using var file = File.OpenHandle(path);
        var got = 0;
        for (var i = 0; i < ProcBytes && got < ProcBytes; i++)
        {
            var n = RandomAccess.Read(file, _proc.AsSpan(got), got);
            if (n <= 0) break;
            got += n;
        }

        return Assert(got <= ProcBytes) ? _proc.AsSpan(0, got) : [];
    }

    // "MemTotal:       32768000 kB", "VmRSS:\t  102016 kB"; -1 without key
    internal static long Kb(ReadOnlySpan<byte> text, ReadOnlySpan<byte> key)
    {
        var at = text.IndexOf(key);
        if (at < 0 || (at > 0 && text[at - 1] != '\n')) return -1;
        var value = text[(at + key.Length)..].TrimStart(" \t"u8); // meminfo pads with spaces, status with a tab
        return Assert(Utf8Parser.TryParse(value, out long kb, out _)) && Assert(kb >= 0) ? kb : 0;
    }

    private void SampleWindows()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status) && Assert(status.ullTotalPhys > 0) &&
            Assert(status.ullAvailPhys <= status.ullTotalPhys))
            (TotalRamMb, AvailableRamMb) = ((long)(status.ullTotalPhys / Mb), (long)(status.ullAvailPhys / Mb));
        if (GetSystemTimes(out var idle, out var kernel, out var user) && Assert(kernel >= idle))
            UpdateSystemCpu(kernel + user - idle, kernel + user); // kernel includes idle
    }

    private void UpdateSystemCpu(ulong busy, ulong total)
    {
        if (!Assert(busy <= total)) return;
        if (Assert(total >= _lastTotal) && Assert(busy >= _lastBusy) && _lastTotal != 0 && total > _lastTotal)
            SystemCpuPercent = 100.0 * (busy - _lastBusy) / (total - _lastTotal);
        (_lastBusy, _lastTotal) = (busy, total);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;

        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual,
            ullAvailExtendedVirtual;
    }
}

// Any glGet* forces a glthread drain (~10 µs), so the frame's timer query is read once per HUD interval from the oldest
// of four: long complete, radeonsi answers without flushing.
internal sealed class GpuStats : IDisposable
{
    private const int Ring = 4, MaxExtensions = 4096;

    // DEDICATED is the card's VRAM; TOTAL_AVAILABLE adds GTT on Mesa (21 GB "used" on 16 GB). CURRENT_AVAILABLE:
    // device-free.
    private const GetPName Dedicated = (GetPName)0x9047, Available = (GetPName)0x9049, Evictions = (GetPName)0x904A;
    private readonly int[] _queries = new int[Ring];
    private int _frame, _lastEvictions = -1;
    private long _lastVramSample;
    private bool _open;
    private bool? _vram;
    private static int _glVersion;

    public double Ms { get; private set; }
    public double VramUsedMb { get; private set; } = double.NaN;
    public double VramTotalMb { get; private set; } = double.NaN;

    public double EvictionsPerSec { get; private set; } = double.NaN;

    public void Dispose()
    {
        if (_queries[0] != 0) GL.DeleteQueries(Ring, _queries);
        Array.Clear(_queries);
        _open = false;
    }

    public void Begin()
    {
        if (Komet.Vulkan.VulkanRenderer.GlIdle) return;
        if (_open) GL.EndQuery(QueryTarget.TimeElapsed); // the collectors went on mid-frame: the previous End() never ran
        if (_queries[0] == 0) GL.GenQueries(Ring, _queries);
        if (!Assert(_queries[0] != 0) || !Assert(_queries[Ring - 1] != 0)) return;
        GL.BeginQuery(QueryTarget.TimeElapsed, _queries[_frame % Ring]);
        _open = true;
    }

    public void End()
    {
        if (!_open) return;
        GL.EndQuery(QueryTarget.TimeElapsed);
        _open = false;
        _frame++;
        _ = Assert(_frame > 0);
    }

    public void Sample()
    {
        if (_frame < Ring || Komet.Vulkan.VulkanRenderer.GlIdle) return; // not three ended frames yet
        var oldest = _queries[(_frame + 1) % Ring];
        if (!Assert(oldest != 0)) return;
        GL.GetQueryObject(oldest, GetQueryObjectParam.QueryResultAvailable, out int ready);
        // After a stall the read would block the main thread
        if (!Assert(ready is 0 or 1) || ready == 0) return;
        GL.GetQueryObject(oldest, GetQueryObjectParam.QueryResult, out long nanoseconds);
        if (!Assert(nanoseconds is >= 0 and < 60_000_000_000)) return; // a frame never takes a minute
        Ms = nanoseconds / 1e6;
    }

    // Three more syncs: only while shown or benched; hidden, they reset (no stale eviction rate).
    public void SampleVram(bool shown)
    {
        if (!shown)
        {
            (VramUsedMb, VramTotalMb, EvictionsPerSec, _lastEvictions) = (double.NaN, double.NaN, double.NaN, -1);
            return;
        }

        _vram ??= Offered("GL_NVX_gpu_memory_info");
        if (_vram == false) return;
        GL.GetInteger(Dedicated, out var totalKb);
        GL.GetInteger(Available, out var freeKb);
        GL.GetInteger(Evictions, out var evictions); // cumulative since the device came up
        if (!Assert(totalKb > 0) || !Assert(freeKb >= 0) || !Assert(freeKb <= totalKb)) return;
        (VramTotalMb, VramUsedMb) = (totalKb / 1024.0, (totalKb - freeKb) / 1024.0);
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastVramSample) / (double)Stopwatch.Frequency;
        if (_lastEvictions >= 0 && evictions >= _lastEvictions && seconds > 0)
            EvictionsPerSec = (evictions - _lastEvictions) / seconds;
        (_lastEvictions, _lastVramSample) = (Math.Max(0, evictions), now); // a wrap starts the count over
    }

    internal static int GlVersion
    {
        get
        {
            if (_glVersion == 0)
                _glVersion = 10 * GL.GetInteger(GetPName.MajorVersion) + GL.GetInteger(GetPName.MinorVersion);
            _ = Assert(_glVersion >= 0);
            return Assert(_glVersion < 1000) ? _glVersion : 0;
        }
    }

    private static HashSet<string>? _extensions; // the context's, asked once (it lives as long as the process)

    // glGetError would steal an error the engine's CheckGlError expects
    internal static bool Offered(string extension)
    {
        if (_extensions is { } known) return known.Contains(extension);
        var count = GL.GetInteger(GetPName.NumExtensions);
        if (!Assert(count > 0)) return false;
        var made = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(count, MaxExtensions); i++)
            if (GL.GetString(StringNameIndexed.Extensions, i) is { } name)
                _ = made.Add(name);
        _extensions = made;
        return Assert(made.Count <= MaxExtensions) && made.Contains(extension);
    }
}
