using Komet.Chunks;
using Komet.Rendering;
using Komet.Shapes;

namespace Komet.Vulkan;

// Komet's largest holdings next to the Vulkan memory report: does the managed heap grow with Komet or the game. Bytes
// are estimates (entry sizes); chunk lookups are the engine's chunks.
internal static partial class VulkanRenderer
{
    private const int FaceGroupBytes = 280; // a FaceSorting.Sorted with its four arrays and its entry

    internal static string Held()
    {
        var (mirrors, mirrorBytes) = _scene?.Mirrors is { } m ? (m.Count, m.ManagedBytes) : (0, 0);
        var tap = GlTap.Held;
        _ = Assert(mirrors >= 0) && Assert(tap.Bytes >= 0);
        return $"Komet holds {mirrors} buffer mirrors " +
               $"({mirrorBytes >> 10} KB managed), the tap's {tap.Programs} programs, {tap.Arrays} vertex arrays, " +
               $"{tap.Textures} texture targets, {tap.Framebuffers} framebuffers ({tap.Bytes >> 10} KB), " +
               $"{FaceSorting.Located} face groups ({(long)FaceSorting.Located * FaceGroupBytes >> 10} KB), " +
               $"{ChunkLookup.Count} chunk lookups, {AnimationFrames.Count} animation sets" + Record();
    }

    // The record: what it follows, and how often it had to ask OpenGL for an object made before it
    private static string Record()
    {
        if (!GlTap.Recording || !Assert(GlTap.Tapped)) return "";
        var held = GlTap.Records;
        _ = Assert(held.Misses >= 0);
        return $", the Vulkan-only record of {held.Textures} textures, {held.Framebuffers} framebuffers, " +
               $"{held.Programs} programs ({held.Linked} linked under it), {held.Buffers} buffer sizes " +
               $"({held.Misses} objects asked from OpenGL)";
    }
}
