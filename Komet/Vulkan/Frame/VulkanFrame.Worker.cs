namespace Komet.Vulkan;

// Recorded on its own thread, like glthread for GL: it begins, records, ends and submits segment command buffers from
// its own pool (one thread per pool) and makes queue operations in post order (Queue). The engine thread waits only
// where a submission must exist (Reach): GL's segment wait, other threads' queue ops, arena reuse.
internal sealed unsafe partial class VulkanFrame
{
    public const int MaxOps = 1 << 15, MaxDescriptors = 1 << 18, MaxVertexBindings = 1 << 16, MaxPushWords = 1 << 19;
    private const int SpinCycles = 200, MaxWaitMs = 5000;

    public enum OpKind : byte
    {
        EndRendering,
        Barrier,
        BeginRendering,
        Viewport,
        Scissor,
        Draw,
        Release,
        Clear,
        BeginQuery,
        EndQuery,
        LineWidth,
        Timestamp,
        Copy, // CountOffset bytes from Index at IndexOffset to Indirect at IndirectOffset: no closure per copy
        Blit, // VulkanFrame.Blit's: the images in Pipeline and Layout, the corners in X..Height and Index..IndirectOffset
        Fill, // zeros, CountOffset bytes into Indirect at IndirectOffset, then compute's order (VulkanBackend.Ordered)
        Call,
        Begin, // the frame's own: a segment's command buffer begun (Ref the segment)
        Queue // the frame's own: a queue operation (Ref an Action), made even after a fault
    }

    public enum DrawKind : byte
    {
        Direct,
        Indexed,
        IndexedIndirect,
        IndexedIndirectCount, // Count the most, the count read at CountBuffer + CountOffset
        Indirect, // Vulkan-only's, from mods: glDrawArraysIndirect's commands, Count of them First bytes apart
        IndirectCount
    }

    public struct Op
    {
        public OpKind Kind;
        public DrawKind Draw;
        public uint IndexType;
        public ulong Pipeline, Layout, Index, IndexOffset, Indirect, IndirectOffset, CountBuffer, CountOffset;
        public int Descriptors, DescriptorCount, Push, PushBytes, Vertices, VertexCount;
        public uint Count, Instances, First, BaseInstance;
        public int BaseVertex;
        public float X, Y, Width, Height;
        public object? Ref;
    }

    public struct Descriptor
    {
        public uint Binding, Type;
        public Vk.BufferDescriptor Buffer;
        public Vk.ImageDescriptor Image;
    }

    // Rendering and Moves go back to the frame once recorded (Spare): a segment switch would make two each otherwise
    public sealed class Rendering
    {
        public readonly ulong[] Colors = new ulong[MaxColors];
        public int Count;
        public ulong Depth;
        public int DepthLayout;
        public uint Width, Height;
        public bool Stencil; // the depth's view holds a stencil too: attached as the stencil as well
        public uint Layers = 1; // a layered rendering's (every layer of an array attached at once)
    }

    public sealed record Clearing(Vk.ClearAttachment[] Attachments, Vk.Rect Rect, uint Layers = 1);

    // Also orders all memory before it against all after
    public sealed class Moves
    {
        public (ulong Image, uint Aspect, int Levels, int Layers, int From, int To)[] Images = [];
        public int Count;

        public Moves() { }

        public Moves(params (ulong Image, uint Aspect, int Levels, int Layers, int From, int To)[] images)
        {
            (Images, Count) = (images, images.Length);
            _ = NotNull(images) && Assert(Count <= MaxImages);
        }
    }

    private const int MaxSpare = 64;

    // the recording thread gives back, the frame's thread takes
    private readonly Core.BoundedPool<Rendering> _spareRenderings = new(MaxSpare);
    private readonly Core.BoundedPool<Moves> _spareMoves = new(MaxSpare);

    private static T Spared<T>(Core.BoundedPool<T> spare) where T : class, new() => spare.Take() ?? new T();

    private void Spare(object? recorded)
    {
        if (recorded is Rendering rendering && Assert(rendering.Count <= MaxColors)) _spareRenderings.Give(rendering);
        else if (recorded is Moves moves && Assert(moves.Count <= moves.Images.Length)) _spareMoves.Give(moves);
    }

    private readonly Op[] _ops = new Op[MaxOps];
    private readonly Descriptor[] _descriptors = new Descriptor[MaxDescriptors];
    private readonly (uint Location, ulong Buffer, ulong Offset)[] _vertexBindings =
        new (uint, ulong, ulong)[MaxVertexBindings];
    private readonly uint[] _push = new uint[MaxPushWords];
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _worker;
    private long _posted, _recorded;
    private int _descriptorsUsed, _vertexBindingsUsed, _pushUsed, _sleeping, _stop;
    private IntPtr _recording;
    private ulong _workerPool;

    // Recording thread faults end its segment's work: nothing is recorded any more, the segments stay empty
    public string Fault { get; private set; } = "";

    private void StartWorker()
    {
        if (!Assert(_worker is null) || !Assert(_device.Handle != IntPtr.Zero)) return;
        _workerPool = _device.CommandPool();
        _worker = new Thread(Work) { IsBackground = true, Name = "komet-vulkan-record", Priority = ThreadPriority.AboveNormal };
        _worker.Start();
    }

    private IntPtr AllocateRecorded() =>
        Assert(_workerPool != 0) && Assert(_worker is not null) ? _device.Allocate(_workerPool) : IntPtr.Zero;

