using System.Diagnostics;

namespace Komet.Vulkan;

// A frame is a few segments of Vulkan's own work (VulkanFrame.Only), each a submission of a setup (transfers) and its
// commands; they hold the images they use and put them back to rest as they close. Slots are reused once the frame-end
// fence passes. Only the recording thread submits, in post order.
internal sealed partial class VulkanFrame : IDisposable
{
    public const int Slots = 3, MaxSegments = 32, MaxImages = 1152; // handed with each segment: the targets' 80 and more
    private const ulong GpuTimeoutNs = 5_000_000_000; // a GPU that takes longer is hung, or waits on OpenGL that waits on it
    private const ulong UploadBytes = 32UL << 20;
    private const int MaxClosers = 64;

    private readonly VulkanDevice _device;
    private readonly List<Slot> _slots = [];
    private Slot? _slot;
    private Segment? _open;
    private int _next;

    private sealed class Segment(IntPtr commands, IntPtr setup)
    {
        public IntPtr Commands { get; } = commands; // the recording thread's: the draws
        public Handoff.Image[] Released { get; set; } = [];
        public IntPtr Setup { get; } = setup; // transfers that run before the segment's draws
    }

    private sealed class Slot(HostBuffer uploads, HostBuffer staging, ulong fence)
    {
        public HostBuffer Uploads { get; } = uploads;
        public HostBuffer Staging { get; } = staging; // bulk: only copied from
        public ulong Fence { get; } = fence;
        public List<Segment> Segments { get; } = [];
        public int Used { get; set; }
        public bool InFlight { get; set; } // the recording thread's, once it reached Signalled
        public long Signalled { get; set; } // the ops posted up to the fence's submission
        public Action? Signal { get; set; }

        // Transfers outside any segment (Alone)
        public List<IntPtr> Loose { get; } = [];
        public int LooseUsed { get; set; }

        public int Index { get; init; }
        public List<Stamp> Stamps { get; } = [];

        // Pipeline statistics: the frame is counted, its queries were reset, how many it began
        public bool Sampled { get; set; }
        public bool Reset { get; set; }
        public int Queries { get; set; }
    }

    // An image held by a segment: at rest between segments in its resting layout
    public readonly record struct Shared(SharedImage Image, int Resting);

    private VulkanFrame(VulkanDevice device)
    {
        _ = Assert(device.Handle != IntPtr.Zero) && Assert(Slots > 1);
        _device = device;
    }

    public Func<IReadOnlyList<Shared>> Images { get; set; } = () => [];

    // Run on segment open once its setup takes transfers, and at frame end with none open. Filling: the renderer's own,
    // before Opened
    public Action? Opened { get; set; }
    public Action? Ending { get; set; }
    public Action? Filling { get; set; }

    public bool Open => _open is not null;
    public IntPtr Setup => _open?.Setup ?? IntPtr.Zero;

    public long Serial { get; private set; }

    public HostBuffer? Uploads => _slot?.Uploads;

    // HostBuffer's bulk kind: the GPU copies it elsewhere before reading it
    public HostBuffer? Staging => _slot?.Staging;

    public long Frames { get; private set; }
    public long Number { get; private set; } // frames begun

    public int Used => _slot?.Used ?? 0;

    // The last frame the GPU finished for sure: the one whose slot the frame begun last waited for
    public long Done => Number - Slots;
    public long Segments { get; private set; }
    public long SubmitTicks { get; private set; }

    public Dictionary<string, long> Closers { get; } = new(StringComparer.Ordinal);

    public static VulkanFrame? Create(VulkanDevice device, out string why)
    {
        why = "";
        if (!NotNull(device)) return null;
        var frame = new VulkanFrame(device);
        _ = Assert(frame._slots.Count == 0);
        for (var i = 0; i < Slots; i++)
        {
            var (uploads, fence) = (HostBuffer.Create(device, UploadBytes), device.Fence(false));
            var staging = HostBuffer.Create(device, UploadBytes, bulk: true);
            if (uploads is not null && staging is not null && fence != 0)
            {
                frame._slots.Add(new Slot(uploads, staging, fence) { Index = i });
                continue;
            }

            uploads?.Dispose();
            staging?.Dispose();
            device.Destroy(fence);
            frame.Dispose();
            why = "no memory for a frame's uploads, or no fence";
            return null;
        }

        frame.StartWorker();
        device.CatchUp = frame.CaughtUp;
        return frame;
    }

