namespace Komet.Vulkan;

// The window as an FBO Vulkan stands for (Window, 0: none): framebuffer 0 and GL_BACK/GL_FRONT name it, so the
// engine's last passes and GUI draw into Vulkan's images, which Vulkan's swapchain shows.
internal static unsafe partial class GlTap
{
    private const uint GlReadFramebuffer = 0x8CA8, GlBackLeft = 0x402, GlFront = 0x404, GlFrontLeft = 0x400;
    private const uint GlFrontAndBack = 0x408, GlBackBuffer = 0x405;

    private static uint _readFramebuffer;

    // The stand-in framebuffer object; set and cleared by its owner (VulkanRenderer's terrain renderer)
    public static uint Window { get; set; }

    public static int ReadFramebuffer => (int)_readFramebuffer;

    private static uint Framed(uint framebuffer) =>
        Assert(framebuffer != uint.MaxValue) && framebuffer == 0 && Window != 0 ? Window : framebuffer;

    private static uint Windowed(uint buffer)
    {
        _ = Assert(Window != 0) && Assert(buffer != uint.MaxValue);
        return buffer is GlBackLeft or GlFront or GlBackBuffer or GlFrontLeft or GlFrontAndBack ? GlColor0 : buffer;
    }
}
