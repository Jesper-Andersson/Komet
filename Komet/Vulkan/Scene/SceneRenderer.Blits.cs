namespace Komet.Vulkan;

// glBlitFramebuffer as vkCmdBlitImage in the open segment, colors, depth and stencil (OnlyBlitted). Both keep OpenGL's
// row order, so corners carry over; same-size blits are texel-exact, scaling nearest blits map pixel centres like OpenGL.
internal sealed partial class SceneRenderer
{
    private const uint ColorBlit = 0x4000, DepthBlit = 0x100, StencilBlit = 0x400, Nearest = 0x2600, Linear = 0x2601;

    private readonly List<SharedImage> _blitted = [];
    private readonly (SharedImage From, SharedImage To)[] _pairs = new (SharedImage, SharedImage)[GlTap.MaxBuffers + 1];

    public long Blits { get; private set; }

    public bool Blit(GlTap.BlitCall call)
    {
        if (!Framing || !Clearing() || !Assert(call.Read > 0 && call.Draw > 0)) return false;
        var why = OnlyBlitted(call);
        return why.Length == 0 || Leave("a blit " + why);
    }
}
