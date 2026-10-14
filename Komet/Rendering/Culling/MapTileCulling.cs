using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// ChunkMapLayer (essentials) draws every map tile it holds while a map shows, minimap included: at VD 1536 a thousand
// 96x96 tiles a frame, all but a dozen outside the scissor box. Under Vulkan each sampled tile needs an image of its
// own, so world join fell to a few FPS. The prefix skips tiles whose rectangle (placed as
// MultiChunkMapComponent.Render does) lies wholly outside the map's inner bounds (PushScissor's box), a pixel to spare
// for rounding. Skipped draws leave the gui shader's uniforms as the last drawn tile did; later layers set their own.
internal static class MapTileCulling
{
    // EngineShape of Seams() in 1.22.7
    internal const ulong Fingerprint = 0xF2927D95A8831334UL;

    private const string Component = "Vintagestory.GameContent.MultiChunkMapComponent",
        Map = "Vintagestory.GameContent.GuiElementMap";
    private const double Slack = 1;

    private static readonly SeamGuard Guard = new(nameof(MapTileCulling), PatchKinds.Replacing, [typeof(MapTileCulling)]);
    private static AccessTools.FieldRef<object, Cuboidd>? _view;
    private static AccessTools.FieldRef<object, float>? _zoom;

    public static bool Enabled { get; set; } = true;

    // Unverified body, essentials missing or another mod's patch: every tile drawn
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 1) || seams[0] is null ||
            AccessTools.TypeByName(Map) is not { } map || !Guard.Match(seams, fingerprint)) return;
        (_view, _zoom) = (AccessTools.FieldRefAccess<Cuboidd>(map, "CurrentBlockViewBounds"),
            AccessTools.FieldRefAccess<float>(map, "ZoomLevel"));
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Render)));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        var (type, map) = (AccessTools.TypeByName(Component), AccessTools.TypeByName(Map));
        MethodBase?[] seams =
            [type is null || map is null ? null : AccessTools.DeclaredMethod(type, "Render", [map, typeof(float)])];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Render(GuiElement map, LoadedTexture? ___Texture, Vec3d? ___worldPos)
    {
        if (!Enabled || Blocked || _view is null || _zoom is null || ___Texture is null || ___worldPos is null ||
            map?.Bounds is not { } bounds || _view(map) is not { } view) return true;
        var (width, height, zoom) = (view.X2 - view.X1, view.Z2 - view.Z1, _zoom(map));
        if (!double.IsFinite(width + height + zoom + bounds.renderX + bounds.renderY) || !(width > 0 && height > 0)) return true;
        var x = (int)(bounds.renderX + (float)((___worldPos.X - view.X1) / width * bounds.InnerWidth));
        var y = (int)(bounds.renderY + (float)((___worldPos.Z - view.Z1) / height * bounds.InnerHeight));
        var (right, bottom) = (x + (int)(___Texture.Width * zoom), y + (int)(___Texture.Height * zoom));
        _ = Assert(right >= x) && Assert(bottom >= y);
        return right >= bounds.renderX - Slack && x <= bounds.renderX + bounds.InnerWidth + Slack &&
               bottom >= bounds.renderY - Slack && y <= bounds.renderY + bounds.InnerHeight + Slack;
    }
}
