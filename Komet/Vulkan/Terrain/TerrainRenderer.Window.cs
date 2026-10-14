using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Vulkan;

// A window-sized framebuffer stands in for OpenGL's default one, so the last passes and GUI are Vulkan's too.
internal sealed partial class TerrainRenderer
{
    private FrameBufferRef? _window;
    private WindowSwapchain? _swapchain;
    private bool _noSwapchain;
    private FrameBridge? _bridge;
    private FrameBridge.Shot? _bridging; // this frame's scene copied, before the GUI
    private int _refreshHz;
    private long _refreshAsked; // Stopwatch ticks of the last look at the monitor's rate

    public long Bridged => _bridge?.Bridged ?? 0;

    // Vulkan-only, at the GUI's start (VulkanRenderer.Bridging): the window's picture and the scene's depth copied for
    // FrameBridge, with the camera they were drawn from
    public void BridgeScene(float[] view, double[] projection)
    {
        _bridging = null;
        if (_bridge is not { } bridge || !Frame.Open || _window is not { ColorTextureIds: [var color, ..] } ||
            Targets.Find(color) is not { } image || !Frame.Holds(image) || !NotNull(view) || !NotNull(projection)) return;
        var depth = Targets.Framebuffers is { Count: > 0 } framebuffers && framebuffers[0] is { DepthTextureId: > 0 } primary
            ? SampledFind(primary.DepthTextureId)
            : null;
        if (depth is not null && !Frame.Holds(depth)) depth = null;
        var slot = bridge.Take(image, depth);
        if (slot < 0 || !Frame.CopyRaw(image, bridge.Scene(slot))) return;
        var depthed = depth is not null && bridge.Depth(slot) != 0 && Frame.CopyRaw(depth, bridge.Depth(slot));
        _bridging = FrameBridge.Shoot(slot, view, projection, depthed);
    }

    // The bridge as VulkanRenderer.Bridging says, for the swapchain shown into
    private void Bridging(WindowSwapchain chain)
    {
        if (!NotNull(chain) || !Assert(_refreshHz >= 0)) return;
        if (!VulkanRenderer.Bridging)
        {
            Unbridged();
            return;
        }

        if (_bridge is not null) return;
        _bridge = FrameBridge.Create(Device, chain, _refreshHz, out var why);
        if (_bridge is null) _logger?.Warning("Komet: Vulkan cannot bridge late frames: {0}", why);
        else _logger?.Notification("Komet: Vulkan bridges frames the engine misses at {0} Hz", _refreshHz);
    }

    private void Unbridged()
    {
        if (_bridge is { Bridged: > 0 } bridged)
            _logger?.Notification("Komet: Vulkan bridged {0} frames the engine missed; {1} shown frames stepped against the turn",
                bridged.Bridged, bridged.Reversed);
        _bridge?.Dispose();
        (_bridge, _bridging) = (null, null);
        _ = Assert(_refreshHz >= 0) && Assert(Device.Handle != IntPtr.Zero);
    }

    public bool Showing { get; private set; }

    // Frames Vulkan could not show (the window kept the one before)
    public long Unshown { get; private set; }

