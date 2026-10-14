using Komet.Vulkan;

namespace Komet.Test.Gpu;

public sealed class OnlyBlitsTests
{
    [Test]
    public void ClippedCornersFollowAlongTheBlit()
    {
        Assert.That(SceneRenderer.Along((0, 10), (0, 10), (0, 100), (0, 100)), Is.EqualTo((0, 10, 0, 10)));
        // mirrored and scaled twice: the destination's half outside [0, 30) takes the source's first half with it
        Assert.That(SceneRenderer.Along((0, 10), (40, 20), (0, 100), (0, 30)), Is.EqualTo((5, 10, 30, 20)));
        Assert.That(SceneRenderer.Along((0, 10), (50, 60), (0, 100), (0, 30)), Is.Null, "nothing left inside");
    }
}
