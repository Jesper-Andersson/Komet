using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace Komet.Diagnostics;

// /proc/<pid>/task/<tid>/schedstat: "<ns on a core> <ns runnable, waiting> <timeslices>\n". Linux only.
internal static class SchedStat
{
    public const int Run = 0, Wait = 1, Fields = 3;
    private const int MaxBytes = 128;

    private static SafeFileHandle? _main;
    private static bool _opened;

    // Main thread's core wait so far (FrameClock, TessGovernor); /proc/thread-self opened on first call. -1 for none or
    // error.
    public static long MainWaitNs()
    {
        if (!_opened) OpenSelf();
        if (_main is null) return -1;
        var delay = Read(_main, Wait);
        if (_main.IsClosed) _main = null;
        return Assert(delay >= -1) ? delay : -1;
    }

    private static void OpenSelf()
    {
        _opened = true;
        if (!OperatingSystem.IsLinux() || !Assert(_main is null)) return;
        try
        {
            _main = File.OpenHandle("/proc/thread-self/schedstat");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _main = null; // nothing measured: the column stays blank, the ceiling stands
        }
    }

    // One pread into the stack: any thread, no allocation. -1 for a missing or cut field; a throwing handle is
    // disposed.
    [SkipLocalsInit]
    public static long Read(SafeFileHandle file, int field)
    {
        if (!NotNull(file) || !Index(field, Fields)) return -1;
        Span<byte> text = stackalloc byte[MaxBytes];
        int read;
        try
        {
            read = RandomAccess.Read(file, text, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            file.Dispose();
            return -1;
        }

        return Assert(read is >= 0 and <= MaxBytes) ? Field(text[..read], field) : -1;
    }

    internal static long Field(ReadOnlySpan<byte> text, int field)
    {
        if (!Index(field, Fields)) return -1;
        long value = 0;
        int at = 0, digits = 0;
        foreach (var c in text.Bounded(MaxBytes))
        {
            if (c is >= (byte)'0' and <= (byte)'9') (value, digits) = (value * 10 + (c - '0'), digits + 1);
            else if (at++ == field) return digits > 0 ? value : -1;
            else (value, digits) = (0, 0);
        }

        return -1;
    }
}
