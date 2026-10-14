using Komet.Vulkan;

namespace Komet.Rendering;

// GL state a skipped draw leaves (AnimatableCulling, PotCulling), set once per run of skips: GlTap bumps Version on
// every state call, so an unchanged version needs no GL calls. Untapped: every call made.
internal static class LeftState
{
    // AnimatableCulling's with/without face culling; PotCulling's: culling off, standard blending
    public enum Kind
    {
        None,
        Culling,
        Unculled,
        Pot
    }

    private static long _version = -1;
    private static Kind _state;

    public static bool Holds(Kind state) =>
        Assert(state != Kind.None) && GlTap.Tapped && _state == state && GlTap.Version == _version;

    public static void Left(Kind state)
    {
        (_state, _version) = (state, GlTap.Tapped ? GlTap.Version : -1);
        _ = Assert(state != Kind.None);
    }
}
