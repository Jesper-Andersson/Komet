using System.Collections.Concurrent;
using Komet.Core;
using Komet.Tessellation;

namespace Komet.Test.Tessellation;

public sealed class TessQueueTests
{
    private const long MulX = 1024, MulZ = 1024;

    private static long Index(int x, int y, int z) => (y * MulZ + z) * MulX + x;

    private static TessQueue Queue(int px = 100, int py = 3, int pz = 100, double yaw = 0)
    {
        var queue = new TessQueue();
        queue.Map(MulX, MulZ);
        var (fx, fz) = TessQueue.Facing(yaw);
        queue.Aim(new TessView(px, py, pz, fx, fz));
        return queue;
    }

    private static List<long> TakeAll(TessQueue queue)
    {
        var taken = new List<long>();
        for (var i = 0; i < 100000 && queue.TryTake(out var raw); i++)
        {
            taken.Add(raw);
            queue.Done(raw & long.MaxValue);
        }

        return taken;
    }

    // Columns (x, z) at y 3 as they arrive, and the order they are taken in from (100, 3, 100) facing +x: nearest first; within the
    // near ring every chunk scores its squared distance and equal scores keep the engine's arrival order; ahead beats behind
    [TestCase(new[] { 140, 100, 101, 100, 110, 100 }, new[] { 1, 2, 0 }, TestName = "nearest comes first")]
    [TestCase(new[] { 100, 101, 99, 100, 100, 99, 101, 100 }, new[] { 0, 1, 2, 3 }, TestName = "equal scores keep the order")]
    [TestCase(new[] { 90, 100, 110, 100 }, new[] { 1, 0 }, TestName = "ahead beats behind at the same distance")]
    public void TheQueueTakesNearestFirst(int[] columns, int[] order)
    {
        var queue = Queue();
        long[] marks = [.. columns.Chunk(2).Select(c => Index(c[0], 3, c[1]))];
        queue.AddRange(marks);
        Assert.That(TakeAll(queue), Is.EqualTo(order.Select(i => marks[i])));
    }

    [Test]
    public void TurningReordersWhatWaits()
    {
        var queue = Queue(yaw: 0);
        long east = Index(110, 3, 100), west = Index(90, 3, 100);
        queue.AddRange([east, west]);
        var (fx, fz) = TessQueue.Facing(Math.PI);
        queue.Aim(new TessView(100, 3, 100, fx, fz));
        Assert.That(TakeAll(queue), Is.EqualTo([west, east]));
    }

    // Marks of one chunk as they arrive (true: edge-only) and the one pass taken
    [TestCase(new[] { false, true }, false, TestName = "edge-only is dropped while the full pass waits")]
    [TestCase(new[] { true, false }, false, TestName = "full upgrades a waiting edge-only mark")]
    [TestCase(new[] { true }, true, TestName = "edge-only alone stays edge-only")]
    public void MarksOfOneChunkMerge(bool[] edges, bool edge)
    {
        var queue = Queue();
        var index = Index(7, 2, 7);
        queue.AddRange([.. edges.Select(e => e ? index | long.MinValue : index)]);
        Assert.That(TakeAll(queue), Is.EqualTo([edge ? index | long.MinValue : index]));
    }

    [Test]
    public void DeferredComeBackOnlyOnUndefer()
    {
        var queue = Queue();
        var index = Index(7, 2, 7);
        queue.AddRange([index]);
        Assert.That(queue.TryTake(out var raw), Is.True);
        Assert.That(queue.Count, Is.Zero);
        queue.Done(raw & long.MaxValue);
        queue.Defer(raw);
        Assert.That(queue.TryTake(out _), Is.False);
        Assert.That(queue.Count, Is.EqualTo(1), "a deferred chunk still counts as waiting");
        queue.Undefer();
        Assert.That(TakeAll(queue), Is.EqualTo([index]));
    }

