using Komet.Api;
using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Komet.World;
using OpenTK.Windowing.Desktop;

namespace Komet.Test.Core;

// Every engine method and field Komet reaches by name. A feature whose method or accessor field is gone stands down at install with a
// logged assertion, so a game update that renames one turns it off; here it turns the build red instead. A name Harmony injects into
// a patch (a ___field or an argument) would make harmony.Patch throw at install; Il.Binds stands the feature down before that, and
// PatchBindsByName holds those names against this engine.
public sealed class EngineSeamTests
{
    private static readonly Type[] NoArgs = [];
    private static readonly Type[] Coords = [typeof(int), typeof(int), typeof(int)];
    private static readonly Type[] Renderer = [typeof(IRenderer), typeof(EnumRenderStage), typeof(string)];

    private static IEnumerable<TestCaseData> Methods()
    {
        yield return Case<ShaderProgramBase>("Use", NoArgs);
        yield return Case<DefaultShaderUniforms>("Update", null);
        yield return Case<MeshDataPool>("FrustumCull", null);
        yield return Case<MeshDataPoolMasterManager>("OnFrame", null);
        yield return Case<ClientPlatformWindows>("RenderMesh",
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);
        yield return Case<SystemRenderSunMoon>("OnRenderFrame3DPost", null);
        yield return Case<ClientEventManager>("TriggerRenderStage", null);
        yield return Case<GameTickListener>("OnTriggered", null);
        yield return Case<GameTickListenerBlock>("OnTriggered", null);
        yield return Case<ClientWorldMap>("GetChunk", [typeof(long)]);
        yield return Case<ClientWorldMap>("GetChunk", Coords);
        yield return Case<ClientWorldMap>("GetChunkAtBlockPos", Coords);
        yield return Case<ClientWorldMap>("loadChunkMT", null);
        yield return Case<ClientWorldMap>("OverloadChunkMT", null);
        yield return Case<SystemUnloadChunks>("HandleChunkUnload", null);
        yield return Case<MeshDataPool>("InsertAt", null);
        yield return Case<MeshDataPool>("RemoveLocation", null);
        yield return Case<MeshDataPool>("TrySqueezeInbetween", null);
        yield return Case<MeshData>("CloneExtraData", null);
        yield return Case<MeshData>("DisposeExtraData", null);
        yield return Case<ChunkTesselatorManager>("OnBeforeFrame", null);
        yield return Case<ChunkTesselatorManager>("OnSeperateThreadGameTick", [typeof(float)]);
        yield return Case<ChunkTesselatorManager>("TesselateChunk",
            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()]);
        yield return Case<ClientWorldMap>("LoadOrCreateLerpedClimateMapOffthread", null);
        yield return Case<ClientWorldMap>("LoadChunkFromPacket", null);
        yield return Case<ClientMain>("ExecuteMainThreadTasks", null);
        yield return Case<ClientChunk>("InitBlockEntitiesFromPacket", [typeof(ClientMain)]);
        yield return Case<ClientWorldMap>("GetClientChunk", Coords);
        yield return Case<Vintagestory.GameContent.BlockEntityGroundStorage>("UpdateIgnitable", NoArgs);
    }

    private static IEnumerable<TestCaseData> Fields()
    {
        yield return Field<MeshDataPool>("poolLocations");
        yield return Field<MeshDataPool>("poolId");
        yield return Field<MeshDataPool>("poolOrigin");
        yield return Field<MeshDataPool>("modelRef");
        yield return Field<FrustumCulling>("frustum");
        yield return Field<FrustumCulling>("playerPos");
        yield return Field<ClientWorldMap>("chunks", typeof(Dictionary<long, ClientChunk>));
        yield return Field<ClientWorldMap>("chunksLock", typeof(object));
        yield return Field<ClientWorldMap>("LerpedClimateMaps");
        yield return Field<ClientWorldMap>("LerpedClimateMapsLock");
        yield return Field<ClientMain>("reversedQueue", typeof(Queue<ClientTask>));
        yield return Field<ProcessPacketTask>("packet", typeof(Packet_Server));
        yield return Field<ClientEventManager>("EventBusListeners", typeof(List<EventBusListener>));
        yield return Field<SystemRenderSunMoon>("occlQueryId");
        yield return Field<SystemRenderSunMoon>("firstTickDone");
        yield return Field<SystemRenderSunMoon>("nowQuerying");
        yield return Field<SystemRenderSunMoon>("targetSunSpec");
        yield return Field<List<int>>("_version");
        // IdleAnimators' accessors: the count's offset is measured at install, the version is read by every parked stage
        yield return Field<AnimatorBase>("activeAnimCount", typeof(int));
        yield return Field<List<RenderHandler>>("_version", typeof(int)).SetName("List<RenderHandler>._version");
        // TessSchedule's accessors: a mismatch would throw on the tessellation thread, which ends the game
        yield return Field<ClientSystem>("game", typeof(ClientMain));
        yield return Field<ChunkTesselator>("started", typeof(bool));
        foreach (var queue in (string[])["dirtyChunks", "dirtyChunksLast", "dirtyChunksPriority"])
        {
            yield return Field<ClientMain>(queue, typeof(UniqueQueue<long>));
            yield return Field<ClientMain>(queue + "Lock", typeof(object));
        }
    }

    private static TestCaseData Case<T>(string member, Type[]? args) => new TestCaseData(typeof(T), member, args).SetName(
        $"{typeof(T).Name}.{member}{(args == null ? "" : $"({args.Length})")}");

    // A field type is given where an UnsafeAccessor binds it, which needs the exact type
    private static TestCaseData Field<T>(string member, Type? type = null) =>
        new TestCaseData(typeof(T), member, type).SetName($"{typeof(T).Name}.{member}");

    [TestCaseSource(nameof(Methods))]
    public void MethodExists(Type type, string name, Type[]? args)
    {
        ArgumentNullException.ThrowIfNull(type);
        Assert.That(AccessTools.Method(type, name, args), Is.Not.Null,
            $"{type.FullName}.{name} is gone, the Komet feature that patches it stands down");
    }

    [TestCaseSource(nameof(Fields))]
    public void FieldExists(Type type, string name, Type? fieldType)
    {
        ArgumentNullException.ThrowIfNull(type);
        var field = fieldType is null ? AccessTools.Field(type, name) : AccessTools.DeclaredField(type, name);
        Assert.That(field, Is.Not.Null, $"{type.FullName}.{name} is gone, the Komet feature that reads it stands down");
        if (fieldType is not null)
            Assert.That(field.FieldType, Is.EqualTo(fieldType), $"{type.FullName}.{name} changed its type");
    }

    private static IEnumerable<TestCaseData> Patches()
    {
        yield return Patch<SystemRenderSunMoon>("OnRenderFrame3DPost", null, typeof(SunOcclusion), "Prefix");
        yield return Patch<MeshDataPool>("InsertAt", null, typeof(MeshPool), "InsertAt");
        yield return Patch<MeshDataPool>("RemoveLocation", null, typeof(MeshPool), "RemoveLocation");
        yield return Patch<MeshDataPool>("TrySqueezeInbetween", null, typeof(MeshPool), "TrySqueezeInbetween");
        yield return Patch<MeshDataPool>("FrustumCull", null, typeof(FrustumSweep), "FrustumCull");
        yield return Patch<ClientPlatformWindows>("RenderMesh",
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)],
            typeof(IndirectDraw), "RenderMesh");
        yield return Patch<MeshData>("CloneExtraData", null, typeof(MeshRecycle), "CloneExtraData");
        yield return Patch<ClientWorldMap>("GetChunk", [typeof(long)], typeof(ChunkLookup), "ByIndex");
        yield return Patch<ClientWorldMap>("GetChunk", Coords, typeof(ChunkLookup), "ByCoord");
        yield return Patch<ClientWorldMap>("GetChunkAtBlockPos", Coords, typeof(ChunkLookup), "AtBlockPos");
        yield return Patch<ClientWorldMap>("loadChunkMT", null, typeof(ChunkLookup), "Loaded");
        yield return Patch<ClientWorldMap>("OverloadChunkMT", null, typeof(ChunkLookup), "Overloading");
        yield return Patch<ClientWorldMap>("OverloadChunkMT", null, typeof(ChunkLookup), "Overloaded");
        yield return Patch<SystemUnloadChunks>("HandleChunkUnload", null, typeof(ChunkLookup), "Unloading");
        yield return Patch<SystemRenderEntities>("OnBeforeRender", [typeof(float)], typeof(EntityTessBudget), "Frame");
        yield return Patch<ChunkTesselatorManager>("TesselateChunk", null, typeof(TessAccounting), "Begin");
        yield return Patch<ChunkTesselatorManager>("TesselateChunk", null, typeof(TessAccounting), "End");
        yield return Patch<ClientEventManager>("TriggerRenderStage", null, typeof(ModTimes), "RenderStage");
        yield return Patch<AnimationUtil>("StartAnimation", null, typeof(IdleAnimators), "Woken");
        yield return Patch<ClientMain>("ExecuteMainThreadTasks", null, typeof(EventBusSweep), "Sweep");
        yield return Patch<BlockEntity>("OnBlockUnloaded", null, typeof(EventBusSweep), "Died");
        yield return Patch<BlockEntity>("OnBlockRemoved", null, typeof(EventBusSweep), "Died");
        yield return Patch<BlockEntity>("Initialize", null, typeof(EventBusSweep), "Revived");
        yield return Patch<ClientEventAPI>("RegisterEventBusListener", null, typeof(EventBusSweep), "Registered");
        yield return Patch<WorldChunk>("GetLocalBlockEntityAtBlockPos", null, typeof(BlockEntityBudget), "Accessed");
        yield return Patch<EventManager>("AddGameTickListenerBlockInternal", null, typeof(ListenerSlots), "AddTick");
        yield return Patch<EventManager>("RemoveGameTickListener", null, typeof(ListenerSlots), "RemovingTick");
        yield return Patch<EventManager>("RemoveGameTickListener", null, typeof(ListenerSlots), "RemovedTick");
        yield return Patch<ClientEventManager>("RegisterRenderer", Renderer, typeof(ListenerSlots), "Rendering");
        yield return Patch<ClientEventManager>("RegisterRenderer", Renderer, typeof(ListenerSlots), "Rendered");
        yield return Patch<ClientEventManager>("UnregisterRenderer", null, typeof(ListenerSlots), "Unrendering");
        yield return Patch<ClientEventManager>("UnregisterRenderer", null, typeof(ListenerSlots), "Unrendered");
        yield return Patch<DummyRenderer>("set_RenderOrder", null, typeof(ListenerSlots), "Moved");
        yield return Patch<ClientEventAPI>("RegisterEventBusListener", null, typeof(ListenerSlots), "Listening");
        yield return Patch<ClientEventAPI>("RegisterEventBusListener", null, typeof(ListenerSlots), "Listened");
        yield return Patch<ClientEventAPI>("UnregisterEventBusListener", null, typeof(ListenerSlots), "Unlistening");
        yield return Patch<ClientEventAPI>("UnregisterEventBusListener", null, typeof(ListenerSlots), "Unlistened");
        yield return Patch<Vintagestory.GameContent.BlockEntityFirepit>("getOrCreateMesh", null, typeof(BlockEntityCaches),
            "Mesh");
    }

    private static TestCaseData Patch<T>(string member, Type[]? args, Type komet, string patch) =>
        new TestCaseData(typeof(T), member, args, komet, patch).SetName(
            $"{typeof(T).Name}.{member} by {komet.Name}.{patch}");

    [TestCaseSource(nameof(Patches))]
    public void PatchBindsByName(Type type, string name, Type[]? args, Type komet, string patch)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(komet);
        var (original, mine) = (AccessTools.Method(type, name, args), AccessTools.Method(komet, patch));
        Assert.That(Il.Binds(original, mine), Is.True,
            $"{komet.Name}.{patch} names what {type.Name}.{name} lacks, its Install stands down");
    }

    [Test]
    public void BindsRefusesAParameterOrFieldTheOriginalLacks()
    {
        var remove = AccessTools.Method(typeof(MeshDataPool), "RemoveLocation");
        var squeeze = AccessTools.Method(typeof(MeshDataPool), "TrySqueezeInbetween");
        Assert.Multiple(() =>
        {
            Assert.That(Il.Binds(remove, AccessTools.Method(typeof(MeshPool), "TrySqueezeInbetween")), Is.False,
                "modeldata");
            Assert.That(Il.Binds(squeeze, AccessTools.Method(typeof(SunOcclusion), "Prefix")), Is.False,
                "___firstTickDone");
        });
    }

    [Test]
    public void ClientSizeIsStillAPropertyOnTheWindow()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize)), Is.Not.Null);
            Assert.That(AccessTools.PropertySetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize)), Is.Not.Null);
            Assert.That(AccessTools.Method(typeof(NativeWindow), "OnResize"), Is.Not.Null);
        });
    }
}

