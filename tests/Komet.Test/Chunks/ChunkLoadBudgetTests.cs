using Komet.Chunks;
using Komet.Core;

namespace Komet.Test.Chunks;

// The engine's own task loop, rewritten, on a ClientMain without a game: the tasks are the engine's kinds (a ProcessPacketTask, a
// delegate on LoadChunkFromPacket's closure with its chunk packet) whose bodies only record that they ran.
[NonParallelizable]
public sealed class ChunkLoadBudgetTests
{
    private const int Far = 10, Near = 2;
    private static readonly FieldInfo Reversed = AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue");
    private static readonly MethodInfo Record = AccessTools.DeclaredMethod(typeof(ChunkLoadBudgetTests), nameof(Ran));
    private static readonly List<string> Order = [];
    private static readonly Dictionary<object, (string Name, double Spin)> Names = new(ReferenceEqualityComparer.Instance);

    private TestHarmony? _harmony;
    private int _mainThread;
    private FrameProfilerUtil? _profiler;

    [SetUp]
    public void Install()
    {
        (_mainThread, _profiler) = (RuntimeEnv.MainThreadId, ScreenManager.FrameProfiler);
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static _ => { });
        _harmony = new TestHarmony("komet-test-chunkloadbudget");
        ChunkLoadBudget.Install(_harmony, new QuietLogger());
        (ChunkLoadBudget.Millis, Counting.Hud) = (1, true);
        Order.Clear();
        Names.Clear();
    }

    [TearDown]
    public void Restore()
    {
        _harmony?.Dispose();
        (ChunkLoadBudget.Millis, Counting.Hud) = (ChunkLoadBudget.DefaultMillis, false);
        (RuntimeEnv.MainThreadId, ScreenManager.FrameProfiler) = (_mainThread, _profiler!);
    }

    [Test]
    public void InstallGatesTheTaskLoop()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ChunkLoadBudget.Rewritten, Is.True, "no single loop test on reversedQueue.Count");
            Assert.That(EngineShape.Of(ChunkLoadBudget.Seams()), Is.EqualTo(ChunkLoadBudget.Fingerprint),
                $"not the bodies verified: 0x{EngineShape.Of(ChunkLoadBudget.Seams()):X16}UL");
            Assert.That(ChunkLoadBudget.Seams()[2]?.Name, Does.Contain("LoadChunkFromPacket"));
        });
    }

    // Over budget the queue stops in front of a far chunk and an unload, never in front of anything else, and every task runs once,
    // in the order it was queued, ahead of what arrived meanwhile
    [Test]
    public void TheQueueStopsBeforeAFarChunkAndKeepsItsOrder()
    {
        var game = Game();
        Queue(game, Slow("a"), Load("far1", Far), Other("b"), Unload("unload"), Load("far2", Far));
        game.ExecuteMainThreadTasks(0);
        Assert.That(Order, Is.EqualTo(["a"]), "a took the budget, far1 waits");
        Queue(game, Other("late"));
        game.ExecuteMainThreadTasks(0);
        Assert.That(Order, Is.EqualTo(["a", "far1", "b", "unload", "far2", "late"]));
        Assert.That((ChunkLoadBudget.Stops, Pending(game)), Is.EqualTo((1L, 0)));
    }

    [Test]
    public void ANearChunkFurtherBackIsNeverHeld()
    {
        var game = Game();
        Queue(game, Slow("a"), Load("far1", Far), Other("b"), Load("near", Near), Load("far2", Far), Other("c"));
        game.ExecuteMainThreadTasks(0);
        Assert.That(Order, Is.EqualTo(["a", "far1", "b", "near"]), "runs through the near chunk, then stops");
        game.ExecuteMainThreadTasks(0);
        Assert.That(Order, Is.EqualTo(["a", "far1", "b", "near", "far2", "c"]));
    }

    [TestCase(0, true, TestName = "Engine budget")]
    [TestCase(1, false, TestName = "Not spawned")]
    public void TheEngineLoopRunsEverything(int millis, bool spawned)
    {
        ChunkLoadBudget.Millis = millis;
        var game = Game(spawned);
        Queue(game, Slow("a"), Load("far1", Far), Unload("unload"));
        game.ExecuteMainThreadTasks(0);
        Assert.That(Order, Is.EqualTo(["a", "far1", "unload"]));
    }

    [Test]
    public void WithinTheBudgetEverythingRuns()
    {
        ChunkLoadBudget.Millis = ChunkLoadBudget.MaxMillis;
        var game = Game();
        Queue(game, Other("a"), Load("far1", Far), Unload("unload"), Load("far2", Far));
        game.ExecuteMainThreadTasks(0);
        Assert.That(Order, Is.EqualTo(["a", "far1", "unload", "far2"]));
    }

    // The first task of a frame always runs, however slow the one before it was
    [Test]
    public void EveryFrameRunsAtLeastOneTask()
    {
        var game = Game();
        Queue(game, Slow("far1", Far), Slow("far2", Far), Slow("far3", Far));
        for (var frame = 1; frame <= 3; frame++)
        {
            game.ExecuteMainThreadTasks(0);
            Assert.That(Order, Has.Count.EqualTo(frame));
        }
    }

    // Columns, not distance: within three of the player's column in x and z, any height; a moving dimension's chunk always loads
    [TestCase(3, 0, 3, ExpectedResult = false)]
    [TestCase(-3, 7, -3, ExpectedResult = false)]
    [TestCase(4, 0, 0, ExpectedResult = true)]
    [TestCase(0, 0, -4, ExpectedResult = true)]
    [TestCase(40, 1024, 40, ExpectedResult = false)]
    public bool FarIsMoreThanThreeColumnsAway(int dx, int y, int dz)
    {
        var pos = new EntityPos { X = 100 * 32 + 5, Z = 200 * 32 + 31 };
        return ChunkLoadBudget.Far(new Packet_ServerChunk { X = 100 + dx, Y = y, Z = 200 + dz }, pos);
    }

    private static ClientMain Game(bool spawned = true)
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        (game.GameLaunchTasks, game.MainThreadTasks, game.MainThreadTasksLock, game.Spawned) = (new(), new(), new object(), spawned);
        Reversed.SetValue(game, new Queue<ClientTask>());
        var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        data.EntityPlayer = (EntityPlayer)RuntimeHelpers.GetUninitializedObject(typeof(EntityPlayer));
        _ = AccessTools.PropertySetter(typeof(Entity), nameof(Entity.Pos)).Invoke(data.EntityPlayer, [new EntityPos()]);
        game.player = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        AccessTools.Field(typeof(ClientPlayer), "worlddata").SetValue(game.player, data);
        return game;
    }

    private static int Pending(ClientMain game) => ((Queue<ClientTask>)Reversed.GetValue(game)!).Count;

    private static void Queue(ClientMain game, params ClientTask[] tasks)
    {
        foreach (var task in tasks) game.MainThreadTasks.Enqueue(task);
    }

    // A delegate whose Target is the given object and whose body records the task's name
    private static ClientTask Task(object target, string name, double spin = 0)
    {
        Names[target] = (name, spin);
        return new ClientTask { Action = (Action)Delegate.CreateDelegate(typeof(Action), target, Record), Code = name };
    }

    private static void Ran(object target)
    {
        var (name, spin) = Names.TryGetValue(target, out var known) ? known : ("?", 0);
        Order.Add(name);
        Busy.Spin(spin);
    }

    private static ClientTask Other(string name) => Task(new object(), name);
    private static ClientTask Slow(string name) => Task(new object(), name, 2);

    private static ClientTask Slow(string name, int column) => Task(Closure(column), name, 2);
    private static ClientTask Load(string name, int column) => Task(Closure(column), name);

    private static object Closure(int column)
    {
        var type = ChunkLoadBudget.Seams()[2]!.DeclaringType!;
        var closure = RuntimeHelpers.GetUninitializedObject(type);
        AccessTools.DeclaredField(type, "p").SetValue(closure, new Packet_ServerChunk { X = column, Y = 0, Z = 0 });
        return closure;
    }

    private static ClientTask Unload(string name)
    {
        var task = (ProcessPacketTask)RuntimeHelpers.GetUninitializedObject(typeof(ProcessPacketTask));
        AccessTools.Field(typeof(ProcessPacketTask), "packet").SetValue(task, new Packet_Server { Id = 11 });
        return Task(task, name);
    }
}
