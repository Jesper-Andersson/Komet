using HarmonyLib;
using Komet.Diagnostics;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Vulkan-only: Vulkan draws every terrain pass (liquid depth, shadow maps, opaque, transparent, water plants) and, via
// the scene, the rest of the frame; OpenGL rests. Programs are read back from the driver as compiled, keeping engine
// settings and mod changes. The phase is decided only in Before, so a frame is wholly Vulkan's or GL's; Off keeps
// Vulkan's holdings.
internal static partial class VulkanRenderer
{
    public enum Phase
    {
        Off,
        Waiting,
        Running,
        Failed
    }

    public enum Pass
    {
        None = -1,
        LiquidDepth,
        Shadows,
        Opaque,
        Transparent,
        WaterPlants
    }

    // Also: the pass the program draws after the transparent terrain too (chunkopaque the water plants)
    private static readonly (string Name, Pass Pass, Pass Also)[] Programs =
    [
        ("chunkliquiddepth", Pass.LiquidDepth, Pass.None), ("chunkshadowmap", Pass.Shadows, Pass.None),
        ("chunkopaque", Pass.Opaque, Pass.WaterPlants), ("chunktopsoil", Pass.Opaque, Pass.None),
        ("chunkliquid", Pass.Transparent, Pass.None), ("chunktransparent", Pass.Transparent, Pass.None)
    ];

    // Uniforms that change with every pool: push constants
    private static readonly HashSet<string> Hot = new(["origin"], StringComparer.Ordinal);
    private static readonly GlslPort.Ported?[] Ports = new GlslPort.Ported?[Programs.Length];
    private static readonly int[] Built = new int[Programs.Length]; // the GL program each port was made from
    private static readonly Build<GlslPort.Ported>?[] Porting = new Build<GlslPort.Ported>?[Programs.Length];

    private static ILogger? _logger;
    private static ICoreClientAPI? _api;
    private static TerrainRenderer? _renderer;
    private static string _failure = "";
    private static bool _failed;

    // The wait for the GPU at the frame's start instead of in its middle (VulkanFrame.Pace)
    public static bool Pacing { get; set; } = true;

    // Vulkan-only: refreshes the engine misses are shown turned as the camera goes (FrameBridge)
    public static bool Bridging { get; set; }

    // Vulkan-only: the terrain past TerrainRenderer.FarRadius drawn now and then into a layer laid in between
    public static bool FarCaching { get; set; }

    // FramePacer's, at window_RenderFrame's start
    public static void Paced()
    {
        if (!Pacing || State != Phase.Running || _renderer is not { } renderer || !Assert(renderer.Frame.Number >= 0)) return;
        renderer.Frame.Pace();
    }

    public static Phase State { get; private set; }
    public static string Status { get; private set; } = "";

    public static bool Unavailable => VulkanCore.Failed;

