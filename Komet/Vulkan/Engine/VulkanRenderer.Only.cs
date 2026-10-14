using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Vulkan-only ("OpenGL idle", knob VulkanOnly): Vulkan does all in-world GPU work; GL keeps names, state, the menu and
// loading screens.
// GlTap's record (GlTap.Recording) runs from mod start, so Vulkan learns GL objects from what the engine told GL, not
// by asking back; the device is picked without GL_DEVICE_UUID. Device, renderer and scene write sink also exist from
// mod start (Early), so atlases, pools and meshes reach Vulkan as made; every resource is Vulkan's own
// (TerrainRenderer.Only), no memory shared. The frame (VulkanFrame.Only) has no semaphores, one segment from first work
// to present, no GL sharing extensions, presented only by Vulkan's swapchain.
// The scene takes every stage plus between-frame work, blits/copies/clears (scissored, scaled, levels/layers, stencil
// bits dropped), draws into mip levels and array layers, draws sampling their own target (via
// snapshots), multi-draws range by range. GL reads (screenshots) are answered from Vulkan images; OcclusionCulling
// rests without drawIndirectCount; without a swapchain Vulkan-only lets go. What cannot be taken is missing from the
// picture, logged once per kind and counted for the report.
// GL rests (GlTap.Resting): only uploads, names and state, so it can take over at once on failure or switch-off
// (redrawing targets only Vulkan drew). A draw waits for its port and pipeline instead of going missing (ports begun at
// link, pipelines from earlier starts' recipes begun ahead); the frame begins before Before's first renderer; lean
// keeps large textures and buffers out of GL memory and gives them back, with Vulkan-only textures and render targets
// written back, on takeover (a faulted device strands GL: only the GUI over a cleared frame). Snapshots are sampled via
// their texture's level range. Mods' raw GL is drawn where Vulkan can express it: stencil, renderbuffers, texture and
// masked clears, points, line loops, 8-bit indices, instance divisors, indirect draws, layered framebuffers.
internal static partial class VulkanRenderer
{
    // Off: GL draws again from its next call (GlTap.Resting), the renderer is remade next frame. Lean's holdings go
    // back first; if that fails with the device alive, Vulkan-only stays on (held) until the world is left
    public static bool Only
    {
        get => _only;
        set
        {
            if (!value && _only && Strands("was switched off", holds: true) && _held) return;
            var again = value && !_only;
            (_only, GlslPort.PointSized) = (value, value || GlslPort.PointSized); // ports made since stay as they are
            // in the world, pools GL got back whole on takeover are made anew for Vulkan-only (lean, packed)
            if (again && OnlyOn) Komet.Rendering.TerrainRebuild.Request();
            if (!value) GlTap.Resting = false;
        }
    }

    private static bool _only, _held;

    // The renderer's switch: the device starts with it too
    public static bool OnlyOn => Only;

    // VulkanOnlyLean: GL gets no copy of the world's large textures and buffers (GlTap.Lean), saving that second copy,
    // given back from Vulkan's when Vulkan-only lets go (Strands)
    public static bool Lean { get; set; }

    private static bool LeanOn => Lean && OnlyOn;

    // Give back what lean withheld from Vulkan's holdings (false: GL can take over). Only a faulted device gives
    // nothing: GL rests for good (GlTap.Strand) until the world is left, drawing only the GUI over a cleared frame
    // (Overlaid). holds: Vulkan-only may stay on instead (_held)
    private static bool Strands(string why, bool holds = false)
    {
        if (GlTap.Leaned == 0 || !GlTap.Tapped || !GlTap.Recording || !NotNull(why)) return false;
        if (GlTap.Stranded) return true;
        GlTap.Lean = false; // from now on OpenGL gets every texture and buffer whole
        if (Restored(why)) return false;
        if (holds && _renderer is { } renderer && renderer.Frame.Fault.Length == 0)
        {
            _held = true;
            _logger?.Error("Komet: Vulkan-only {0}, but OpenGL could not be given back the {1} large textures and buffers lean " +
                           "kept from it: Vulkan-only stays on until the world is left", why, GlTap.Leaned);
            return Assert(_held);
        }

        GlTap.Strand();
        _logger?.Error("Komet: Vulkan-only {0}, and with VulkanOnlyLean OpenGL holds none of the world's large textures and " +
                       "buffers ({1} MB), which the failed device cannot give back: OpenGL shows only the GUI. Leave the " +
                       "world and join it again.", why, GlTap.LeanBytes >> 20);
        return Assert(GlTap.Stranded);
    }

