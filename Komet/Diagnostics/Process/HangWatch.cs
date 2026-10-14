using System.Diagnostics;
using System.Globalization;

namespace Komet.Diagnostics;

// After HangMs without a frame: log line, thread states, kernel waits and (with dotnet-stack) managed stacks, while
// hanging.
internal static class HangWatch
{
    private const int CheckMs = 1000, HangMs = 10_000, DumpMs = 60_000;

    private static Timer? _timer;
    private static ILogger? _logger;
    private static long _seen = -1, _since;
    private static bool _reported;

    public static void Start(ILogger logger)
    {
        if (!NotNull(logger) || !Assert(FrameClock.Frames >= 0) || _timer is not null) return;
        (_logger, _seen, _since, _reported) = (logger, -1, Environment.TickCount64, false);
        _timer = new Timer(static _ => Check(), null, CheckMs, CheckMs);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        (_timer, _logger) = (null, null);
        _ = Assert(!_reported || HangMs > 0);
    }

    private static void Check()
    {
        var (frames, now) = (FrameClock.Frames, Environment.TickCount64);
        if (!Assert(frames >= 0) || !Assert(now >= _since)) return;
        if (frames != _seen)
        {
            (_seen, _since, _reported) = (frames, now, false);
            return;
        }

        if (_reported || now - _since < HangMs || _logger is not { } logger) return;
        _reported = true; // once a hang
        logger.Warning("Komet: the main thread has finished no frame for {0} s (frame {1})", (now - _since) / 1000, frames);
        _ = Dump(logger);
        _ = Assert(_reported);
    }

    // Includes native threads (Mesa, Vulkan driver) with the kernel function each waits in
    private static string? Dump(ILogger logger)
    {
        if (!NotNull(GamePaths.Logs) || !Assert(Environment.ProcessId > 0)) return null;
        var path = Path.Combine(GamePaths.Logs, "komet-hang-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        try
        {
            var report = Tool() is { } tool ? Stacks(tool) : "dotnet-stack is not installed: no managed stacks";
            File.WriteAllText(path, report + "\nEvery thread as the kernel sees it (id, name, state, waits in):\n" + Komet.Vulkan.VulkanWatch.Threads());
            logger.Warning("Komet: the stacks of every thread are in {0}", path);
            return path;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or
                                       UnauthorizedAccessException or AggregateException) // on a timer thread: anything left would end the game
        {
            logger.Warning("Komet: no stacks of the hang: {0}", e.Message);
            return null;
        }
    }

    private static string Stacks(string tool)
    {
        if (!NotNull(tool) || !Assert(tool.Length > 0)) return "";
        using var stack = Process.Start(new ProcessStartInfo(tool, ["report", "-p", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        });
        if (!NotNull(stack) || !Assert(stack.StartInfo.RedirectStandardOutput)) return "";
        var (output, error) = (stack.StandardOutput.ReadToEndAsync(), stack.StandardError.ReadToEndAsync());
        if (!stack.WaitForExit(DumpMs)) stack.Kill();
        // empty report (no attach permission, as on macOS) says why
        var report = output.Wait(DumpMs) ? output.Result : "";
        var (exit, why) = (stack.HasExited ? stack.ExitCode : -1, error.Wait(DumpMs) ? error.Result : "");
        return report.Length > 0 ? report : $"dotnet-stack wrote no stacks (exit {exit}): {why}";
    }

    private static string? Tool()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet-stack.exe" : "dotnet-stack";
        if (!Assert(name.Length > 0)) return null;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", name);
        if (File.Exists(home)) return home;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Bounded(256))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }
}
