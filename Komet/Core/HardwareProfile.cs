using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;

namespace Komet.Core;

// What this computer has, read once as the client starts (main thread, OpenGL current), and the knob values that suit
// it (each knob's Suggest). Knobs a player never set follow these (KometSettings.Chosen): a new GPU or more RAM moves them along on the next start.
// Unknown facts (0) suggest nothing; a knob without a suggestion keeps Komet's own default.
internal static class HardwareProfile
{
    private const GetPName DedicatedNvx = (GetPName)0x9047, TextureFreeAti = (GetPName)0x87FC;

    public readonly record struct Facts(int Cores, int RamGb, int VramGb, int RefreshHz);

    public static Facts Found { get; private set; }

    // Options.Presets' "auto" picks speed: little video memory, few cores or little RAM
    public static bool Weak => Found is var f && Assert(f.Cores >= 0) &&
                               (f.VramGb is > 0 and <= 4 || f.Cores is > 0 and <= 4 || f.RamGb is > 0 and <= 8);

    private static readonly Dictionary<string, int> Suggested = new(StringComparer.Ordinal);

    public static void Detect(ILogger? logger)
    {
        var f = Suggest(new Facts(Environment.ProcessorCount, ReadRam(), ReadVram(), ReadRefresh()));
        logger?.Notification("Komet: this computer: {0} cores, {1} GB RAM, {2} GB video memory, {3} Hz; suggested {4}",
            f.Cores, f.RamGb, f.VramGb, f.RefreshHz, string.Join(", ", Suggested.Select(static s => $"{s.Key} {s.Value}")));
        _ = Assert(f.Cores > 0) && Assert(Suggested.Count <= Knobs.MaxKnobs);
    }

    // The facts taken as this computer's, and the knob values they suggest
    internal static Facts Suggest(Facts f)
    {
        Found = f;
        Suggested.Clear();
        var knobs = Knobs.BuiltIn;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Suggest?.Invoke(f) is { } value)
                Suggested[knobs[i].Key] = value;
        return Assert(f.Cores >= 0) && Assert(Suggested.Count <= Knobs.MaxKnobs) ? f : default;
    }

    // The value for this computer, or null: Komet's default
    public static int? Suggestion(string key) =>
        NotNull(key) && Assert(Suggested.Count <= Knobs.MaxKnobs) && Suggested.TryGetValue(key, out var value) ? value : null;

    private static int ReadRam()
    {
        var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return Assert(bytes >= 0) && bytes > 0 ? (int)Math.Round(bytes / (double)(1L << 30)) : 0;
    }

    // NVX (NVIDIA, Mesa) gives the dedicated total; ATI only the free texture memory, near the total at the start
    private static int ReadVram()
    {
        try
        {
            var kb = 0;
            if (Diagnostics.GpuStats.Offered("GL_NVX_gpu_memory_info")) GL.GetInteger(DedicatedNvx, out kb);
            else if (Diagnostics.GpuStats.Offered("GL_ATI_meminfo"))
            {
                var free = new int[4];
                GL.GetInteger(TextureFreeAti, free);
                kb = free[0];
            }

            return Assert(kb >= 0) && kb > 0 ? (int)Math.Round(kb / (1024.0 * 1024.0)) : 0;
        }
        catch (Exception e) when (e is InvalidOperationException or AccessViolationException or EntryPointNotFoundException)
        {
            return 0; // no current context or no such query
        }
    }

    // The fastest monitor: the window may sit on any of them, and the bridge follows the one it is on (FrameBridge.Retuned)
    private static int ReadRefresh()
    {
        try
        {
            var best = 0;
            foreach (var monitor in Monitors.GetMonitors().Bounded(16))
                best = Math.Max(best, monitor.CurrentVideoMode.RefreshRate);
            return Assert(best >= 0) ? best : 0;
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException)
        {
            return 0;
        }
    }
}
