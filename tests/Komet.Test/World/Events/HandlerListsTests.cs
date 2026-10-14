using Komet.World;

namespace Komet.Test.World;

[NonParallelizable]
public sealed class HandlerListsTests
{
    private static readonly string[] Expected = ["a", "b", "a", "b", "a", "b", "c"];

    [TearDown]
    public void Restore()
    {
        HandlerLists.Enabled = true;
        _ = HandlerLists.Clear();
    }

    [Test]
    public void Installs()
    {
        _ = typeof(Vintagestory.GameContent.RoomRegistry).Assembly; // Komet finds it by name, once the game has loaded VSEssentials
        using var harmony = new TestHarmony("komet-test-handlerlists");
        HandlerLists.Install(harmony);
        Assert.That(HandlerLists.Rewritten, Is.True,
            "TriggerOnGetClimate, TriggerOnGetWindSpeed, ParticlePhysics.UpdateMotion or RoomRegistry.Event_ChunkDirty changed");
    }

    [Test]
    public void AListIsMadeOncePerDelegate()
    {
        Action first = () => { }, second = () => { };
        var both = (Action)Delegate.Combine(first, second);
        var (a, b) = (HandlerLists.Of(both), HandlerLists.Of(both));
        var more = (Action)Delegate.Combine(both, first);
        var c = HandlerLists.Of(more);
        HandlerLists.Enabled = false;
        var (d, e) = (HandlerLists.Of(both), HandlerLists.Of(both));
        Assert.Multiple(() =>
        {
            Assert.That(b, Is.SameAs(a));
            Assert.That(a, Is.EqualTo(both.GetInvocationList()));
            Assert.That(c, Is.EqualTo(more.GetInvocationList()).And.Not.SameAs(a));
            Assert.That(d, Is.Not.SameAs(e), "switched off every call makes its own");
        });
    }

    [Test]
    public void TheTriggerCallsEveryHandlerInOrder()
    {
        using var harmony = new TestHarmony("komet-test-handlerlists");
        HandlerLists.Install(harmony);
        var events = (EventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
        var calls = new List<string>();
        events.OnGetClimate += (ref ClimateCondition climate, BlockPos _, EnumGetClimateMode _, double _) =>
        {
            calls.Add("a");
            climate.Temperature += 1;
        };
        events.OnGetClimate += (ref ClimateCondition climate, BlockPos _, EnumGetClimateMode _, double _) =>
        {
            calls.Add("b");
            climate.Temperature *= 2;
        };
        var climate = new ClimateCondition();
        events.TriggerOnGetClimate(ref climate, new BlockPos(0));
        events.TriggerOnGetClimate(ref climate, new BlockPos(0));
        events.OnGetClimate += (ref ClimateCondition _, BlockPos _, EnumGetClimateMode _, double _) => calls.Add("c");
        events.TriggerOnGetClimate(ref climate, new BlockPos(0));
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(Expected));
            Assert.That(climate.Temperature, Is.EqualTo(14));
        });
    }
}
