using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;

namespace Komet.Options;

// Blurred at quarter size (1/16 of the pixels per pass), so it runs every frame; the last pass flips rows upright.
internal sealed class Backdrop(ICoreClientAPI capi) : IDisposable
{
    private const int Shrink = 4, Rounds = 2, Buffers = 2, MaxSize = 16384;

    private const string Vertex = """
        #version 330 core
        out vec2 uv;
        void main()
        {
            vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            uv = corner;
            gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    // shrink: four bilinear fetches average a 4x4 block; else nine Gaussian taps (sigma 2) as five bilinear fetches
    private const string Fragment = """
        #version 330 core
        in vec2 uv;
        out vec4 color;
        uniform sampler2D source;
        uniform vec2 stride;
        uniform int shrink;
        uniform int flip;
        void main()
        {
            vec2 at = flip == 1 ? vec2(uv.x, 1.0 - uv.y) : uv;
            vec3 sum;
            if (shrink == 1)
            {
                sum = (texture(source, at + stride * vec2(-1.0, -1.0)).rgb + texture(source, at + stride * vec2(1.0, -1.0)).rgb
                    + texture(source, at + stride * vec2(-1.0, 1.0)).rgb + texture(source, at + stride * vec2(1.0, 1.0)).rgb) * 0.25;
            }
            else
            {
                sum = texture(source, at).rgb * 0.2270270270;
                sum += (texture(source, at + stride * 1.3846153846).rgb + texture(source, at - stride * 1.3846153846).rgb) * 0.3162162162;
                sum += (texture(source, at + stride * 3.2307692308).rgb + texture(source, at - stride * 3.2307692308).rgb) * 0.0702702703;
            }
            color = vec4(sum, 1.0);
        }
        """;

    private readonly int[] _small = new int[Buffers], _smallFb = new int[Buffers]; // the quarter picture, ping-ponged by the passes
    private readonly int[] _viewport = new int[4];
    private readonly Vec4f _tint = new(1, 1, 1, 1); // Render2DTexture reads it into a uniform during the call
    private int _program, _vao, _full, _fullFb, _source, _step, _shrink, _flip;
    private (int Width, int Height) _size;
    private bool _failed;

    public static bool Enabled { get; set; } = true;

    public void Dispose()
    {
        Release();
        _ = Assert(_size == default);
        if (_program != 0) GL.DeleteProgram(_program);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        (_program, _vao) = (0, 0);
    }

    // Ahead of the first opening, whose frame would otherwise pay
    public void Prepare()
    {
        var (width, height) = (capi.Render.FrameWidth, capi.Render.FrameHeight);
        if (!Enabled || _failed || width < Shrink || height < Shrink || !Assert(width <= MaxSize && height <= MaxSize)) return;
        if (_program == 0 && !Build()) return;
        if (_size != (width, height)) _ = Targets(width, height);
    }

    // amount 0..1: blur spread and opacity grow together; false when none. Under Vulkan-only GlTap routes it to the
    // scene.
    public bool Draw(float z, double amount)
    {
        if (!Enabled || _failed || !Finite(z) || !Finite(amount) || amount <= 0) return false;
        amount = Math.Min(1, amount);
        var viewport = _viewport;
        GL.GetInteger(GetPName.Viewport, viewport);
        var (width, height) = (viewport[2], viewport[3]);
        if (width < Shrink || height < Shrink || !Assert(width <= MaxSize && height <= MaxSize)) return false;
        if (_program == 0 && !Build()) return false;
        if (_size != (width, height) && !Targets(width, height)) return false;

        GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFb);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out int readFb);
        GL.GetInteger(GetPName.CurrentProgram, out int program);
        GL.GetInteger(GetPName.VertexArrayBinding, out int vao);
        GL.GetInteger(GetPName.ActiveTexture, out int unit);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int texture);
        var (blend, scissor) = (GL.IsEnabled(EnableCap.Blend), GL.IsEnabled(EnableCap.ScissorTest));
        try
        {
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, drawFb);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fullFb);
            GL.BlitFramebuffer(viewport[0], viewport[1], viewport[0] + width, viewport[1] + height, 0, 0, width, height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            Passes(width, height, (float)amount);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFb);
            GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
            GL.UseProgram(program);
            GL.BindVertexArray(vao);
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.ActiveTexture((TextureUnit)unit);
            if (blend) GL.Enable(EnableCap.Blend);
            if (scissor) GL.Enable(EnableCap.ScissorTest);
        }

        _tint.W = (float)amount;
        capi.Render.Render2DTexture(_small[0], 0, 0, capi.Render.FrameWidth, capi.Render.FrameHeight, z, _tint);
        return true;
    }

    private void Passes(int width, int height, float amount)
    {
        var (w, h) = (width / Shrink, height / Shrink);
        if (!Assert(w > 0 && h > 0) || !Finite(amount)) return;
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.Uniform1(_source, 0);
        GL.Viewport(0, 0, w, h);
        Pass(_full, _smallFb[0], 1f / width, 1f / height, true, false);
        for (var round = 0; round < Rounds; round++)
        {
            Pass(_small[0], _smallFb[1], amount / w, 0, false, false);
            Pass(_small[1], _smallFb[0], 0, amount / h, false, round == Rounds - 1);
        }
    }

    private void Pass(int source, int target, float x, float y, bool shrink, bool flip)
    {
        if (!Assert(source != 0 && target != 0)) return;
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target);
        GL.BindTexture(TextureTarget.Texture2D, source);
        GL.Uniform2(_step, x, y);
        GL.Uniform1(_shrink, shrink ? 1 : 0);
        GL.Uniform1(_flip, flip ? 1 : 0);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private bool Build()
    {
        if (!Assert(_program == 0)) return true;
        var (vertex, fragment) = (Stage(ShaderType.VertexShader, Vertex), Stage(ShaderType.FragmentShader, Fragment));
        if (vertex == 0 || fragment == 0) return Fail("a shader did not compile");
        _program = GL.CreateProgram();
        GL.AttachShader(_program, vertex);
        GL.AttachShader(_program, fragment);
        GL.LinkProgram(_program);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0) return Fail("the program did not link: " + GL.GetProgramInfoLog(_program));
        (_source, _step) = (GL.GetUniformLocation(_program, "source"), GL.GetUniformLocation(_program, "stride"));
        (_shrink, _flip) = (GL.GetUniformLocation(_program, "shrink"), GL.GetUniformLocation(_program, "flip"));
        _vao = GL.GenVertexArray();
        return Assert(_source >= 0 && _step >= 0) || Fail("the uniforms are missing");
    }

    private int Stage(ShaderType type, string source)
    {
        if (!NotNull(source) || !Assert(source.Length > 0)) return 0;
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled != 0) return shader;
        capi.Logger?.Warning("Komet options: the backdrop's {0} shader: {1}", type, GL.GetShaderInfoLog(shader));
        GL.DeleteShader(shader);
        return 0;
    }

    private bool Targets(int width, int height)
    {
        if (!Assert(width >= Shrink && height >= Shrink)) return false;
        Release();
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int drawFb);
        GL.GetInteger(GetPName.TextureBinding2D, out int texture);
        try
        {
            (_full, _fullFb) = Target(width, height);
            for (var i = 0; i < Buffers; i++) (_small[i], _smallFb[i]) = Target(width / Shrink, height / Shrink);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);
            GL.BindTexture(TextureTarget.Texture2D, texture);
        }

        if (_fullFb == 0 || Array.IndexOf(_smallFb, 0) >= 0) return Fail("a framebuffer is incomplete");
        _size = (width, height);
        return true;
    }

    private static (int Texture, int Framebuffer) Target(int width, int height)
    {
        if (!Assert(width > 0 && height > 0)) return (0, 0);
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        var framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            texture, 0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) == FramebufferErrorCode.FramebufferComplete)
            return (texture, framebuffer);
        GL.DeleteFramebuffer(framebuffer);
        GL.DeleteTexture(texture);
        return (0, 0);
    }

    private void Release()
    {
        if (_fullFb != 0) GL.DeleteFramebuffer(_fullFb);
        if (_full != 0) GL.DeleteTexture(_full);
        for (var i = 0; i < Buffers; i++)
        {
            if (_smallFb[i] != 0) GL.DeleteFramebuffer(_smallFb[i]);
            if (_small[i] != 0) GL.DeleteTexture(_small[i]);
            (_small[i], _smallFb[i]) = (0, 0);
        }

        (_full, _fullFb, _size) = (0, 0, default);
        _ = Assert(Array.TrueForAll(_small, t => t == 0));
    }

    private bool Fail(string why)
    {
        _failed = NotNull(why);
        capi.Logger?.Warning("Komet options: no blurred backdrop, {0}", why);
        return false;
    }
}
