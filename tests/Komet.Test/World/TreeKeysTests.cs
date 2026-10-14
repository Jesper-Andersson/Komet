using Komet.World;

namespace Komet.Test.World;

// A tree read back through the rewritten FromBytes is the tree written: keys new and seen before, non-ASCII, nested, and every value
[NonParallelizable]
public sealed class TreeKeysTests
{
    [Test]
    public void ReadsBackWhatWasWritten()
    {
        using var harmony = new TestHarmony("komet-test-treekeys");
        TreeKeys.Install(harmony);
        Assert.That(TreeKeys.Rewritten, Is.True);
        var tree = new TreeAttribute();
        tree.SetString("inventory", "game:bread-spelt-perfect");
        tree.SetInt("quantity", 12);
        tree.SetString("größe-ä", "naïve");
        var inner = tree.GetOrAddTreeAttribute("slots");
        inner.SetFloat("quantity", 0.5f);
        inner.SetString(new string('k', 300), "long key");
        for (var round = 0; round < 2; round++)
        {
            var read = new TreeAttribute();
            read.FromBytes(tree.ToBytes());
            Assert.That(read.ToJsonToken(), Is.EqualTo(tree.ToJsonToken()), $"round {round}");
            Assert.That(ReferenceEquals(read.Keys[0], string.IsInterned(read.Keys[0])), Is.True, "interned");
        }
    }
}
