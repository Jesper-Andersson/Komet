using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// What an FBO really has attached (texture, level, layer, layered), asked from GL: the engine does not always attach
// what its FrameBufferRef names (the transparent pass gets a reveal texture and array layers). Each query waits for
// Mesa's glthread: once per framebuffer and change. While GlTap records (VulkanRenderer.OnlyOn) the record answers.
internal static class GlFramebuffer
{
    public const int Colors = 8, Depth = Colors; // the depth's index in Attachments' result
    private const int GlTexture = 0x1702;

    public readonly record struct Attached(int Texture, int Level, int Layer, bool Layered);

    public static Attached[] Attachments(int fbo) =>
        Assert(fbo > 0) && Assert(fbo < int.MaxValue)
            ? GlTap.AttachmentsOf(fbo) ?? Asked(fbo)
            : new Attached[Colors + 1];

    public static Attached[] Asked(int fbo)
    {
        var attached = new Attached[Colors + 1];
        if (!Assert(fbo > 0) || !Assert(attached.Length == Colors + 1)) return attached;
        for (var i = 0; i <= Colors; i++)
        {
            var point = i == Depth ? FramebufferAttachment.DepthAttachment : FramebufferAttachment.ColorAttachment0 + i;
            if (Parameter(fbo, point, FramebufferParameterName.FramebufferAttachmentObjectType) != GlTexture) continue;
            attached[i] = new Attached(Parameter(fbo, point, FramebufferParameterName.FramebufferAttachmentObjectName),
                Parameter(fbo, point, FramebufferParameterName.FramebufferAttachmentTextureLevel),
                Parameter(fbo, point, FramebufferParameterName.FramebufferAttachmentTextureLayer),
                Parameter(fbo, point, FramebufferParameterName.FramebufferAttachmentLayered) != 0);
        }

        return attached;
    }

    private static int Parameter(int fbo, FramebufferAttachment point, FramebufferParameterName name)
    {
        _ = Assert(fbo > 0) && Assert(point >= FramebufferAttachment.ColorAttachment0 || point == FramebufferAttachment.DepthAttachment);
        GL.GetNamedFramebufferAttachmentParameter(fbo, point, name, out int value);
        return value;
    }
}