// The features' fingerprints are pinned in FeaturesTests, each feature tests its own stand-down.
public sealed class EngineShapeTests
{
    [Test]
    public void FingerprintIsStableAndTellsMethodsApart()
    {
        var visible = VisibleFaces.Target();
        var fluids = AccessTools.DeclaredMethod(typeof(ChunkTesselator),
            nameof(ChunkTesselator.CalculateVisibleFaces_Fluids));
        var emit = AccessTools.DeclaredMethod(typeof(Block), nameof(Block.DoEmitSideAo));
        var byFlag = AccessTools.DeclaredMethod(typeof(Block), nameof(Block.DoEmitSideAoByFlag));
        Assert.Multiple(() =>
        {
            var first = EngineShape.Of(visible);
            Assert.That(EngineShape.Of(visible), Is.Not.Zero.And.EqualTo(first), "the same body, the same fingerprint");
            Assert.That(EngineShape.Of(visible), Is.Not.EqualTo(EngineShape.Of(fluids)),
                "the fluid twin differs in a few instructions");
            Assert.That(EngineShape.Of(emit), Is.Not.EqualTo(EngineShape.Of(byFlag)), "two near-identical one-liners");
            Assert.That(EngineShape.Of((MethodBase?)null), Is.Zero);
            Assert.That(EngineShape.Of(AccessTools.Method(typeof(IGeometryTester),
                nameof(IGeometryTester.GetCurrentBlockEntityOnSide), [typeof(BlockFacing)])), Is.Zero, "no body");
            Assert.That(EngineShape.Of([visible, null]), Is.Zero, "a missing method");
            Assert.That(EngineShape.Of([visible, fluids]), Is.Not.EqualTo(EngineShape.Of([fluids, visible])),
                "in order");
        });
    }