    public void Next()
    {
        Close("the frame's end", endsFrame: true);
        if (_slot is not null) Ending?.Invoke(); // before the first frame there is nothing to end
        if (_slot is { } used && used.Used + used.LooseUsed > 0) // loose transfers too: their buffers are begun again
        {
            var (device, fence) = (_device, used.Fence);
            used.InFlight = false;
            Enqueue(new Op { Kind = OpKind.Queue, Ref = used.Signal ??= () => used.InFlight = device.Signal(fence) });
            used.Signalled = _posted;
            if (used.Used > 0) Frames++;
        }

        var slot = _slots[_next];
        _next = (_next + 1) % Slots;
        Number++;
        VulkanWatch.Mark("frame end: waiting for the GPU to finish the slot's last frame", Number);
        var finished = Await(slot);
        if (!finished && Fault.Length == 0) Fault = $"the GPU did not finish frame {Number - Slots} within 5 s";
        Timed(slot, finished);
        CountedFrame(slot, finished);
        VulkanWatch.Mark("frame begun", Number);
        (slot.InFlight, slot.Used, slot.LooseUsed) = (false, 0, 0);
        slot.Uploads.Reset();
        slot.Staging.Reset();
        (_slot, _ended) = (slot, false);
        _ = Assert(_open is null);
    }

    // At frame start, before the engine reads its clock: wait for the GPU to finish the slot's previous frame (else
    // Next waits mid-frame). Here GPU-bound frames start as evenly as the GPU finishes; mid-frame the wait only absorbs
    // prior CPU work, so frame starts (engine dt, camera step) jitter by CPU variance while presents stay GPU-paced.
    public void Pace()
    {
        if (_slot is null || _open is not null || EarlyBegun || Fault.Length > 0) return;
        var slot = _slots[_next];
        _ = Assert(slot.Index == _next) && Assert(Slots > 1);
        if (Await(slot)) slot.InFlight = false;
    }

    // The slot's last frame done on the GPU (false: not within GpuTimeoutNs)
    private bool Await(Slot slot)
    {
        Reach(slot.Signalled); // frames ago: made long since
        if (!slot.InFlight) return Assert(Index(slot.Index, Slots));
        var start = Hitches.Now;
        var finished = _device.Wait(slot.Fence, GpuTimeoutNs);
        Hitches.Since(Hitches.Kind.GpuWait, start);
        return finished;
    }

    // MaxOnly bounds a slot's command buffers far past any frame's: a frame can always be shown
    public bool Begin(out string why)
    {
        why = Fault;
        if (Fault.Length > 0) return false;
        if (_open is not null) return true;

        if (_slot is null) Next();
        else Early();
        if (!NotNull(_slot)) return false;
        var slot = _slot;
        _ = Assert(slot.Used <= slot.Segments.Count);
        if (slot.Used >= MaxOnly)
        {
            why = "the frame has used all its segments";
            return false;
        }

        VulkanWatch.Mark("segment opening");
        var segment = slot.Used < slot.Segments.Count ? slot.Segments[slot.Used] : Made(slot, out why);
        if (segment is null || !VulkanDevice.Begin(segment.Setup))
        {
            if (why.Length == 0) why = "no command buffer";
            return false;
        }

        Acquire();
        Enqueue(new Op { Kind = OpKind.Begin, Ref = segment }); // before anything recorded into it
        Ordered(segment.Setup);
        Transfers(segment.Setup, true);
        _open = segment;
        Serial++;
        TimingBegins(segment); // before anything Opened records
        CountingBegins(segment);
        Filling?.Invoke();
        Opened?.Invoke();
        return true;
    }

    private Segment? Made(Slot slot, out string why)
    {
        why = "";
        if (!NotNull(slot) || !Assert(slot.Segments.Count == slot.Used)) return null;
        Drain(); // the recording thread is done with its pool before a command buffer is made in it
        var (commands, setup) = (AllocateRecorded(), _device.Allocate());
        if (commands != IntPtr.Zero && setup != IntPtr.Zero)
        {
            var segment = new Segment(commands, setup);
            slot.Segments.Add(segment);
            return segment;
        }

        why = "no command buffer for a segment";
        return null;
    }

    // Submission is posted as the segment's last command: the recording thread ends the command buffer, submits, then
    // runs after, while the engine thread goes on. Other threads' queue operations wait for it (VulkanDevice.CatchUp),
    // for the submit only, not after (a present may wait for the display). A failed submission faults the frame.
    public void Close(string why, Action? after = null, bool endsFrame = false)
    {
        if (endsFrame && _slot is not null) _ended = true; // with no segment open too
        if (_open is not { } segment || _slot is not { } slot || !NotNull(why)) return;
        _ = Assert(slot.Used < MaxOnly);
        if (Closers.Count < MaxClosers || Closers.ContainsKey(why)) Closers[why] = Closers.GetValueOrDefault(why) + 1;
        EndRendering();
        Release(segment);
        Transfers(segment.Setup, false);
        TimingEnds(segment);
        var setupEnded = VulkanDevice.End(segment.Setup); // from the engine's thread's pool: ended here
        var showing = _showing;
        _showing = 0;
        ulong[] signals = showing == 0 ? [] : [showing];
        var (setup, device) = (segment.Setup, _device);
        Post(commands => Submitted(device, (setup, commands), signals, setupEnded));
        if (after is not null) Post(_ => After(after));
        (_open, _counting) = (null, -1); // a query is ended before the rendering is, unless the frame faulted
        Shown = showing != 0 && Fault.Length == 0;
        slot.Used++; // the command buffers were begun: the slot's fence covers them either way
        if (Fault.Length == 0) Segments++;
    }

