using Komet.Diagnostics;
using Vintagestory.Client.NoObf;

namespace Komet.Core;

// ClientSettings' getters look their key up lower-cased, a new string per read: per-frame readers take one read per frame.
// Main thread only; before FrameClock counts (Frames 0) every read asks the engine. A change applies next frame.
internal static class FrameSettings
{
    private static (long Frame, int Value) _viewDistance, _vsync, _shadows, _fov;
    private static (long Frame, float Value) _ssaa;
    private static (long Frame, bool Value) _glDebug;

    public static int ViewDistance => Cached(ref _viewDistance, static () => ClientSettings.ViewDistance);
    public static int VsyncMode => Cached(ref _vsync, static () => ClientSettings.VsyncMode);
    public static int ShadowMapQuality => Cached(ref _shadows, static () => ClientSettings.ShadowMapQuality);
    public static int FieldOfView => Cached(ref _fov, static () => ClientSettings.FieldOfView);
    public static float Ssaa => Cached(ref _ssaa, static () => ClientSettings.SSAA);
    public static bool GlDebugMode => Cached(ref _glDebug, static () => ClientSettings.GlDebugMode);

    private static T Cached<T>(ref (long Frame, T Value) slot, Func<T> read)
    {
        var frame = FrameClock.Frames;
        if (frame == 0 || slot.Frame != frame) slot = (frame, read());
        _ = Assert(slot.Frame == frame) && Assert(frame >= 0);
        return slot.Value;
    }
}