    private static bool RunOriginal() => true;

    // The guard's patch check on the sweep's method, counted as each copy it replaces counts: a prefix of the same class is its own
    // (VisibleFaces, FaceLight, ExtendedRows), one of another class under the same id is not; under an own id no patch is
    // (ColumnNoiseScratch, ShapeInitMemo, InitOnce); prefixes and postfixes leave the body (AnimationFrames' and OccludedChunks' seam
    // 0); with nobody own, any patch counts (FaceLight's AO methods and ForFluidsLayer overrides); a missing seam is foreign
    [Test]
    public void ForeignCountsOtherPatchesOfTheKindsAsked()
    {
        var target = VisibleFaces.Target();
        Assert.That(Harmony.GetPatchInfo(target!)?.Owners, Is.Null.Or.Empty, "a patch another test left behind");
        using var other = new TestHarmony("komet-test-foreign-other");
        using var own = new TestHarmony("komet-test-foreign-own");
        const PatchKinds replacing = PatchKinds.Replacing, all = PatchKinds.All,
            body = PatchKinds.Body;
        Assert.Multiple(() =>
        {
            Assert.That(EngineShape.Foreign([target], all, null), Is.False, "no patch");
            Assert.That(EngineShape.Foreign([target, null], all, null), Is.True, "a missing seam");
        });
        _ = own.Patch(target, new HarmonyMethod(typeof(EngineShapeTests), nameof(RunOriginal)));
        Assert.Multiple(() =>
        {
            Assert.That(EngineShape.Foreign([target], replacing, null, typeof(EngineShapeTests)), Is.False,
                "its own class");
            Assert.That(EngineShape.Foreign([target], replacing, null, typeof(VisibleFaces)), Is.True,
                "another class, the same id");
            Assert.That(EngineShape.Foreign([target], all, own.Id), Is.False, "its own id");
            Assert.That(EngineShape.Foreign([target], body, null), Is.False, "a prefix leaves the body");
            Assert.That(EngineShape.Foreign([target], all, null), Is.True, "any patch");
        });
        _ = other.Patch(target, postfix: Foreign.Postfix);
        Assert.Multiple(() =>
        {
            Assert.That(EngineShape.Foreign([target], replacing, own.Id), Is.False, "a postfix sees the same result");
            Assert.That(EngineShape.Foreign([target], all, own.Id), Is.True);
            Assert.That(EngineShape.Foreign([target], all, null, typeof(EngineShapeTests), typeof(Foreign)), Is.False,
                "both classes own");
        });
        _ = other.Patch(target, transpiler: Foreign.Transpiler);
        Assert.Multiple(() =>
        {
            Assert.That(EngineShape.Foreign([target], body, null), Is.True, "a rewritten body");
            Assert.That(EngineShape.Foreign([target], body, other.Id), Is.False);
        });
    }