    // Every lean texture and buffer given its full size and Vulkan's contents back (TerrainRenderer.Restore)
    private static bool Restored(string why)
    {
        if (_renderer is not { } renderer || !NotNull(why)) return false;
        try
        {
            if (renderer.Restore(_scene?.Mirrors) is not var (whole, blank, buffers, bytes)) return false;
            _logger?.Notification("Komet: Vulkan-only {0}: OpenGL got lean's {1} textures ({2} blank: none of their " +
                                  "contents in a format read exactly) and {3} buffers ({4} MB) back at their full size", why,
                whole + blank, blank, buffers, bytes >> 20);
            _ = Assert(GlTap.Leaned == 0);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger?.Warning("Komet: Vulkan-only could not give OpenGL its lean textures and buffers back: {0}", e.Message);
            return false;
        }
    }

    // Stranded: the frame OpenGL shows begins cleared (nothing of the world drawn into it), the GUI goes over it
    private static void Overlaid()
    {
        if (!GlTap.Stranded || _renderer is not null || !Assert(!GlTap.Resting) || !Assert(GlTap.Tapped)) return;
        using var quiet = GlTap.Quietly();
        OpenTK.Graphics.OpenGL.GL.ClearNamedFramebuffer(0, OpenTK.Graphics.OpenGL.ClearBuffer.Color, 0, [0f, 0f, 0f, 1f]);
        OpenTK.Graphics.OpenGL.GL.ClearNamedFramebuffer(0, OpenTK.Graphics.OpenGL.ClearBufferCombined.DepthStencil, 0, 1f, 0);
    }

    // The Ortho stage (the GUI): textures made in it reach OpenGL whole; stranded, OpenGL draws it
    private static void OrthoBegins()
    {
        GlTap.GuiStage = true;
        _ = Assert(Renderers.Count > 0) && Assert(GlTap.Gui >= 0);
        if (Bridging && State == Phase.Running && _renderer is not null && _api?.Render is { } render)
            Guarded(static r => _renderer?.BridgeScene(r.CameraMatrixOriginf, r.PerspectiveProjectionMat), render);
    }

    private static void OrthoEnds()
    {
        GlTap.GuiStage = false;
        _ = Assert(Renderers.Count > 0) && Assert(GlTap.Gui >= 0);
    }

    // Cairo's textures (dialogs, text) are the GUI's, made outside the Ortho stage too
    private static void CairoMaking()
    {
        GlTap.Gui++;
        _ = Assert(GlTap.Gui > 0) && Assert(GlTap.Gui < 1 << 16);
    }

    private static void CairoMade()
    {
        GlTap.Gui = Math.Max(GlTap.Gui - 1, 0);
        _ = Assert(GlTap.Gui >= 0) && Assert(GlTap.Gui < 1 << 16);
    }

    // In the world GL runs no Komet GPU work: timer queries (RenderCost, GpuStats), options blur stay off
    public static bool GlIdle => State == Phase.Running && OnlyOn && _renderer is not null;

    // Tapped at mod start, before the engine compiles shaders and fills pools: the record hears them made
    private static void Recorded()
    {
        if (!OnlyOn || !Assert(!GlTap.Tapped)) return;
        if (!Current())
        {
            // The record's seed and the device pick would ask GL with no context: the first frame taps instead, and
            // earlier links/uploads are read back from GL on first use
            _logger?.Warning("Komet: no OpenGL context is current as the mod starts: Vulkan-only begins with the first frame");
            return;
        }

        if (!Tapped())
        {
            _logger?.Warning("Komet: the Vulkan-only record could not tap OpenGL ({0})", GlTap.Missing);
            return;
        }

        _ = Assert(!GlTap.Tapped || GlTap.Recording || !GlTap.Scene);
        Guarded(Started);
    }

