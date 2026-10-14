using System.Globalization;
using Komet.Diagnostics;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

internal static partial class VulkanRenderer
{
    private const ulong Budget = 12UL << 30, LogGrowth = 512UL << 20;
    private const long GuardMs = 250;
    private const int ReportEvery = 600, LogEvery = 3600, VerifyAt = 300, LeanEvery = 600;
    private static ulong _logged;
    private static long _guarded;
    private static string _waits = "";

    private static void Report(string waits)
    {
        if (!NotNull(waits)) return;
        var phase = waits.Length > 0 ? Phase.Waiting : Phase.Running;
        if (phase != State || waits != _waits)
        {
            Status = waits.Length > 0 ? waits : "drawing: " + Passes();
            if (waits.Length > 0 || State != Phase.Running) _logger?.Notification("Komet: Vulkan {0}", Status);
        }

        (State, _waits) = (phase, waits);
        _ = Assert(State is Phase.Running or Phase.Waiting);
    }

    private static string Passes()
    {
        var names = new List<string>(4);
        if (Taking[(int)Pass.LiquidDepth]) names.Add("liquid depth");
        if (Taking[(int)Pass.Shadows])
            names.Add(!ShadowView.Differed
                ? $"shadow maps (casters culled by the view, {EntityCasters.Culled} of {EntityCasters.Tested} entity shadows)"
                : "shadow maps");
        if (Taking[(int)Pass.Opaque]) names.Add(OcclusionCulling.Drawer is null ? "opaque" : "opaque (culled on the GPU)");
        if (Taking[(int)Pass.Transparent]) names.Add("transparent");
        if (Taking[(int)Pass.WaterPlants]) names.Add("water plants");
        if (SceneParts() is { Length: > 0 } parts) names.Add("and of the rest " + parts);
        if (_windowing)
            names.Add(_renderer?.Showing == true ? "the window, shown by Vulkan" + Unshown() : "the window, not shown");
        _ = Assert(names.Count <= 7);
        return names.Count == 0 ? "no pass (all switched off)" : string.Join(", ", names);
    }

    private static string Unshown() =>
        _renderer is { Unshown: > 0 } renderer && Assert(renderer.Unshown < long.MaxValue)
            ? $" ({renderer.Unshown} frames by OpenGL, as Vulkan could not)"
            : "";

