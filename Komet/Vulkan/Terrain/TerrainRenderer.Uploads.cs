namespace Komet.Vulkan;

// What OpenGL is told about a texture's contents and parameters reaches its image (TextureImages)
internal sealed partial class TerrainRenderer
{
    // Anything else done to the texture: its image is read back on next use
    public void Changed(int texture)
    {
        if (Assert(texture >= 0) && NotNull(Images)) Images.Changed(texture);
    }

    // BASE_LEVEL, MAX_LEVEL and the sampling parameters
    public void Tuned(int texture, uint name, float value)
    {
        if (Assert(texture >= 0) && Finite(value)) Images.Tuned(texture, name, value);
    }

    // glGenerateMipmap: the image makes its levels too; false when impossible (the image is then changed)
    public bool Mipmapped(int texture) => Assert(texture >= 0) && NotNull(Images) && Images.Mipmapped(texture);
}