    public FrameBufferRef? Window(int width, int height)
    {
        if (!Assert(width >= 0 && height >= 0) || !Assert(width <= 1 << 15 && height <= 1 << 15)) return null;
        if (_window is { } made && made.Width == width && made.Height == height) return made;
        if (_window is null && (width == 0 || height == 0)) return null; // minimized: nothing to mark each frame
        VulkanWatch.Mark($"window stand-in made at {width}x{height}");
        Unwindowed();
        if (width == 0 || height == 0) return null;
        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int color);
        GL.TextureStorage2D(color, 1, SizedInternalFormat.Rgba8, width, height);
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int depth);
        GL.TextureStorage2D(depth, 1, (SizedInternalFormat)0x8CAC, width, height); // GL_DEPTH_COMPONENT32F
        foreach (var texture in (ReadOnlySpan<int>)[color, depth])
        {
            GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }

        GL.CreateFramebuffers(1, out int framebuffer);
        GL.NamedFramebufferTexture(framebuffer, FramebufferAttachment.ColorAttachment0, color, 0);
        GL.NamedFramebufferTexture(framebuffer, FramebufferAttachment.DepthAttachment, depth, 0);
        GL.NamedFramebufferDrawBuffer(framebuffer, DrawBufferMode.ColorAttachment0);
        _window = new FrameBufferRef
        {
            FboId = framebuffer, ColorTextureIds = [color], DepthTextureId = depth, Width = width, Height = height
        };
        GlTap.Window = (uint)framebuffer;
        return Assert(framebuffer > 0) ? _window : null;
    }

    public void Unwindowed()
    {
        if (_window is not { } window || !Assert(window.FboId > 0) || !Assert(GlTap.Window == window.FboId)) return;
        Frame.Close("the window's stand-in goes");
        Unbridged(); // its thread presents into the swapchain
        _swapchain?.Dispose();
        (_swapchain, _window, Showing) = (null, null, false);
        ForgetTargets();
        _ = Device.WaitIdle();
        using var quiet = GlTap.Quietly();
        var bound = (GlTap.DrawFramebuffer, GlTap.ReadFramebuffer);
        GlTap.Window = 0;
        if (bound.DrawFramebuffer == window.FboId) GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        if (bound.ReadFramebuffer == window.FboId) GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Targets.Release(window);
        GL.DeleteFramebuffer(window.FboId);
        foreach (var texture in (window.ColorTextureIds ?? []).Bounded(4))
            if (texture > 0) GL.DeleteTexture(texture);
        if (window.DepthTextureId > 0) GL.DeleteTexture(window.DepthTextureId);
    }

    // True: Vulkan presented or dropped the frame (no segment left, no image, submit failed: the window keeps the last
    // one, swapchain kept), SwapBuffers must not run. False when Vulkan cannot show frames at all (Unshowable).
    public bool Present(NativeWindow? window)
    {
        if (_window is not { } stand || !Assert(stand.Width > 0) || !Assert(stand.Height > 0)) return false;
        VulkanWatch.Mark("presenting");
        var image = stand.ColorTextureIds is [var color, ..] ? Targets.Find(color) : null;
        var shown = window is not null && image is not null ? Shown(image, window) : Showed.Refused;
        Showing = shown == Showed.Shown;
        if (shown != Showed.Refused) return true;
        return Unshowing(_noSwapchain ? "Vulkan has no swapchain for the window" : "the window's image is not Vulkan's");
    }

    private enum Showed
    {
        Shown,
        Dropped, // not this frame (no segment, no image): the window keeps the last one, the next may be shown
        Refused // no swapchain for the window
    }

    // Segment opened before acquiring, as an acquired image must be presented; the frame keeps its last segment for
    // this
    private Showed Shown(SharedImage color, NativeWindow window)
    {
        if (Swapchain(window) is not { } chain || !NotNull(color) || !Assert(color.Width > 0)) return Showed.Refused;
        if (Frame.Open && !Frame.Holds(color)) Frame.Close("the window's stand-in is new");
        if (!Frame.Open && !Frame.Begin(out var refused)) return Dropped(refused);
        if (!Frame.Holds(color)) return Dropped("the segment does not hold the window's image");
        Retune(window);
        Bridging(chain);
        var index = chain.Acquire(color.Width, color.Height, (int)window.VSync, out var why);
        if (index < 0) return Dropped(why);
        var bridge = _bridge;
        var shot = _bridging is { } scene && bridge is not null && bridge.Final(scene.Slot) != 0 &&
                   Frame.CopyRaw(color, bridge.Final(scene.Slot))
            ? scene
            : (FrameBridge.Shot?)null; // the GUI drawn: the frame's own picture beside its scene
        _bridging = null;
        if (!Frame.Show(color, chain.Image(index), (chain.Width, chain.Height))) return Dropped("the frame did not show");
        Frame.Close("the frame shown by Vulkan", () => // presented on the recording thread, then published to the bridge
        {
            _ = chain.Present(index);
            bridge?.Shown(shot, chain.Presents);
        }, endsFrame: true);
        return Frame.Shown ? Showed.Shown : Dropped("the submission failed");
    }

    private Showed Dropped(string why)
    {
        Unshown++;
        if (!NotNull(why) || !Assert(why.Length > 0)) return Showed.Dropped;
        Frame.Close("a frame Vulkan could not show", endsFrame: true);
        if (Unshown <= MaxDropsLogged)
            _logger?.Warning("Komet: Vulkan could not show a frame, the window keeps the last one: {0}", why);
        return Showed.Dropped;
    }

    private const int MaxDropsLogged = 8;

    // Vulkan that cannot present: VulkanRenderer lets go and OpenGL (which got every upload) draws.
    public string Unshowable { get; private set; } = "";

    private bool Unshowing(string why)
    {
        Unshowable = NotNull(why) ? why : "?";
        Frame.Close("a frame Vulkan-only cannot show", endsFrame: true);
        return !Assert(Unshowable.Length > 0) && !Assert(Frame.Number >= 0);
    }

    private unsafe WindowSwapchain? Swapchain(NativeWindow window)
    {
        if (_swapchain is not null || _noSwapchain || !NotNull(window) || !Assert(Device.Handle != IntPtr.Zero))
            return _swapchain;
        var platform = GLFW.GetPlatform();
        WindowSwapchain.Native native = platform switch
        {
            Platform.Wayland => new(true, GLFW.GetWaylandDisplay(), (nuint)GLFW.GetWaylandWindow(window.WindowPtr)),
            Platform.X11 => new(false, GLFW.GetX11Display(), GLFW.GetX11Window(window.WindowPtr)),
            _ => default
        };
        _ = Assert(native.Wayland == (platform == Platform.Wayland)) && Assert(Device.Handle != IntPtr.Zero);
        _refreshHz = Refresh(window);
        _swapchain = WindowSwapchain.Create(Device, native, out var why);
        _noSwapchain = _swapchain is null;
        if (_noSwapchain)
            _logger?.Warning("Komet: Vulkan has no swapchain: {0} ({1}); it cannot run without one", why, platform);
        else _logger?.Notification("Komet: Vulkan shows the frames itself ({0})", platform);
        return _swapchain;
    }

    // Twice a second: the window's monitor and its rate again, as players move the window or change the mode; 0 (unknown)
    // keeps the last rate known
    private void Retune(NativeWindow window)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _refreshAsked < System.Diagnostics.Stopwatch.Frequency / 2 || !NotNull(window)) return;
        _refreshAsked = now;
        var hz = Refresh(window);
        if (hz <= 0 || hz == _refreshHz || !Assert(hz < 100000)) return;
        _refreshHz = hz;
        if (_bridge is not { } bridge) return;
        bridge.Retuned(hz);
        _logger?.Notification("Komet: Vulkan bridges frames the engine misses at {0} Hz now", hz);
    }

    // The refresh rate of the window's monitor: its own in fullscreen, else the one under the window's centre (borderless
    // windows have none of their own), else the primary one's; 0 where GLFW does not know it
    private static unsafe int Refresh(NativeWindow window)
    {
        if (!NotNull(window)) return 0;
        var monitor = GLFW.GetWindowMonitor(window.WindowPtr);
        if (monitor == null) monitor = Under(window);
        if (monitor == null) monitor = GLFW.GetPrimaryMonitor();
        var mode = monitor == null ? null : GLFW.GetVideoMode(monitor);
        return mode == null || !Assert(mode->RefreshRate >= 0) ? 0 : mode->RefreshRate;
    }

    private static unsafe OpenTK.Windowing.GraphicsLibraryFramework.Monitor* Under(NativeWindow window)
    {
        GLFW.GetWindowPos(window.WindowPtr, out var x, out var y);
        GLFW.GetWindowSize(window.WindowPtr, out var width, out var height);
        var (cx, cy) = (x + width / 2, y + height / 2);
        var monitors = GLFW.GetMonitorsRaw(out var count);
        for (var i = 0; monitors != null && i < Math.Min(count, 16); i++)
        {
            GLFW.GetMonitorPos(monitors[i], out var mx, out var my);
            var mode = GLFW.GetVideoMode(monitors[i]);
            if (mode != null && cx >= mx && cx < mx + mode->Width && cy >= my && cy < my + mode->Height) return monitors[i];
        }

        _ = Assert(count >= 0) && Assert(width >= 0);
        return null;
    }
}
