using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using OpenTK.Graphics.OpenGL;

namespace Komet.Rendering;

// A map tile (MultiChunkMapComponent, essentials) is a 96x96 texture of 3x3 chunk pieces. FinishSetChunks
// (render-thread tick) uploads each piece to a shared 32x32 texture and draws it into the tile via its own framebuffer,
// rebinding the primary each time: up to 200 pieces a tick while streaming, a quarter of the render thread
// (tick-gtentity), each draw its own render pass (and under Vulkan its own segment). The draw is a texel-exact copy
// (UVs follow corners, v = 0 at the bottom like the upload's first row; cell at (32 * dx, 32 * dz); nearest at scale 1,
// all-opaque pixels pass alpha test and blend to source). The prefix uploads such pieces straight into their cell
// (glTexSubImage2D, same format), then mipmaps as the engine; any non-opaque or non-32x32 piece leaves the whole tile
// to the engine. Skipped: the framebuffer, draws and the state they leave (primary bound, depth test and blending on),
// which every later render stage sets itself.
internal static class MapTileUpload
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0xAEBCE3250C8B0D68UL;

    private const string Component = "Vintagestory.GameContent.MultiChunkMapComponent";
    private const int Side = 32, Cells = 3, Tile = Side * Cells, Opaque = unchecked((int)0xFF000000);

    private static readonly SeamGuard Guard = new(nameof(MapTileUpload), PatchKinds.Replacing, [typeof(MapTileUpload)]);

    public static bool Enabled { get; set; } = true;

    // Unverified body, essentials missing or another mod's patch: engine draws every piece
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 1) || seams[0] is null || !Guard.Match(seams, fingerprint)) return;
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Finish)));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        var type = AccessTools.TypeByName(Component);
        MethodBase?[] seams = [type is null ? null : AccessTools.DeclaredMethod(type, "FinishSetChunks", [])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Finish(ref LoadedTexture? ___Texture, ref int[]?[]? ___pixelsToSet, int[]? ___emptyPixels,
        ICoreClientAPI? ___capi)
    {
        if (!Enabled || Blocked || ___pixelsToSet is not { Length: Cells * Cells } pieces || ___capi?.Render is not { } render ||
            !Uploadable(pieces)) return true;
        if (___Texture is null || ___Texture.Disposed)
        {
            if (___emptyPixels is null) return true;
            ___Texture = new LoadedTexture(___capi, 0, Tile, Tile);
            render.LoadOrUpdateTextureFromRgba(___emptyPixels, false, 0, ref ___Texture);
        }

        if (!Assert(___Texture.Width == Tile) || !Assert(___Texture.Height == Tile)) return true;
        render.BindTexture2d(___Texture.TextureId);
        for (var i = 0; i < Cells * Cells; i++)
            if (pieces[i] is { } pixels)
            {
                GL.TexSubImage2D(TextureTarget.Texture2D, 0, Side * (i % Cells), Side * (i / Cells), Side, Side,
                    PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
                World.MapPixels.Release(pixels); // OpenGL copied it
            }
        render.GlGenerateTex2DMipmaps();
        ___pixelsToSet = null;
        return false;
    }

    internal static bool Uploadable(int[]?[] pieces)
    {
        for (var i = 0; i < Math.Min(pieces.Length, Cells * Cells); i++)
        {
            if (pieces[i] is not { } pixels) continue;
            if (pixels.Length != Side * Side) return false;
            for (var p = 0; p < Side * Side; p++)
                if ((pixels[p] & Opaque) != Opaque)
                    return false;
        }

        return true;
    }
}