    private static readonly bool[] Taking = new bool[5];

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Stop();
        if (!NotNull(harmony) || !NotNull(api) || !NotNull(logger) || !NotNull(api.Event)) return;
        (_logger, _api) = (logger, api);
        Register(api.Event);
        Patch(harmony);
        VulkanWatch.Watching = () => State == Phase.Running;
        VulkanWatch.Start();
        Recorded();
    }

    public static void Stop()
    {
        _ = Assert(Built.Length == Programs.Length);
        Unhook();
        Unpushed();
        VulkanWatch.Stop();
        Release();
        GlTap.Untap();
        (_failed, _failure, State, Status) = (false, "", Phase.Off, "");
        if (_held) (_only, _held) = (false, false); // switched off while lean could not give back: off with the world
        GlTap.GuiStage = false;
        foreach (var porting in Porting.Bounded(Programs.Length)) porting?.Drop();
        Array.Clear(Porting);
        Array.Clear(Ports);
        Array.Clear(Built);
        Array.Clear(Taking);
    }

    // Every GL name stays the engine's
    private static void Release()
    {
        if (_renderer is not { } renderer) return;
        Culling(renderer, false); // before the backend the passes use goes with the renderer
        SceneGone(); // before the renderer whose frame and programs it uses
        renderer.Dispose();
        _renderer = null;
        Array.Clear(Built); // a new renderer ports again
        _ = Assert(Built.Length == Programs.Length);
    }

    private static ShaderProgramBase?[] Engine() =>
        Assert(Programs.Length == 6)
            ? [ShaderPrograms.Chunkliquiddepth, ShaderPrograms.Chunkshadowmap, ShaderPrograms.Chunkopaque,
                ShaderPrograms.Chunktopsoil, ShaderPrograms.Chunkliquid, ShaderPrograms.Chunktransparent]
            : [];

    // Ported on a builder and awaited: a pass without its port would be missing from the picture
    internal static void Update(ShaderProgramBase?[] programs, TerrainRenderer? renderer, ReadOnlySpan<bool> passes)
    {
        if (!NotNull(programs) || !Assert(programs.Length == Programs.Length)) return;
        for (var i = 0; i < Math.Min(programs.Length, Programs.Length); i++)
        {
            if (Needed(i, passes) && programs[i] is { ProgramId: > 0 } program && program.ProgramId != Built[i])
                Port(i, program.ProgramId, renderer);
            if (Porting[i] is { Done: false } waited) _ = waited.Awaited(Hitches.Kind.Port); // else missing
            if (Porting[i] is { Done: true } done) Ported(i, done, renderer);
        }
    }

    private static void Port(int at, int program, TerrainRenderer? renderer)
    {
        if (!Index(at, Programs.Length) || !Assert(program > 0)) return;
        renderer?.Forget(Built[at]);
        Porting[at]?.Drop(); // of the program the engine rebuilt
        (Built[at], Ports[at]) = (program, null);
        var start = Hitches.Now;
        Porting[at] = ProgramPorts.Started(program, Programs[at].Name, out var error, Hot);
        Hitches.Since(Hitches.Kind.Port, start);
        if (Porting[at] is null) _logger?.Notification("Komet: Vulkan port of {0}: {1}", Programs[at].Name, error);
    }

    private static void Ported(int at, Build<GlslPort.Ported> done, TerrainRenderer? renderer)
    {
        if (!Index(at, Programs.Length) || !NotNull(done)) return;
        Porting[at] = null;
        var ported = Ports[at] = done.Take(out var error);
        if (ported is not null && renderer?.Watch(Programs[at].Name, Built[at], ported) is { } watched)
        {
            watched.Sources = PipelineRecipes.SourcesOf(Built[at]); // the pipelines it drew with at earlier starts begin now
            renderer.Warm(watched);
        }
        _logger?.Notification("Komet: Vulkan port of {0}: {1}", Programs[at].Name,
            ported is null ? error : $"{FrameClock.ToMs(done.Ticks):0} ms off the engine's thread");
    }

    private static bool Needed(int at, ReadOnlySpan<bool> passes) =>
        Index(at, Programs.Length) && Assert(passes.Length == Taking.Length) &&
        (passes[(int)Programs[at].Pass] || (Programs[at].Also != Pass.None && passes[(int)Programs[at].Also]));

    // None for what is no terrain pass's (Komet's distant shadow cascade after the far map: the scene's)
    internal static Pass PassOf(int program, EnumRenderStage stage)
    {
        if (!Assert(program >= 0) || program == 0) return Pass.None;
        var at = Array.IndexOf(Built, program);
        if (at < 0 || Ports[at] is null) return Pass.None;
        var pass = Programs[at].Pass;
        return (pass, stage) switch
        {
            (Pass.LiquidDepth, EnumRenderStage.Before) => pass,
            (Pass.Shadows, EnumRenderStage.ShadowFar or EnumRenderStage.ShadowNear) => pass,
            (Pass.Opaque, EnumRenderStage.Opaque) => pass,
            (Pass.Opaque, EnumRenderStage.AfterOIT) => Programs[at].Also,
            (Pass.Transparent, EnumRenderStage.OIT) => pass,
            _ => Pass.None
        };
    }

    // The engine framebuffers Vulkan's images stand for: every one with the scene, else the terrain passes'
    private static int[] Shared() => Assert(Taking.Length == 5) && SceneOn ? EveryFramebuffer : TerrainAndTransparent;

    private const int EngineFramebuffers = 18; // EnumFrameBuffer: Primary 0 to SSAOBlurHorizontalHalfRes 17

    private static readonly int[] EveryFramebuffer = [.. Enumerable.Range(0, EngineFramebuffers)],
        TerrainAndTransparent =
        [
            TerrainTargets.Primary, TerrainTargets.LiquidDepth, TerrainTargets.ShadowFar, TerrainTargets.ShadowNear,
            TerrainTargets.Transparent
        ];
}
