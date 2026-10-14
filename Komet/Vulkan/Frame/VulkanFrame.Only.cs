namespace Komet.Vulkan;

// Nothing is shared with GL, so no semaphores either way and GL calls never close a segment. The frame is one segment
// from Vulkan's first work to present, split only by Vulkan's own needs (a query result, a resource released, a new
// image when full); each setup then starts with a full memory barrier instead of a semaphore. MaxOnly only bounds a
// slot's command buffers.
internal sealed unsafe partial class VulkanFrame
{
    // MaxHeld: the images a segment holds, which is the frame's
    public const int MaxOnly = 1024, MaxHeld = 4096;

    // Frame ended, next not begun: work in between (GUI, Before's first renderers) begins the next frame on its first
    // segment, so nothing records into a shown slot and Start finds it begun (Fresh)
    private bool _ended;

    public bool EarlyBegun { get; private set; }

    // The renderer's Start: the next frame, unless work before it began it already
    public void Fresh()
    {
        if (EarlyBegun) EarlyBegun = false;
        else Next();
        _ = Assert(!EarlyBegun) && Assert(_slot is not null || Fault.Length > 0);
    }

    // Frames begun this way: from inside an OpenGL call (a draw between frames), the GPU wait for the slot included
    public long EarlyBegins { get; private set; }

    private bool _beginning; // Next runs for Early: what it ends (Ending) must not begin a frame again

    private void Early()
    {
        if (!_ended || _beginning || Fault.Length > 0 || !Assert(_open is null)) return;
        _beginning = true;
        try
        {
            Next();
        }
        finally
        {
            _beginning = false;
        }

        EarlyBegun = _slot is not null;
        EarlyBegins++;
        _ = Assert(!_ended || Fault.Length > 0);
    }

    private static void Ordered(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(VkApi.CmdPipelineBarrier != null)) return;
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var memory = new Vk.GlobalBarrier { SType = Vk.MemoryBarrier, SrcAccess = all, DstAccess = all };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 1, &memory, 0, null, 0, null);
    }
}
