using System.Diagnostics;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// The engine's fps cap (no VSync) sleeps (int)(1000 / MaxFps - elapsed ms): truncation makes frames 0..1 ms short, so
// at 144 fps they alternate ~6/~7 ms and overshoot the cap. Here the wait runs to the exact interval (whole ms asleep,
// never longer than the engine would, the rest yielding), so the engine's sleep computes 0. Off, it sleeps as the
// engine, so FrameClock's later prefix times from the same point. Vulkan's GPU wait (VulkanRenderer.Paced) comes first.
internal static class FramePacer
{
    private const double SleepMarginMs = 0.5, StepMarginMs = 1.1; // timer slack and wake-up: the rest is yielded away
    private const int MaxYields = 1 << 20, MaxSteps = 16;

    private static AccessTools.FieldRef<ClientPlatformWindows, Stopwatch>? _watch;

    public static bool Enabled { get; set; } = true;

    public static bool Patched { get; private set; }

    public static void Install(Harmony harmony)
    {
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        var watch = AccessTools.FieldRefAccess<ClientPlatformWindows, Stopwatch>("frameStopWatch");
        Patched = false;
        if (!NotNull(harmony) || !NotNull(frame) || !NotNull(watch)) return;
        _watch = watch;
        Patched = NotNull(harmony.Patch(frame, new HarmonyMethod(Pace) { priority = Priority.First }));
    }

    // Engine's condition, then its interval on its own stopwatch
    private static void Pace(ClientPlatformWindows __instance)
    {
        Komet.Vulkan.VulkanRenderer.Paced();
        if (FrameSettings.VsyncMode == 1 || _watch is not { } field) return;
        var fps = __instance.MaxFps;
        if (!(fps > 10f && fps < 241f)) return;
        var watch = field(__instance);
        if (!NotNull(watch) || !watch.IsRunning) return;
        if (Enabled)
        {
            Wait(watch, fps);
            return;
        }

        var engine = (int)(1000f / fps - 1000f * watch.ElapsedTicks / Stopwatch.Frequency); // the engine's own, as float
        if (engine > 0) Thread.Sleep(engine);
    }

    internal static void Wait(Stopwatch watch, float fps)
    {
        if (!NotNull(watch) || !Assert(fps > 0f)) return;
        var target = (long)(Stopwatch.Frequency / (double)fps);
        var leftMs = (target - watch.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
        if (leftMs > SleepMarginMs + 1) Thread.Sleep((int)(leftMs - SleepMarginMs));
        // Linux/macOS sleep 1 ms within a few dozen µs (fewer yields burn the core); Windows' timer may sleep 15 ms
        for (var i = 0; i < MaxSteps && !OperatingSystem.IsWindows() &&
                        (target - watch.ElapsedTicks) * 1000.0 / Stopwatch.Frequency > StepMarginMs; i++)
            Thread.Sleep(1);
        for (var i = 0; i < MaxYields && watch.ElapsedTicks < target; i++)
            if (!Thread.Yield())
                Thread.SpinWait(32);
        _ = Assert(target > 0);
    }
}