    private static void Periodically(TerrainRenderer renderer)
    {
        if (!NotNull(renderer)) return;
        var frame = renderer.Frame.Number;
        if (!Assert(frame >= 0)) return;
        if (frame == VerifyAt) Verify(renderer);
        if (frame % LeanEvery == LeanEvery / 2 && LeanOn && renderer.LeanEarlier() is (> 0, var bytes) lean)
            _logger?.Notification("Komet: Vulkan-only lean: {0} textures OpenGL held from before ({1} MB) given a stand-in",
                lean.Count, bytes >> 20);
        if (frame % ReportEvery != 1) return;
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // "1.5 ms" in the English report whatever the game's language
        try
        {
            Status = (_waits.Length > 0 ? _waits + "; " : "") + Short(renderer);
            // the first window (frames 1-600) is the post-start upload burst: its per-pass times are not steady state
            if (frame % LogEvery == 1 || frame == 2 * ReportEvery + 1) _logger?.Notification("Komet: {0}", Full(renderer));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // Since the last report; zero while Vulkan does not run
    internal static (double Draws, double Left, double Segments, long MemoryMb, string[] Closers) Snapshot()
    {
        if (State != Phase.Running || _renderer is not { } renderer) return (0, 0, 0, 0, []);
        var frames = Math.Max(renderer.Frame.Frames, 1);
        var draws = renderer.Draws + (_scene?.Draws ?? 0);
        // what Vulkan leaves is missing from the picture, OpenGL draws none of it
        var left = renderer.Missed + renderer.MissedWaiting;
        var closers = renderer.Frame.Closers.OrderByDescending(static c => c.Value).Take(6).Select(static c => c.Key).ToArray();
        _ = Assert(frames > 0) && Assert(draws >= 0);
        return ((double)draws / frames, (double)left / frames, (double)renderer.Frame.Segments / frames, (long)(VkMemory.Bytes >> 20),
            closers);
    }

    // A long HUD line made the panel slow to draw; the full report goes to the log
    private static string Short(TerrainRenderer renderer)
    {
        var frames = Math.Max(renderer.Frame.Frames, 1);
        return Assert(frames > 0) && NotNull(renderer) ? OnlyShort(renderer, frames) : "";
    }

    private static string Full(TerrainRenderer renderer)
    {
        var frame = renderer.Frame;
        var frames = Math.Max(frame.Frames, 1);
        var ms = FrameClock.TickMs / frames;
        _ = Assert(frames > 0) && Finite(ms);
        return $"terrain in Vulkan ({Passes()}): {frames} frames, recording {renderer.TakeTicks * ms:0.00} ms (packing " +
               $"{TerrainDraw.PackTicks * ms:0.00}) a frame, {(double)renderer.Draws / frames:0} pool draws and " +
               $"{(double)frame.Segments / frames:0.0} segments a frame, {frame.SubmitTicks * ms:0.00} ms submitting a frame, " +
               $"{renderer.Pools.Count} pools ({renderer.Pools.Bytes >> 20} MB: {renderer.Pools.Breakdown()}, {Born} adopted as made), " +
               OnlyHeld(renderer) + $", {renderer.UnalignedFaces} face writes not whole quads; closed by " +
               string.Join(", ", frame.Closers.OrderByDescending(c => c.Value).Take(8).Select(c => $"{c.Key} {c.Value}")) +
               Culled(renderer, frames) + SceneReport(frames) + renderer.Frame.Times() +
               Hitches.Report() +
               Builds.Report() + ShaderCache.Report() + "; memory " +
               VkMemory.Report() + "; " + Held();
    }

    private static string SceneReport(long frames)
    {
        if (_scene is not { } scene || !Assert(frames > 0)) return "";
        var ms = FrameClock.TickMs / frames;
        var mirrors = scene.Mirrors;
        _ = Assert(Finite(ms));
        return $"; the rest of the frame: {(double)scene.Draws / frames:0} draws, {(double)scene.Clears / frames:0.0} " +
               $"clears ({scene.EarlyClears} kept from between frames), {(double)scene.Blits / frames:0.0} blits, " +
               $"{(double)scene.Left / frames:0.0} missing a frame, recording {scene.Ticks * ms:0.00} ms a frame; {mirrors.Count} buffer mirrors, {mirrors.Bytes >> 20} MB, " +
               $"{mirrors.Writes} writes, {mirrors.Renames} renamed, {mirrors.Imports} read back " +
               $"({mirrors.ImportedBytes >> 10} KB, {mirrors.Asked} sizes asked of OpenGL)" +
               (mirrors.Imports > 0 ? $" ({mirrors.Reasons})" : "");
    }

    // The HUD line: segments, and what is missing from the picture
    private static string OnlyShort(TerrainRenderer renderer, long frames)
    {
        if (!NotNull(renderer) || !Assert(frames > 0)) return "";
        var scene = _scene is { } s ? $"{(double)s.Draws / frames:0} other draws, " : "";
        return $"Vulkan-only draws {Passes()}: {(double)renderer.Draws / frames:0} pool draws, {scene}" +
               $"{(double)renderer.Frame.Segments / frames:0.0} segments a frame, " +
               $"{(double)(renderer.Missed + renderer.MissedWaiting) / frames:0.0} calls missing a frame " +
               $"({renderer.MissedKind} kinds){(Bridging ? $", {(double)renderer.Bridged / frames:0.00} frames bridged a frame" : "")}" +
               $"{(renderer.FarRefreshes > 0 ? $", far cache laid {renderer.FarLays} frames, drawn anew {renderer.FarRefreshes}" : "")}; " +
               $"{VkMemory.Bytes >> 20} MB";
    }

    // Vulkan-only: the images and mappings of its own, and what it had to read back from OpenGL
    private static string OnlyHeld(TerrainRenderer renderer)
    {
        if (renderer.Images is not { } images || renderer.Maps is not { } maps || !Assert(renderer.Frame.Number >= 0)) return "";
        _ = Assert(images.Count >= 0) && Assert(maps.Count >= 0);
        return $"Vulkan-only: {images.Count} texture images (about {images.Bytes >> 20} MB), {images.Uploads} uploads, " +
               $"mipmaps and copies made by Vulkan, {images.Readbacks} read back from OpenGL ({images.ReadBytes >> 20} MB), " +
               $"{images.Unseen} changes it could not follow, {images.Refused} uploads or images it had no room or format " +
               $"for, {GlTap.UntrackedCalls} calls on textures it does not follow; {maps.Count} persistent mappings of " +
               $"its own ({maps.Bytes >> 20} MB), {renderer.PoolWrites} pool uploads ({renderer.PoolWritesLost} lost), " +
               $"{renderer.Pools.Readbacks} pool buffers read back from OpenGL; {renderer.Answered} OpenGL reads answered " +
               $"from Vulkan's images; {renderer.Missed} GPU calls missing from the picture" +
               (renderer.Missed > 0 ? $" ({renderer.MissedKinds(6)})" : "") +
               $", {renderer.MissedWaiting} draws missing while their port or pipeline was made" +
               (_scene is { Unported.Length: > 0 } scene ? $"; programs with no Vulkan port: {scene.Unported}" : "") +
               $"; {renderer.Snapshots} snapshots of images drawn into while sampled" + Warmed(renderer) +
               $"; {renderer.Frame.EarlyBegins} frames begun by work between frames, {GlTap.MultiDrawRanges} multi-draw " +
               $"ranges taken one by one (at most {GlTap.LargestMultiDraw} in one), " +
               $"{_scene?.Mirrors.PersistentReads ?? 0} inputs read from persistent mappings without OpenGL's fences" +
               (OcclusionCulling.Suspended ? "; OcclusionCulling suspended (the device draws with no count the GPU wrote)" : "") +
               (GlTap.Leaned > 0
                   ? $"; lean: {GlTap.Leaned} large textures and buffers ({GlTap.LeanBytes >> 20} MB) kept out of OpenGL's " +
                     $"memory, {GlTap.LeanMapped} given back for a map"
                   : "") + ForMods(renderer.Device);
    }

    // What the device gives mods' raw OpenGL (VulkanDevice.Only)
    private static string ForMods(VulkanDevice device)
    {
        if (!NotNull(device) || !Assert(device.DepthStencil is 0 or Vk.FormatD24S8 or Vk.FormatD32S8)) return "";
        var stencil = device.DepthStencil switch { Vk.FormatD24S8 => "D24S8", Vk.FormatD32S8 => "D32S8", _ => "none" };
        return $"; for mods: depth and stencil {stencil}, 8-bit indices {(device.IndexUint8 ? "yes" : "no")}, instance " +
               $"divisors past 1 {(device.Divisors ? "yes" : "no")}, several indirect draws at once " +
               $"{(device.MultiDraws ? "yes" : "no")}";
    }

    private static string Warmed(TerrainRenderer renderer) =>
        _scene is { } scene && Assert(scene.PipelinesStarted >= 0) && NotNull(renderer)
            ? $"; {scene.PipelinesStarted} scene and {renderer.PipelinesStarted} terrain pipelines begun before their first " +
              "draw (known from earlier starts)"
            : "";

    private static string Culled(TerrainRenderer renderer, long frames)
    {
        if (!NotNull(renderer) || !Assert(frames > 0) || OcclusionCulling.Drawer is null) return "";
        return $"; OcclusionCulling in Vulkan: {(double)renderer.Counted / frames:0.0} draws a frame, " +
               $"{OcclusionCulling.Refused} refused, {renderer.Compute.Refused} passes without a segment or memory" +
               (OcclusionCulling.RowsUsed > 0
                   ? $", from rows ({OcclusionCulling.RowsUsed} rows, {OcclusionCulling.Rewrites} regions written in full, {OcclusionCulling.Resyncs} brought up to date)"
                   : "") + $", {(double)OcclusionCulling.SortedDraws / frames:0.0} draws a frame sorted near to far, shadow casters " +
               (ShadowView.Differed ? "no longer culled by the view (it differed from the engine's)" : "culled by the view") +
               DrawnTerrain();
    }

    // NaN: no statistics read back yet (a report after the first frame, before the readback ring filled)
    internal static string DrawnTerrain()
    {
        OcclusionCulling.Sample();
        var (ranges, hidden, triangles, gone) = (OcclusionCulling.Stat(0), OcclusionCulling.Stat(1), OcclusionCulling.Stat(2),
            OcclusionCulling.Stat(3));
        if (double.IsNaN(ranges) || ranges <= 0 || !Finite(ranges) || !Assert(hidden <= ranges)) return "";
        var (late, lateTriangles) = (OcclusionCulling.LateRanges, OcclusionCulling.LateTriangles);
        return $"; the camera's opaque terrain a frame: {ranges - hidden + late:0} draw commands of {ranges:0} in the " +
               $"frustum, {(triangles - gone + lateTriangles) / 1e6:0.00} of {triangles / 1e6:0.00} million triangles";
    }

    // Per-pool and per-frame values (origin, the counters) differ by nature
    private static void Verify(TerrainRenderer renderer)
    {
        var differ = new List<string>();
        foreach (var program in renderer.Programs.ToArray().Bounded(256))
            foreach (var u in program.Ported.VertexUniforms.Members.Concat(program.Ported.FragmentUniforms.Members)
                         .ToArray().Bounded(512))
                Compare(program, u, differ);
        _ = Assert(differ.Count <= 256 * 512) && Assert(renderer.Programs.Count <= 256);
        _logger?.Notification("Komet: Vulkan terrain uniforms against OpenGL: {0}",
            differ.Count == 0 ? "all alike" : string.Join("; ", differ.Take(60)));
    }

    private static void Compare(TerrainRenderer.Program program, GlslPort.Uniform u, List<string> differ)
    {
        var location = GL.GetUniformLocation(program.Id, u.Name);
        var (columns, rows) = GlslPort.Shape(u.Type);
        if (location < 0 || columns * rows is 0 or > 16 || !NotNull(differ)) return;
        var held = program.Mirror.Held(u);
        var gl = new float[16];
        GL.GetUniform(program.Id, location, gl);
        var integer = u.Type is "int" or "bool" || u.Type.StartsWith("ivec", StringComparison.Ordinal);
        for (var i = 0; i < Math.Min(columns * rows, 16); i++)
        {
            var mine = i < held.Length ? held[i] : 0;
            var value = integer ? (int)mine : BitConverter.UInt32BitsToSingle(mine);
            var theirs = gl[i]; // an int uniform read as floats comes converted
            if (Math.Abs(value - theirs) > 1e-4 * Math.Max(1, Math.Abs(theirs)))
                differ.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{program.Name}.{u.Name}[{i}] mirror {value} GL {theirs}"));
        }
    }

    // Lets go of everything for this world before the kernel's OOM killer takes the game
    private static bool MemoryGuard(TerrainRenderer renderer)
    {
        if (!NotNull(renderer) || Environment.TickCount64 - _guarded < GuardMs) return false;
        _guarded = Environment.TickCount64;
        var bytes = VkMemory.Bytes;
        if (bytes > _logged + LogGrowth || bytes + LogGrowth < _logged)
        {
            _logged = bytes;
            _logger?.Notification("Komet: Vulkan memory {0}; {1}", VkMemory.Report(), Held());
        }

        var (available, total) = VulkanWatch.SystemMemory();
        var low = total > 0 && available < total / 12;
        if (!low && bytes < Budget) return false;
        Fail($"Vulkan let go for this world: {(low ? "the system ran low on memory" : "Vulkan used too much")} " +
             $"({available >> 20} of {total >> 20} MB available); Vulkan held {VkMemory.Report()}");
        _logger?.Notification("Komet: Vulkan memory after letting go: {0}", VkMemory.Report());
        return Assert(_failed) && Assert(_renderer is null);
    }
}
