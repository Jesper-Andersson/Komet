using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// A map tile written as FinishSetChunks writes it - each piece uploaded to a 32x32 texture and drawn into its cell with the game's
// texture2texture shaders, its quad and the state RenderTextureIntoFrameBuffer sets (the framebuffer's viewport, standard blending,
// the alpha test) - holds, texel for texel, what MapTileUpload's glTexSubImage2D into the cell leaves: on an empty tile and over
// pieces drawn before.
[NonParallelizable]
public sealed class MapTileUploadGpuTests
{
    private const int Side = 32, Tile = 96;

    [Test]
    public void TheUploadLeavesTheTileTheEnginesDrawLeaves()
    {
        GameInstall.RequireAssets();
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here: " + GlContext.Why);
        var random = new Random(11);
        int[]?[] first = [Piece(random), null, Piece(random), null, Piece(random), null, null, Piece(random), null];
        int[]?[] second = [Piece(random), Piece(random), null, null, Piece(random), Piece(random), Piece(random), null, null];
        var (drawn, uploaded) = (Texture(), Texture());
        var engine = Engine();
        try
        {
            foreach (var pieces in new[] { first, second })
            {
                engine.Draw(drawn, pieces);
                Upload(uploaded, pieces);
                Assert.That(Read(uploaded), Is.EqualTo(Read(drawn)));
            }
        }
        finally
        {
            engine.Dispose();
            GL.DeleteTexture(drawn);
            GL.DeleteTexture(uploaded);
        }
    }

    private static int[] Piece(Random random) =>
        [.. Enumerable.Range(0, Side * Side).Select(_ => unchecked((int)0xFF000000) | random.Next(1 << 24))];

    // As LoadOrUpdateTextureFromRgba makes it from the empty pixels
    private static int Texture()
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, Tile, Tile, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, new int[Tile * Tile]);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)All.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)All.Nearest);
        return texture;
    }

    private static void Upload(int texture, int[]?[] pieces)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        for (var i = 0; i < pieces.Length; i++)
            if (pieces[i] is { } pixels)
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, Side * (i % 3), Side * (i / 3), Side, Side, PixelFormat.Rgba,
                    PixelType.UnsignedByte, pixels);
    }

    private static int[] Read(int texture)
    {
        var texels = new int[Tile * Tile];
        GL.GetTextureImage(texture, 0, PixelFormat.Rgba, PixelType.UnsignedByte, texels.Length * 4, texels);
        return texels;
    }

    private static Drawer Engine()
    {
        var shaders = Path.Combine(GameInstall.Assets, "game", "shaders");
        var program = Parity.Linked(File.ReadAllText(Path.Combine(shaders, "texture2texture.vsh")),
            File.ReadAllText(Path.Combine(shaders, "texture2texture.fsh")));
        // QuadMeshUtilExt.GetQuadModelData: position, then uv, per corner
        float[] corners = [-1, -1, 0, 0, 0, 1, -1, 0, 1, 0, 1, 1, 0, 1, 1, -1, 1, 0, 0, 1];
        int[] indices = [0, 1, 2, 0, 2, 3];
        var (array, vertices, elements) = (GL.GenVertexArray(), GL.GenBuffer(), GL.GenBuffer());
        GL.BindVertexArray(array);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertices);
        GL.BufferData(BufferTarget.ArrayBuffer, corners.Length * 4, corners, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 20, 0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 20, 12);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, elements);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * 4, indices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
        return new Drawer(program, array, [vertices, elements]);
    }

    private sealed class Drawer(int program, int array, int[] buffers) : IDisposable
    {
        public void Draw(int tile, int[]?[] pieces)
        {
            var (framebuffer, piece) = (GL.GenFramebuffer(), GL.GenTexture());
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, tile, 0);
            GL.Viewport(0, 0, Tile, Tile);
            GL.BindTexture(TextureTarget.Texture2D, piece);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, Side, Side, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)All.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)All.Nearest);
            GL.Disable(EnableCap.DepthTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.UseProgram(program);
            GL.BindVertexArray(array);
            for (var i = 0; i < pieces.Length; i++)
            {
                if (pieces[i] is not { } pixels) continue;
                GL.BindTexture(TextureTarget.Texture2D, piece);
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Side, Side, PixelFormat.Rgba, PixelType.UnsignedByte,
                    pixels);
                Uniform("texu", 0);
                Uniform("texv", 0);
                Uniform("texw", 1);
                Uniform("texh", 1);
                Uniform("alphaTest", 0.005f);
                Uniform("xs", Side * (i % 3) / (float)Tile);
                Uniform("ys", Side * (i / 3) / (float)Tile);
                Uniform("width", Side / (float)Tile);
                Uniform("height", Side / (float)Tile);
                GL.Uniform1(GL.GetUniformLocation(program, "tex2d"), 0);
                GL.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.Disable(EnableCap.Blend);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            GL.DeleteTexture(piece);
            GL.Finish();
        }

        private void Uniform(string name, float value) => GL.Uniform1(GL.GetUniformLocation(program, name), value);

        public void Dispose()
        {
            GL.DeleteProgram(program);
            GL.DeleteVertexArray(array);
            GL.DeleteBuffers(buffers.Length, buffers);
        }
    }
}