    public (int Descriptors, int Vertices, int Push) Room(int descriptors, int vertices, int pushWords)
    {
        if (!Assert(descriptors is >= 0 and <= 64 && vertices is >= 0 and <= 16 && pushWords is >= 0 and <= 64))
            return (0, 0, 0);
        if (_descriptorsUsed + descriptors > MaxDescriptors || _vertexBindingsUsed + vertices > MaxVertexBindings ||
            _pushUsed + pushWords > MaxPushWords)
        {
            Drain();
            (_descriptorsUsed, _vertexBindingsUsed, _pushUsed) = (0, 0, 0);
        }

        var room = (_descriptorsUsed, _vertexBindingsUsed, _pushUsed);
        _ = Assert(room.Item1 + descriptors <= MaxDescriptors) && Assert(room.Item3 + pushWords <= MaxPushWords);
        (_descriptorsUsed, _vertexBindingsUsed, _pushUsed) =
            (_descriptorsUsed + descriptors, _vertexBindingsUsed + vertices, _pushUsed + pushWords);
        return room;
    }

    public Span<Descriptor> Descriptors(int at, int count) =>
        Assert(at >= 0) && Assert(count >= 0 && at + count <= MaxDescriptors) ? _descriptors.AsSpan(at, count) : [];

    public Span<(uint Location, ulong Buffer, ulong Offset)> VertexBindings(int at, int count) =>
        Assert(at >= 0) && Assert(count >= 0 && at + count <= MaxVertexBindings) ? _vertexBindings.AsSpan(at, count) : [];

    public Span<uint> PushWords(int at, int count) =>
        Assert(at >= 0) && Assert(count >= 0 && at + count <= MaxPushWords) ? _push.AsSpan(at, count) : [];

    // One command for the open segment's command buffer, recorded on the recording thread after every one posted before
    public void Post(Op op)
    {
        if (!Assert(_open is not null) || !Assert(op.Kind <= OpKind.Call) || Fault.Length > 0) return;
        Counting(op);
        Enqueue(op);
    }

    public void Post(Action<IntPtr> record)
    {
        if (NotNull(record) && Assert(_open is not null)) Post(new Op { Kind = OpKind.Call, Ref = record });
    }

    private void Enqueue(Op op)
    {
        _ = Assert(op.Kind <= OpKind.Queue);
        if (_posted - Volatile.Read(ref _recorded) >= MaxOps) Drain();
        _ops[_posted % MaxOps] = op;
        // Both store then load the other's value (Sleep): full fences, else both read stale and it sleeps until Reach
        _ = Interlocked.Exchange(ref _posted, _posted + 1);
        if (Volatile.Read(ref _sleeping) != 0) _wake.Set();
        _ = Assert(_posted - Volatile.Read(ref _recorded) <= MaxOps);
    }

    public void Drain() => Reach(_posted);

    // Until the thread recorded the first count posted
    private void Reach(long count)
    {
        if (_worker is null || Volatile.Read(ref _recorded) >= count) return;
        _ = Assert(Thread.CurrentThread != _worker) && Assert(count <= _posted); // the thread would wait for itself
        _wake.Set();
        var (spin, start) = (new SpinWait(), Hitches.Now);
        for (var i = 0; i < int.MaxValue && Volatile.Read(ref _recorded) < count; i++)
        {
            spin.SpinOnce(-1);
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds <= MaxWaitMs) continue;
            Fault = "the recording thread did not catch up";
            break;
        }

        Hitches.Since(Hitches.Kind.RecorderWait, start);
        _ = Assert(Fault.Length > 0 || Volatile.Read(ref _recorded) >= count);
    }

    private void Work()
    {
        _ = Assert(Thread.CurrentThread == _worker) && Assert(_workerPool != 0);
        var spin = new SpinWait();
        for (var i = 0L; i < long.MaxValue && Volatile.Read(ref _stop) == 0; i++)
        {
            var read = _recorded;
            if (read == Volatile.Read(ref _posted))
            {
                if (spin.Count < SpinCycles)
                {
                    spin.SpinOnce(-1);
                    continue;
                }

                Sleep();
                spin.Reset();
                continue;
            }

            spin.Reset();
            try
            {
                ref var op = ref _ops[read % MaxOps];
                if (Fault.Length == 0 || op.Kind == OpKind.Queue) Emit(ref op);
            }
            catch (Exception e) when (e is not OutOfMemoryException) // unhandled on this thread it would end the game
            {
                Fault = $"the recording thread failed: {e.Message}";
            }

            Spare(_ops[read % MaxOps].Ref);
            _ops[read % MaxOps].Ref = null;
            Volatile.Write(ref _recorded, read + 1);
        }
    }

    private void Sleep()
    {
        _ = Assert(Volatile.Read(ref _sleeping) == 0) && Assert(Thread.CurrentThread == _worker);
        _wake.Reset();
        _ = Interlocked.Exchange(ref _sleeping, 1);
        if (Volatile.Read(ref _recorded) == Volatile.Read(ref _posted) && Volatile.Read(ref _stop) == 0) _ = _wake.Wait(100);
        Volatile.Write(ref _sleeping, 0);
    }

    private void StopWorker()
    {
        if (_worker is not { } worker || !Assert(Thread.CurrentThread != worker)) return;
        Volatile.Write(ref _stop, 1);
        _wake.Set();
        _worker = null;
        if (!Assert(worker.Join(MaxWaitMs))) return; // leaked: neither the pool nor the wake goes under a thread still recording
        if (_workerPool != 0) _device.DestroyPool(_workerPool);
        _workerPool = 0;
        _wake.Dispose();
    }
}