    [Test]
    public void DrainHandsBackEverythingAsEngineMarks()
    {
        var queue = Queue();
        long a = Index(1, 1, 1), b = Index(2, 2, 2) | long.MinValue, c = Index(3, 3, 3);
        queue.AddRange([a, b]);
        queue.Defer(c);
        var back = new UniqueQueue<long>();
        queue.Drain(back);
        Assert.That(back, Is.EquivalentTo([a, b, c]));
        Assert.That(queue.Count, Is.Zero);
    }

    [Test]
    public void AChunkInFlightIsNotHandedOutTwice()
    {
        var queue = Queue();
        var index = Index(7, 2, 7);
        queue.AddRange([index]);
        Assert.That(queue.TryTake(out var first), Is.True);
        queue.AddRange([index]); // marked dirty again while the first pass runs
        Assert.That(queue.TryTake(out _), Is.False, "the second pass waits for the first");
        Assert.That(queue.Count, Is.EqualTo(1), "and still counts as waiting");
        queue.Done(first);
        Assert.That(TakeAll(queue), Is.EqualTo([index]));
    }

    [Test]
    public void APriorityPassBlocksTheSameChunk()
    {
        var queue = Queue();
        var index = Index(7, 2, 7);
        Assert.That(queue.TryBegin(index), Is.True);
        Assert.That(queue.TryBegin(index), Is.False, "one pass per chunk at a time");
        queue.AddRange([index]);
        Assert.That(queue.TryTake(out _), Is.False);
        queue.Done(index);
        Assert.That(TakeAll(queue), Is.EqualTo([index]));
    }

    [Test]
    public void AHandedBackPassGoesToTheTessellationThread()
    {
        var queue = Queue();
        long index = Index(7, 2, 7), edge = index | long.MinValue;
        queue.AddRange([edge]);
        Assert.That(queue.TryTake(out var raw), Is.True);
        queue.Home(raw);
        Assert.Multiple(() =>
        {
            Assert.That(queue.Count, Is.EqualTo(1), "a handed-back pass waits");
            Assert.That(queue.TryTake(out _), Is.False, "workers do not take it");
        });
        Assert.That(queue.TryBegin(index), Is.True); // a priority pass of the same chunk runs meanwhile
        Assert.That(queue.TryTakeHome(out _), Is.False, "not while the chunk is in flight");
        queue.Done(index);
        Assert.That(queue.TryTakeHome(out var home), Is.True);
        Assert.That((home, queue.Count), Is.EqualTo((edge, 0)), "the mark as the engine's queue held it");
        queue.Done(home & long.MaxValue);
        var back = new UniqueQueue<long>();
        queue.AddRange([edge]);
        Assert.That(queue.TryTake(out raw), Is.True);
        queue.Home(raw);
        queue.Drain(back);
        Assert.That(back, Is.EquivalentTo([edge]),
            "handed-back passes go back into the engine's queue when switched off");
    }

    [Test]
    public void APassEndingAfterAClearChangesNothing()
    {
        var queue = Queue();
        long a = Index(1, 1, 1), b = Index(2, 1, 1);
        queue.AddRange([a]);
        Assert.That(queue.TryTake(out var raw), Is.True);
        queue.Clear();
        queue.AddRange([b]);
        queue.Done(raw);
        queue.Home(raw);
        Assert.That(queue.TryTakeHome(out _), Is.False, "the old world's pass is not handed back into this one");
        Assert.That(TakeAll(queue), Is.EqualTo([b]));
    }