    [Test]
    public void ManyBodiesAndConstructorsHaveAFingerprint()
    {
        var constructor = AccessTools.Constructor(typeof(PlayerHeadController),
            [typeof(IAnimationManager), typeof(EntityPlayer), typeof(Shape)]);
        MethodBase?[] bodies =
        [
            .. AccessTools.GetDeclaredMethods(typeof(Block)).Where(m => m.GetMethodBody() is not null)
                .Take(EngineShape.MaxMethods - 1),
            constructor
        ];
        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Length.EqualTo(EngineShape.MaxMethods));
            Assert.That(EngineShape.Of(constructor), Is.Not.Zero);
            Assert.That(EngineShape.Of(bodies), Is.Not.Zero.And.Not.EqualTo(EngineShape.Of(bodies.AsSpan(1))));
        });
    }

    [TestCaseSource(nameof(Declining))]
    public void AChangedEngineDeclinesTheInstall(string name, Action<Harmony, ILogger> install, Func<bool> installed)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(installed);
        using var harmony = new TestHarmony($"komet-test-{name}-changed");
        var logger = new CapturingLogger();
        install(harmony, logger);
        Assert.Multiple(() =>
        {
            Assert.That(installed(), Is.False);
            Assert.That(harmony.GetPatchedMethods(), Is.Empty);
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.All.Contains("1.22.7"));
        });
    }

    private static IEnumerable<TestCaseData> Declining()
    {
        return
        [
            Case("visiblefaces", (h, l) => VisibleFaces.Install(h, l, VisibleFaces.Shape ^ 1),
                () => VisibleFaces.Installed),
            Case("occludedchunks", (h, l) => OccludedChunks.Install(h, l, OccludedChunks.Shape ^ 1),
                () => OccludedChunks.Installed),
            Case("extendedrows", (h, l) => ExtendedRows.Install(h, l, ExtendedRows.Shape ^ 1),
                () => ExtendedRows.Rewritten)
        ];

        static TestCaseData Case(string name, Action<Harmony, ILogger> install, Func<bool> installed) =>
            new TestCaseData(name, install, installed).SetArgDisplayNames(name);
    }
}

