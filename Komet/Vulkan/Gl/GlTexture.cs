using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// What GL holds of a texture (target, size, format, levels, sampling). Each query waits for Mesa's glthread: asked once
// on takeover (framebuffer adopted, copy made), never per draw. Under GlTap's record the record answers.
internal static class GlTexture
{
    public const int Array2D = 0x8C1A;
    private const int MaxLevels = 16, TextureTarget = 0x1006, TextureWidth = 0x1000, TextureHeight = 0x1001;
    private const int TextureDepth = 0x8071, InternalFormat = 0x1003, MaxLevel = 0x813D, CompareMode = 0x884C;
    private const int BorderColor = 0x1004;

    public readonly record struct Description(int Kind, int Width, int Height, int Depth, int Format);

    public static Description Of(int texture)
    {
        if (!Assert(texture > 0) || !Assert(texture < int.MaxValue)) return default;
        if (GlTap.Described(texture) is { } recorded) return recorded;
        GL.GetTextureParameter(texture, (GetTextureParameter)TextureTarget, out int target);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureWidth, out int width);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureHeight, out int height);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureDepth, out int depth);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)InternalFormat, out int format);
        return new Description(target, width, height, depth, format);
    }

    // The levels that exist from level 0 on, up to GL_TEXTURE_MAX_LEVEL
    public static int Levels(int texture)
    {
        if (GlTap.LevelsOf(texture) is { } recorded) return recorded;
        GL.GetTextureParameter(texture, (GetTextureParameter)MaxLevel, out int max);
        var levels = 1;
        for (var level = 1; level < Math.Min(MaxLevels, max + 1); level++)
        {
            GL.GetTextureLevelParameter(texture, level, (GetTextureParameter)TextureWidth, out int width);
            if (width <= 0) break;
            levels++;
        }

        _ = Assert(texture > 0) && Assert(levels <= MaxLevels);
        return levels;
    }

    public static Samplers.State Sampling(int texture)
    {
        if (!Assert(texture > 0) || !Assert(texture < int.MaxValue)) return default;
        if (GlTap.SamplingOf(texture) is { } recorded) return recorded;
        GL.GetTextureParameter(texture, GetTextureParameter.TextureMinFilter, out int min);
        GL.GetTextureParameter(texture, GetTextureParameter.TextureMagFilter, out int mag);
        GL.GetTextureParameter(texture, GetTextureParameter.TextureWrapS, out int wrap);
        GL.GetTextureParameter(texture, (GetTextureParameter)CompareMode, out int compare);
        var border = new float[4];
        GL.GetTextureParameter(texture, (GetTextureParameter)BorderColor, border);
        return Samplers.FromGl(min, mag, wrap, compare != 0) with { WhiteBorder = border[0] > 0.5f };
    }

    // One integer parameter (GL_TEXTURE_MIN_FILTER...): the record's where it follows it
    public static int Parameter(int texture, int name)
    {
        if (!Assert(texture > 0) || !Assert(name > 0)) return 0;
        if (GlTap.ParameterOf(texture, name) is { } recorded) return recorded;
        GL.GetTextureParameter(texture, (GetTextureParameter)name, out int value);
        return value;
    }

    public static float[] Border(int texture)
    {
        if (!Assert(texture > 0)) return new float[4];
        if (GlTap.BorderOf(texture) is { } recorded) return recorded;
        var border = new float[4];
        GL.GetTextureParameter(texture, (GetTextureParameter)BorderColor, border);
        _ = Finite(border[0]);
        return border;
    }

    public static bool Exists(int texture) =>
        Assert(texture >= 0) && (GlTap.TextureExists(texture) ?? GL.IsTexture(texture));
}