    // On the recording thread
    private void Submitted(VulkanDevice device, (IntPtr Setup, IntPtr Commands) buffers, ulong[] signals, bool setupEnded)
    {
        _made = false;
        if (!Assert(buffers.Commands != IntPtr.Zero) || !NotNull(device)) return;
        var start = Stopwatch.GetTimestamp();
        _made = setupEnded && Fault.Length == 0 && device.Submit([buffers.Setup, buffers.Commands], [], signals, 0,
            ended: 1);
        SubmitTicks += Stopwatch.GetTimestamp() - start;
        if (!_made && Fault.Length == 0) Fault = "a segment's submission failed (the device is lost?)";
    }

    private bool _made; // the recording thread's: the last segment's submission went through

    private void After(Action after)
    {
        if (_made && NotNull(after) && Assert(Thread.CurrentThread == _worker)) after();
    }

    // Transfers need no segment, run alone in order; false without a buffer
    public bool Alone(Action<IntPtr> record)
    {
        if (!NotNull(record) || !Assert(_open is null)) return false;
        if (_slot is not { } slot || slot.LooseUsed >= MaxSegments || Fault.Length > 0) return false;
        if (slot.LooseUsed == slot.Loose.Count) slot.Loose.Add(_device.Allocate());
        var commands = slot.Loose[slot.LooseUsed];
        if (commands == IntPtr.Zero || !VulkanDevice.Begin(commands)) return false;
        Transfers(commands, true);
        record(commands);
        Transfers(commands, false);
        slot.LooseUsed++;
        if (!VulkanDevice.End(commands)) return false;
        var device = _device;
        Enqueue(new Op { Kind = OpKind.Queue, Ref = () => Loose(device, commands) }); // the frame's fence covers it
        return Assert(slot.LooseUsed <= MaxSegments);
    }

    // On the recording thread
    private void Loose(VulkanDevice device, IntPtr commands)
    {
        if (Fault.Length > 0 || !Assert(commands != IntPtr.Zero) || !NotNull(device)) return;
        if (!device.Submit([commands], [], [], 0, ended: 1))
            Fault = "a transfer's submission failed (the device is lost?)";
    }

    private static unsafe void Transfers(IntPtr commands, bool before)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(VkApi.CmdPipelineBarrier != null)) return;
        var memory = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier,
            SrcAccess = before ? Vk.AccessMemoryRead | Vk.AccessMemoryWrite : Vk.AccessTransferWrite,
            DstAccess = before ? Vk.AccessTransferWrite | Vk.AccessTransferRead : Vk.AccessMemoryRead | Vk.AccessMemoryWrite
        };
        var (from, to) = before ? (Vk.StageAll, Vk.StageTransfer) : (Vk.StageTransfer, Vk.StageAll);
        VkApi.CmdPipelineBarrier(commands, from, to, 0, 1, &memory, 0, null, 0, null);
    }

    // VulkanDevice.CatchUp: off the recording thread, it caught up (its submissions made)
    private void CaughtUp()
    {
        if (_worker is { } worker && Thread.CurrentThread != worker) Drain();
        _ = Assert(_worker is null || Thread.CurrentThread == _worker || Volatile.Read(ref _recorded) == _posted ||
                   Fault.Length > 0);
    }

    public unsafe void Dispose()
    {
        _ = Assert(_slots.Count <= Slots) && _device.WaitIdle();
        if (_device.CatchUp == CaughtUp) _device.CatchUp = null;
        StopWorker(); // after the GPU is done with the command buffers its pool frees
        if (_stamps != 0) VkApi.DestroyQueryPool(_device.Handle, _stamps, null);
        if (_statistics != 0) VkApi.DestroyQueryPool(_device.Handle, _statistics, null);
        (_stamps, _statistics) = (0, 0);
        (_open, _slot) = (null, null);
        foreach (var slot in _slots.Bounded(Slots))
        {
            slot.Uploads.Dispose();
            slot.Staging.Dispose();
            _device.Destroy(slot.Fence);
        }

        _slots.Clear();
    }
}