public sealed class WorkerPoolTests
{
    private const int Items = 4096;
    private static readonly int[] Hits = new int[Items];
    private static readonly int[] Threads = new int[Items];

    private static readonly Action<int> Count = i =>
    {
        _ = Interlocked.Increment(ref Hits[i]);
        Threads[i] = WorkerPool.Current;
        Thread.SpinWait(200);
    };

    private static readonly ManualResetEventSlim Never = new(false); // a background job that takes a second
    private static int _throwAt = -1, _background;

    private static readonly Action<int> Throwing = i =>
    {
        _ = Interlocked.Increment(ref Hits[i]);
        if (i == _throwAt) throw new InvalidOperationException("item " + i);
    };

    private static int _inside, _most;
    private int _limit;
    private Func<bool>? _saved;

    [SetUp]
    public void Save()
    {
        (_saved, _limit) = (WorkerPool.Background, WorkerPool.BackgroundLimit);
        (WorkerPool.Background, WorkerPool.BackgroundLimit, _background, _inside, _most) =
            (null, WorkerPool.MaxThreads, 0, 0, 0);
        Array.Clear(Hits);
        Array.Clear(Threads);
    }

    [TearDown]
    public void Restore()
    {
        Assert.That(WorkerPool.Stop(), Is.True, "every thread ended");
        (WorkerPool.Background, WorkerPool.BackgroundLimit) = (_saved, _limit);
    }

