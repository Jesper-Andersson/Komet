using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The cache answers the bindings and the viewport as the driver would: after binds by target, a viewport, and deleting the bound
// framebuffer (the window's in its place)
[NonParallelizable]
public sealed class GlQueryCacheGpuTests
{
    [Test]
    public void AnswersAsTheDriver()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here: " + GlContext.Why);
        GlQueryCache.Install();
        var (a, b) = (GL.GenFramebuffer(), GL.GenFramebuffer());
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, a);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, b);
        GL.Viewport(3, 4, 50, 60);
        var asked = Asked();
        GlQueryCache.Enabled = false;
        var driver = Asked();
        GlQueryCache.Enabled = true;
        GL.DeleteFramebuffer(a);
        var deleted = Asked();
        GL.DeleteFramebuffer(b);
        Assert.Multiple(() =>
        {
            Assert.That(asked, Is.EqualTo((a, b, (3, 4, 50, 60))));
            Assert.That(driver, Is.EqualTo(asked));
            Assert.That(deleted, Is.EqualTo((0, b, (3, 4, 50, 60))));
        });
    }

    private static (int Draw, int Read, (int, int, int, int) Viewport) Asked()
    {
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int read);
        var viewport = new int[4];
        GL.GetInteger(GetPName.Viewport, viewport);
        return (draw, read, (viewport[0], viewport[1], viewport[2], viewport[3]));
    }
}
