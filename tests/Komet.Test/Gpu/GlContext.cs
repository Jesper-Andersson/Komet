using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Test.Gpu;

// The GPU tests' OpenGL context, and the depth of a wall in front of its camera
internal static class GlContext
{
    internal const int Size = 64, WallDistance = 10;
    private static bool _resolving;

    // OpenTK asks for libglfw.so.3 by name, which the loader then finds among the libraries already in the process; the game ships it
    private static void Resolve()
    {
        if (_resolving) return;
        _resolving = true;
        _ = NativeLibrary.TryLoad(Path.Combine(GameInstall.Lib, "libglfw.so.3"), out _);
    }

    internal static string Why { get; private set; } = "";

    internal static NativeWindow? Context()
    {
        Resolve();
        GLFWProvider.CheckForMainThread = false; // NUnit runs tests on worker threads; the context stays on this one
        try
        {
            var window = new NativeWindow(new NativeWindowSettings
            {
                StartVisible = false, APIVersion = new Version(4, 6), Profile = ContextProfile.Compatability,
                ClientSize = new OpenTK.Mathematics.Vector2i(Size, Size)
            });
            window.MakeCurrent();
            return window;
        }
        catch (Exception e) when (e is GLFWException or DllNotFoundException or TypeInitializationException
                                      or InvalidOperationException)
        {
            Why = e.ToString();
            return null;
        }
    }

    internal static float[] Projection() => Mat4f.Perspective(new float[16], MathF.PI / 2, 1, 0.1f, 1000);

    internal static float Depth(float[] projection, float distance)
    {
        var clip = Mat4f.MulWithVec4(projection, [0, 0, -distance, 1]);
        return clip[2] / clip[3] * 0.5f + 0.5f;
    }

    // A depth texture as the engine makes the primary one, cleared to the wall's depth
    internal static int Wall(float depth)
    {
        GL.GenTextures(1, out int texture);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, Size, Size, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.GenFramebuffers(1, out int fbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, texture, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ClearDepth(depth);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        return texture;
    }
}
