using Komet.Core;
using Komet.World;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// EntityPartitioning's grid built from pooled chunks and lists: every call leaves the dictionary, each list's entities in their order,
// the players' lists and the largest touch distance exactly as the engine's own run over the same moves, and in steady state the pool
// hands its chunks and lists out again instead of allocating
public sealed class PartitionReuseTests
{
    private const int MapSize = 1024000, Ticks = 8;

    private static readonly Action<EntityPartitioning, ICollection<Entity>> Partition =
        AccessTools.MethodDelegate<Action<EntityPartitioning, ICollection<Entity>>>(
            AccessTools.Method(typeof(EntityPartitioning), "PartitionEntities"));

    // Komet finds the types by name, which only works once the game has loaded VSEssentials; here the test does it
    [OneTimeSetUp]
    public void LoadEssentials()
    {
        var essentials = typeof(EntityPartitioning).Assembly;
        Assert.That(AccessTools.TypeByName("Vintagestory.GameContent.EntityPartitionChunk"), Is.Not.Null,
            essentials.FullName);
    }

    [TearDown]
    public void Restore() => (PartitionReuse.Enabled, Counting.Hud) = (true, false);

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-partitionreuse");
        PartitionReuse.Install(harmony);
        Assert.That(PartitionReuse.Rewritten, Is.True,
            "PartitionEntities, EntityPartitionChunk.Add or FetchOrCreateList no longer allocate as they did");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void EveryCallPartitionsAsTheEngine(bool enabled)
    {
        var engine = Run(out _);
        using var harmony = new TestHarmony("komet-test-partitionreuse");
        PartitionReuse.Install(harmony);
        PartitionReuse.Enabled = enabled;
        Counting.Hud = true;
        var before = PartitionReuse.Reused;
        Assert.That(PartitionReuse.Rewritten, Is.True);
        var pooled = Run(out var players);
        Assert.Multiple(() =>
        {
            Assert.That(pooled, Is.EqualTo(engine));
            Assert.That(PartitionReuse.Reused > before, Is.EqualTo(enabled), "reused only while switched on");
            Assert.That(players, Is.Not.Empty);
        });
    }

    // After a warm-up the pool serves every chunk, list and array: what a call allocates is the enumerator of the collection it walks
    [Test]
    public void SteadyStateAllocatesAlmostNothing()
    {
        using var harmony = new TestHarmony("komet-test-partitionreuse");
        PartitionReuse.Install(harmony);
        var (partitioning, entities) = (Partitioning(), World(Ticks));
        Moves(entities, 0);
        for (var i = 0; i < 4; i++) Partition(partitioning, entities);
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4; i++) Partition(partitioning, entities);
        var pooled = GC.GetAllocatedBytesForCurrentThread() - start;
        PartitionReuse.Enabled = false;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 4; i++) Partition(partitioning, entities);
        var engine = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.That(pooled, Is.LessThan(engine / 20), $"pooled {pooled} B, engine {engine} B for four calls");
    }

    // A body that clears twice is not the shape the rewrite knows: the engine's IL comes back untouched
    [Test]
    public void AShapeItDoesNotKnowIsLeftAlone()
    {
        var target = AccessTools.Method(typeof(EntityPartitioning), "PartitionEntities");
        var code = PatchProcessor.GetOriginalInstructions(target);
        var clear = code.FindIndex(c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo { Name: "Clear" });
        code.InsertRange(clear + 1, [new CodeInstruction(code[clear - 1]), new CodeInstruction(code[clear])]);
        var result = PartitionReuse.RewritePartition(code);
        Assert.Multiple(() =>
        {
            Assert.That(PartitionReuse.Rewritten, Is.False);
            Assert.That(result.Count(c => c.opcode == OpCodes.Callvirt && c.operand is MethodInfo { Name: "Clear" }),
                Is.EqualTo(2));
        });
    }

    // Ticks calls over moving entities, with a player re-partitioned in between, each described as the engine's API shows it
    private static List<string> Run(out List<string> players)
    {
        var (partitioning, entities) = (Partitioning(), World(Ticks));
        var (seen, ids) = (new List<string>(), new Dictionary<Entity, int>(ReferenceEqualityComparer.Instance));
        players = [];
        for (var i = 0; i < entities.Count; i++) ids[entities[i]] = i;
        for (var tick = 0; tick < Ticks; tick++)
        {
            Moves(entities, tick);
            Partition(partitioning, tick % 3 == 2 ? [.. entities.Take(entities.Count - tick)] : entities);
            if (tick == 4) partitioning.RePartitionPlayer((EntityPlayer)entities[0]);
            seen.Add(Describe(partitioning, ids));
            players.Add(string.Join(",", entities.OfType<EntityPlayer>()
                .Select(p => p.entityListForPartitioning is { } list ? Ids(list, ids) : "none")));
        }

        return [.. seen.Zip(players, (grid, player) => grid + " | " + player)];
    }

    private static EntityPartitioning Partitioning()
    {
        var blocks =
            Answers.Of<IBlockAccessor>(new() { ["get_MapSizeX"] = _ => MapSize, ["get_MapSizeZ"] = _ => MapSize });
        var world = Answers.Of<IWorldAccessor>(new() { ["get_BlockAccessor"] = _ => blocks });
        var api = Answers.Of<ICoreAPI>(new() { ["get_World"] = _ => world });
        var partitioning = new EntityPartitioning();
        AccessTools.Field(typeof(EntityPartitioning), "api").SetValue(partitioning, api);
        return partitioning;
    }

    // Two players, creatures and inanimate items, some sharing a cell, some a chunk, a few far apart
    private static List<Entity> World(int count)
    {
        var entities = new List<Entity> { new EntityPlayer(), new EntityPlayer() };
        for (var i = 0; i < 12 * count; i++) entities.Add(i % 3 == 0 ? new EntityItem() : new EntityAgent());
        for (var i = 0; i < entities.Count; i++) entities[i].touchDistance = i % 7 * 0.25;
        return entities;
    }

    // Each tick moves every entity a little and every fifth one into another chunk
    private static void Moves(List<Entity> entities, int tick)
    {
        for (var i = 0; i < entities.Count; i++)
        {
            var jump = (i + tick) % 5 == 0 ? 40 * tick : 0;
            _ = entities[i].Pos.SetPos(512000 + i % 13 * 3.5 + jump, 110 + i % 9 * 4.0, 512000 + i % 11 * 5.25 + tick);
        }
    }

    private static string Describe(EntityPartitioning partitioning, Dictionary<Entity, int> ids)
    {
        var chunks = partitioning.Partitions.OrderBy(p => p.Key).Select(p =>
            $"{p.Key}:[{Cells(p.Value.Entities, ids)}]/" +
            $"{(p.Value.InanimateEntities is { } inanimate ? Cells(inanimate, ids) : "none")}");
        return string.Join(" ", chunks) + $" touch {partitioning.LargestTouchDistance}";
    }

    private static string Cells(List<Entity>[] cells, Dictionary<Entity, int> ids) =>
        string.Join(";", cells.Select(list => list is null ? "-" : Ids(list, ids)));

    private static string Ids(List<Entity> list, Dictionary<Entity, int> ids) =>
        "(" + string.Join(",", list.Select(e => ids[e])) + ")";
}
