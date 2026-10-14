using Komet.Gpu;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

public sealed class DepthPyramidGpuTests
{
    private const int Size = 66; // level 0 is 33 square, level 1 16: an odd level whose last row the mip chain's halving leaves over

    // Far only in the depth buffer's top row: every level's farthest must still reach the 1x1 level, or the screen's edge reads as
    // nearer than it is and what shows there is hidden
    [Test]
    public void TheRowsAnOddLevelLeavesOverReachTheTop()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here: " + GlContext.Why);
        GpuBackends.Current = GlBackend.Instance;
        GL.GenTextures(1, out int depth);
        GL.BindTexture(TextureTarget.Texture2D, depth);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, Size, Size, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.GenFramebuffers(1, out int fbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, depth, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ClearDepth(0.5);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(0, Size - 1, Size, 1);
        GL.ClearDepth(1);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.ScissorTest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        var pyramid = new DepthPyramid();
        try
        {
            Assert.That(pyramid.Ready(new CapturingLogger(), GlBackend.Instance), Is.True, "hiz.comp compiles");
            _ = pyramid.Build(depth, Size, Size);
            GL.MemoryBarrier(MemoryBarrierFlags.TextureUpdateBarrierBit);
            var top = new float[1];
            GL.GetTextureImage(pyramid.Texture, pyramid.Levels - 1, PixelFormat.Red, PixelType.Float, 4, top);
            Assert.That(top[0], Is.EqualTo(1f), "the 1x1 level holds the far top row");
        }
        finally
        {
            pyramid.Release();
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(depth);
        }
    }
}
