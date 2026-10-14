namespace Komet.Vulkan;

// A framebuffer on Vulkan's images stands in for GL's window, so the final blit, AfterBlit, GUI and Done are the scene's
// and the frame ends at the swap, shown by Vulkan's swapchain.
internal static partial class VulkanRenderer
{
    private static bool _windowing; // the stand-in is in place this frame

    private static (IReadOnlyList<FrameBufferRef> Framebuffers, int[] Shared) Windowing(TerrainRenderer renderer,
        IRenderAPI render)
    {
        var shared = Shared();
        _windowing = SceneOn && GlTap.Scene; // only Vulkan's swapchain shows its frames
        _ = Assert(shared.Length <= EngineFramebuffers) && Assert(!_windowing || GlTap.Tapped);
        if (!NotNull(renderer) || !NotNull(render)) return (render?.FrameBuffers ?? [], shared);
        if (!_windowing || renderer.Window(render.FrameWidth, render.FrameHeight) is not { } window)
        {
            renderer.Unwindowed();
            _windowing = false;
            return (render.FrameBuffers, shared);
        }

        var engine = render.FrameBuffers;
        WindowFramebuffers.Clear();
        for (var i = 0; i < Math.Min(engine.Count, 63); i++) WindowFramebuffers.Add(engine[i]);
        WindowFramebuffers.Add(window);
        var at = WindowFramebuffers.Count - 1;
        if (!ReferenceEquals(_windowShared.Of, shared) || _windowShared.Made[^1] != at)
            _windowShared = (shared, [.. shared, at]);
        return (WindowFramebuffers, _windowShared.Made);
    }

    // Every frame the same list and array while nothing changes: Start takes them for the frame only
    private static readonly List<FrameBufferRef> WindowFramebuffers = [];
    private static (int[]? Of, int[] Made) _windowShared = (null, [-1]);
}