    [Test]
    public void ManyThreadsTakeEveryChunkExactlyOnce()
    {
        var queue = Queue();
        var marks = Enumerable.Range(0, 20000).Select(i => Index(i % 200, 3, i / 200 % 200)).Distinct().ToArray();
        queue.AddRange(marks);
        var taken = new ConcurrentBag<long>();
        var active = new ConcurrentDictionary<long, byte>();
        var readded = new ConcurrentDictionary<long, byte>();
        var overlap = 0;
        _ = Parallel.For(0, 4, (_, _) =>
        {
            for (var i = 0; i < 100000 && queue.TryTake(out var raw); i++)
            {
                if (!active.TryAdd(raw, 0)) _ = Interlocked.Increment(ref overlap);
                taken.Add(raw);
                if (i % 7 == 0 && readded.TryAdd(raw, 0))
                    queue.AddRange([raw]); // dirty again while in flight: once more, later
                _ = active.TryRemove(raw, out _);
                queue.Done(raw);
            }
        });
        var counts = taken.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        Assert.Multiple(() =>
        {
            Assert.That(overlap, Is.Zero, "one chunk on two threads at once");
            Assert.That(counts.Keys, Is.EquivalentTo(marks));
            Assert.That(counts.Values.All(n => n is 1 or 2), Is.True);
            Assert.That(queue.Count, Is.Zero);
        });
    }

    [Test]
    public void ScoreWeightsTheAngleOnlyPastTheNearRing()
    {
        Assert.That(TessQueue.Score(-2, 0, 0, 1, 0), Is.EqualTo(TessQueue.Score(2, 0, 0, 1, 0)));
        Assert.That(TessQueue.Score(-5, 0, 0, 1, 0), Is.EqualTo(3 * TessQueue.Score(5, 0, 0, 1, 0)));
        Assert.That(TessQueue.Score(0, 0, 5, 1, 0), Is.EqualTo(2 * TessQueue.Score(5, 0, 0, 1, 0)));
    }

    // The patched tick on a rigged world: an edge-only priority mark merges with the full mark behind it into one pass, and a chunk the
    // server has not sent yet is tried once per tick and comes back in the next
    [Test]
    public void TheTickMergesPriorityMarksAndRetriesARequeueNextTick()
    {
        using var rig = new ChunkRig();
        var manager = TessManager.Ticking(rig);
        var (priority, dirty) = ((UniqueQueue<long>)ChunkRig.Get(rig.Game, "dirtyChunksPriority"),
            (UniqueQueue<long>)ChunkRig.Get(rig.Game, "dirtyChunks"));
        var merged = rig.Put(1, 1, 1, (_, _, _) => ChunkRig.Air, empty: true);
        var unsent = rig.Put(2, 1, 1, (_, _, _) => ChunkRig.Stone);
        ChunkRig.Set(unsent, "loadedFromServer", false);
        priority.Enqueue(ChunkRig.Key(1, 1, 1) | long.MinValue);
        priority.Enqueue(ChunkRig.Key(1, 1, 1));
        dirty.Enqueue(ChunkRig.Key(2, 1, 1));
        var harmony = new Harmony("komet-test-tessschedule");
        try
        {
            TessAccounting.Install(harmony);
            TessSchedule.Install(harmony);
            Counting.Hud = true;
            Assert.That(TessSchedule.Installed, Is.True, "an engine seam the tick reaches is gone");
            manager.OnSeperateThreadGameTick(0);
            Assert.Multiple(() =>
            {
                Assert.That(ChunkRig.Get(merged, "quantityDrawn"), Is.EqualTo(1), "one pass for both marks");
                Assert.That((priority.Count, dirty.Count), Is.EqualTo((0, 0)),
                    "the normal mark moved into the schedule");
                Assert.That(TessAccounting.Count(TessBucket.Requeued), Is.EqualTo(1),
                    "tried once in the tick, not again at once");
            });
            unsent.Empty = true; // arrived meanwhile, and is empty
            manager.OnSeperateThreadGameTick(0);
            Assert.Multiple(() =>
            {
                Assert.That(ChunkRig.Get(unsent, "quantityDrawn"), Is.EqualTo(1), "back in the next tick");
                Assert.That(TessAccounting.Count(TessBucket.Requeued), Is.EqualTo(1));
                Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(2));
            });
        }
        finally
        {
            Counting.Hud = false;
            harmony.UnpatchAll(harmony.Id);
            TessSchedule.Clear();
            TessAccounting.Clear();
        }
    }
}
