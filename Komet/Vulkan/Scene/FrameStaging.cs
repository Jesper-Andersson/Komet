namespace Komet.Vulkan;

// A large upload gets its own buffer, kept until the GPU finishes the frame that took it
internal sealed class FrameStaging(VulkanDevice device, VulkanFrame frame) : IDisposable
{
    private const int MaxOwn = 64;

    private readonly List<(HostBuffer Buffer, long Frame)> _own = [];

    public HostBuffer? Take(int bytes, out long at, int align = 16)
    {
        at = -1;
        if (!Assert(bytes > 0) || !Assert(_own.Count <= MaxOwn)) return null;
        if (frame.Staging is { } uploads && (at = uploads.Take(bytes, out _, align)) >= 0) return uploads;
        if (_own.Count >= MaxOwn || HostBuffer.Create(device, (ulong)bytes, bulk: true) is not { } own) return null;
        _own.Add((own, frame.Number));
        at = own.Take(bytes, out _, align);
        return at >= 0 ? own : null;
    }

    public void Collect()
    {
        var kept = 0;
        for (var i = 0; i < Math.Min(_own.Count, MaxOwn); i++)
        {
            if (_own[i].Frame <= frame.Done) _own[i].Buffer.Dispose();
            else _own[kept++] = _own[i];
        }

        _own.RemoveRange(kept, _own.Count - kept);
        _ = Assert(_own.Count <= MaxOwn) && Assert(kept >= 0);
    }

    public void Dispose()
    {
        _ = Assert(_own.Count <= MaxOwn) && device.WaitIdle();
        foreach (var (buffer, _) in _own.Bounded(MaxOwn)) buffer.Dispose();
        _own.Clear();
    }
}