    [Test]
    public void WithoutThreadsTheBatchIsDeclined()
    {
        WorkerPool.Resize(null, 0);
        Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Declined));
        Assert.That(Hits.Sum(), Is.Zero);
    }

    [Test]
    public void EveryItemRunsOnceAndTheWorkersHelp()
    {
        WorkerPool.Resize(null, 4);
        for (var batch = 0; batch < 50; batch++)
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
        Assert.Multiple(() =>
        {
            Assert.That(Hits.All(n => n == 50), Is.True, "each item once per batch");
            Assert.That(Threads.Any(t => t >= 0), Is.True, "workers took items");
        });
    }

    // An open batch: the workers run its items while the caller goes on, the caller's own claims never pass their bound, and closing it
    // waits for every claimed item and drops the rest - each item ran at most once, and exactly the ones claimed before the close
    [Test]
    public void AnOpenBatchRunsBesideTheCallerAndClosesCleanly()
    {
        WorkerPool.Resize(null, 3);
        for (var batch = 0; batch < 20; batch++)
        {
            Array.Clear(Hits);
            Assert.That(WorkerPool.OpenFrame(Count, Items, WorkerPool.MaxThreads), Is.True);
            var bound = batch * Items / 20;
            var mine = WorkerPool.ClaimBelow(bound);
            var cut = batch % 2 == 0 ? Items / 3 : Items;
            _ = SpinWait.SpinUntil(() => Volatile.Read(ref Hits[cut - 1]) > 0 || cut < Items, 2000);
            Assert.That(WorkerPool.CloseFrame(), Is.EqualTo(WorkerPool.FrameResult.Done));
            var ran = Hits.Count(n => n > 0);
            Assert.Multiple(() =>
            {
                Assert.That(Hits.All(n => n <= 1), Is.True, "no item twice");
                Assert.That(mine, Is.LessThanOrEqualTo(bound), "the caller stopped at its bound");
                Assert.That(Hits.Take(ran).All(n => n == 1), Is.True, "the items that ran are the first ones claimed");
                if (cut == Items) Assert.That(ran, Is.EqualTo(Items), "a batch left open long enough runs whole");
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.CloseFrame(), Is.EqualTo(WorkerPool.FrameResult.Declined), "nothing is open");
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
        });
    }

    [Test]
    public void AnOpenBatchExcludesEveryOther()
    {
        WorkerPool.Resize(null, 2);
        Assert.That(WorkerPool.OpenFrame(Count, Items, WorkerPool.MaxThreads), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.OpenFrame(Count, Items, WorkerPool.MaxThreads), Is.False);
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Declined));
        });
        Assert.That(WorkerPool.CloseFrame(), Is.EqualTo(WorkerPool.FrameResult.Done));
    }

    [Test]
    [Category("Slow")]
    public void ABatchCompletesWhileEveryWorkerIsInABackgroundJob()
    {
        WorkerPool.Background = static () =>
        {
            _ = Interlocked.Increment(ref _background);
            _ = Never.Wait(1000);
            return true;
        };
        WorkerPool.Resize(null, 3);
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _background) >= 3, 1000);
        Assert.That(Volatile.Read(ref _background), Is.EqualTo(3), "every worker is inside a job");
        var watch = Stopwatch.StartNew();
        var result = WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads);
        watch.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(WorkerPool.FrameResult.Done));
            Assert.That(Hits.All(n => n == 1), Is.True);
            Assert.That(Threads.All(t => t < 0), Is.True, "no worker left its job for the batch");
            Assert.That(watch.ElapsedMilliseconds, Is.LessThan(500), "the caller did not wait for a background job");
        });
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _background) >= 6, 4000);
        Assert.That(Volatile.Read(ref _background), Is.GreaterThanOrEqualTo(6), "the background jobs go on");
    }

    // However many threads are free, no more than BackgroundLimit are inside a background job at once; frame batches still get every
    // thread that rests
    [Test]
    public void BackgroundJobsStayUnderTheLimit()
    {
        WorkerPool.BackgroundLimit = 2;
        WorkerPool.Background = static () =>
        {
            var inside = Interlocked.Increment(ref _inside);
            _ = InterlockedMax(inside);
            _ = Interlocked.Increment(ref _background);
            _ = Never.Wait(2);
            _ = Interlocked.Decrement(ref _inside);
            return true;
        };
        WorkerPool.Resize(null, 6);
        _ = SpinWait.SpinUntil(() => Volatile.Read(ref _background) >= 200, 5000);
        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref _background), Is.GreaterThanOrEqualTo(200), "the jobs run");
            Assert.That(Volatile.Read(ref _most), Is.EqualTo(2), "never more than the limit at once");
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
            Assert.That(Threads.Any(t => t >= 0), Is.True, "the threads left out of background work help the frame");
        });
    }

    private static int InterlockedMax(int value)
    {
        var seen = Volatile.Read(ref _most);
        for (var i = 0; i < 64 && value > seen; i++)
            seen = Interlocked.CompareExchange(ref _most, value, seen) == seen ? value : Volatile.Read(ref _most);
        return seen;
    }

    // An item that throws fails its batch after every other item ran; a second failure turns frame batches off until Stop
    [Test]
    public void FailedBatchesTurnFrameJobsOff()
    {
        WorkerPool.Resize(null, 3);
        _throwAt = 17;
        Assert.That(WorkerPool.RunFrame(Throwing, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Failed));
        Assert.Multiple(() =>
        {
            Assert.That(Hits.All(n => n == 1), Is.True, "the other items still ran");
            Assert.That(WorkerPool.LastError, Is.InstanceOf<InvalidOperationException>());
            Assert.That(WorkerPool.FrameOff, Is.False, "one failure only fails its batch");
        });
        Assert.That(WorkerPool.RunFrame(Throwing, Items, WorkerPool.MaxThreads),
            Is.EqualTo(WorkerPool.FrameResult.Failed));
        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.FrameOff, Is.True);
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Declined));
            Assert.That(WorkerPool.Running, Is.EqualTo(3), "the threads keep running for background jobs");
        });
        _throwAt = -1;
    }

    [Test]
    public void AThrowingBackgroundJobStopsOnlyBackgroundJobs()
    {
        WorkerPool.Background = static () => throw new InvalidOperationException("background");
        WorkerPool.Resize(null, 2);
        _ = SpinWait.SpinUntil(() => WorkerPool.BackgroundOff, 1000);
        Assert.Multiple(() =>
        {
            Assert.That(WorkerPool.BackgroundOff, Is.True);
            Assert.That(WorkerPool.Running, Is.EqualTo(2));
            Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads),
                Is.EqualTo(WorkerPool.FrameResult.Done));
        });
    }

    // Fewer threads park the rest; another world retires them all, and Stop joins every thread, retired ones included
    [Test]
    public void ParkingAndAnotherWorld()
    {
        var (first, second) = (new object(), new object());
        WorkerPool.Resize(first, 4);
        WorkerPool.Resize(first, 1);
        Assert.That(WorkerPool.Running, Is.EqualTo(1));
        WorkerPool.Resize(first, 3);
        Assert.That(WorkerPool.Running, Is.EqualTo(3), "parked threads are reused");
        WorkerPool.Resize(second, 2);
        Assert.That(WorkerPool.Running, Is.EqualTo(2));
        Assert.That(WorkerPool.RunFrame(Count, Items, WorkerPool.MaxThreads), Is.EqualTo(WorkerPool.FrameResult.Done));
        Assert.That(WorkerPool.Stop(), Is.True);
        Assert.That(WorkerPool.Running, Is.Zero);
    }
}