    private static unsafe bool Current() =>
        Assert(OnlyOn) && OpenTK.Windowing.GraphicsLibraryFramework.GLFW.GetCurrentContext() != null;

    // Device and renderer from mod start, the scene hearing every write: uploads as the world joins go to Vulkan
    // directly (before the first frame TextureImages batches them on the device)
    private static void Started()
    {
        if (!VulkanCore.Enabled || !Assert(_renderer is null) || !Assert(GlTap.Recording)) return;
        VulkanCore.Start();
        if (VulkanCore.Device is not { } device || Made(device) is not { } renderer) return;
        _renderer = renderer;
        _scene ??= SceneRenderer.Create(renderer, _logger);
        if (_scene is not { } scene) return;
        (GlTap.Writes, GlTap.ProgramGone, GlTap.Linking) = (scene, Deleted, Linked);
        GlTap.Lean = LeanOn; // from now on, as Vulkan holds every upload
        Decide(device); // the passes taken: pools made before the first frame are adopted as they are made
        // programs linked before this (the sky, particles, entities: 30 ports of up to 27 ms on the engine's thread at
        // their first draw otherwise) begin their ports now, off the engine's thread
        foreach (var (id, name) in ProgramPorts.Linked().Bounded(1 << 10))
            if (Array.FindIndex(Programs, p => p.Name == name) < 0)
                scene.Prewarm(id, name);
        _logger?.Notification("Komet: Vulkan-only takes the world's uploads from the start on {0}", device.Name);
    }

    // As GL takes the world over, what only Vulkan drew (map tiles, icons, mods' framebuffers, distant shadow map) is
    // written into GL textures once, so GL does not draw them blank or stale. Failure here is logged, not the switch's.
    private static void HandBack(TerrainRenderer renderer)
    {
        if (!NotNull(renderer) || GlTap.Stranded || !Assert(!GlTap.Resting || OnlyOn)) return;
        try
        {
            var (count, bytes, left) = renderer.HandBack();
            if (count + left > 0)
                _logger?.Notification("Komet: Vulkan-only hands the world to OpenGL: {0} textures only Vulkan drew ({1} MB) " +
                                      "written back into OpenGL's{2}", count, bytes >> 20,
                    left > 0 ? $", {left} left as OpenGL last had them (too many, too large, or of a format read inexactly)" : "");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger?.Warning("Komet: Vulkan-only could not hand its textures to OpenGL: {0}", e.Message);
        }
    }

    // A renderer under a record with a gap (stale images/buffers) goes; the next is new
    private static void Remade()
    {
        if (_renderer is not { } renderer || !Assert(renderer.RecordTaps <= GlTap.Taps)) return;
        if (renderer.RecordTaps == GlTap.Taps) return;
        _logger?.Notification("Komet: Vulkan makes its renderer anew: the record has a gap");
        Release();
        _ = Assert(_renderer is null);
    }

    // A program linked under the record is ported at once while the world loads, so its first draw finds the port:
    // terrain programs for their pass (with hot uniforms), others for the scene
    private static void Linked(uint program)
    {
        if (!OnlyOn || _scene is not { } scene || !Assert(program is > 0 and < int.MaxValue)) return;
        var name = ProgramPorts.Name((int)program);
        for (var i = 0; i < Programs.Length; i++)
            if (Programs[i].Name == name)
            {
                Port(i, (int)program, _renderer);
                return;
            }

        scene.Prewarm((int)program, name);
        _ = Assert(Programs.Length == Built.Length);
    }

    // AllocateEmptyMesh's postfix: mesh written via persistent maps, pointers back to GL's as Vulkan-only ends
    private static void MeshAllocated(MeshRef __result)
    {
        if (__result is VAO { Persistent: true } vao && _renderer?.Maps is { } maps && Assert(vao.VaoId >= 0))
            maps.Register(vao);
    }
}
