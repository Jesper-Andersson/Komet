namespace Komet.Vulkan;

// Vulkan-only: a draw sampling an image it draws into (mip chain built level by level; feedback is undefined in OpenGL)
// samples a snapshot: base to max level, all layers, copied in the open segment just before, viewed from base. Kept per
// image, retired after the image goes. Scene and pool draws alike.
internal sealed partial class TerrainRenderer
{
    private const int MaxSnapshots = 32, BaseLevelName = 0x813C, MaxLevelName = 0x813D;

    private readonly Dictionary<SharedImage, SharedImage> _snapshots = new(ReferenceEqualityComparer.Instance);
    private readonly Retired<SharedImage> _retiredSnapshots = new(4 * MaxSnapshots, static image => image.Dispose());

    public long Snapshots { get; private set; }

    // Held by the open segment; null when none can be had
    public SharedImage? Snapshot(SharedImage image)
    {
        if (!NotNull(image) || !Frame.Open || !Frame.Holds(image)) return null;
        var copy = _snapshots.GetValueOrDefault(image) ?? Snapshotted(image);
        if (copy is null) return null;
        Hold(copy);
        var (first, last) = SampledLevels(image);
        var regions = new Vk.ImageCopy[Math.Clamp(Math.Min(last, Math.Min(image.Levels, 16) - 1) - first + 1, 0, 16)];
        for (var level = 0; level < Math.Min(image.Levels, 16); level++)
        {
            if (level < first || level > last || level - first >= regions.Length) continue; // not sampled: stays as it was
            var (width, height) = image.LevelSize(level);
            var layers = new Vk.Layers
            {
                Aspect = image.Format.Aspect, Level = (uint)level, Count = (uint)image.Layers
            };
            regions[level - first] = new Vk.ImageCopy
            {
                Source = layers, Target = layers, Width = (uint)width, Height = (uint)height, Depth = 1
            };
        }

        if (!Frame.CopyImage(image, copy, regions)) return null; // the levels in one copy behind one barrier

        copy.Window = (first, last - first + 1); // sampled through those levels only, as OpenGL samples the texture
        Snapshots++;
        return Assert(copy.Levels == image.Levels) ? copy : null;
    }

    // Base to max level of the texture; all when unknown
    private static (int First, int Last) SampledLevels(SharedImage image)
    {
        var last = image.Levels - 1;
        if (last <= 0 || image.Texture <= 0 || !Assert(last < 16)) return (0, Math.Max(last, 0));
        var first = Math.Clamp(GlTexture.Parameter(image.Texture, BaseLevelName), 0, last);
        var to = Math.Clamp(GlTexture.Parameter(image.Texture, MaxLevelName), first, last);
        _ = Assert(first <= to);
        return (first, to);
    }

    private readonly List<SharedImage> _unsnapped = [];

    // Pool draw's textures with feedback images swapped for snapshots (as the scene's Unfed); null when impossible
    private TerrainDraw.Program? Snapshotted(Program program, TerrainDraw.Program recorded, VulkanFrame.Target target,
        out string why)
    {
        why = "";
        if (!NotNull(program) || !Fed(target, _sampled)) return recorded;
        _unsnapped.Clear();
        _unsnapped.AddRange(_sampled);
        if (!Unfed(target, _sampled, out why)) return null;
        var count = Math.Min(program.Ported.Samplers.Length, MaxSamplers);
        for (var j = 0; j < Math.Min(_unsnapped.Count, MaxSamplers); j++)
        {
            if (ReferenceEquals(_unsnapped[j], _sampled[j])) continue;
            for (var i = 0; i < Math.Min(count, MaxSamplers); i++)
                if (_resolved[i].View == _unsnapped[j].Sampled)
                    _resolved[i] = new TerrainDraw.Texture(_sampled[j].Sampled, _resolved[i].Sampler, Vk.LayoutShaderRead);
        }

        return Assert(_unsnapped.Count == _sampled.Count) ? program.Recorded(_resolved.AsSpan(0, count)) : null;
    }

    private SharedImage? Snapshotted(SharedImage image)
    {
        if (_snapshots.Count >= MaxSnapshots) Unsnapshot();
        if (_snapshots.Count >= MaxSnapshots || !Assert(image.Image != 0) || !Assert(image.Levels > 0)) return null;
        var copy = SharedImage.Private(Device, (image.Width, image.Height, image.Levels, image.Layers), image.Format,
            Vk.Sampled | Vk.TransferDst, out _, image.Cube, 0, image.Arrayed);
        if (copy is null) return null;
        copy.OpaqueAlpha = image.OpaqueAlpha;
        if (!copy.Rest(Device, Vk.LayoutUndefined, Vk.LayoutShaderRead))
        {
            copy.Dispose();
            return null;
        }

        _snapshots[image] = copy;
        return copy;
    }

    private void Unsnapshot()
    {
        foreach (var (image, copy) in _snapshots.ToArray().Bounded(MaxSnapshots))
        {
            if (image.Image != 0) continue;
            _ = _snapshots.Remove(image);
            if (_retiredSnapshots.Add(copy, Frame.Number)) continue;
            _ = Device.WaitIdle(); // too many waiting: this one goes now
            copy.Dispose();
        }

        _ = Assert(_snapshots.Count <= MaxSnapshots) && Assert(_retiredSnapshots.Count <= 4 * MaxSnapshots);
    }

    private void CollectSnapshots(bool all)
    {
        if (all) _retiredSnapshots.Clear();
        else _retiredSnapshots.Collect(Frame.Done);
        _ = Assert(_retiredSnapshots.Count <= 4 * MaxSnapshots) && Assert(Frame.Done >= -VulkanFrame.Slots);
        if (!all) return;
        foreach (var copy in _snapshots.Values.ToArray().Bounded(MaxSnapshots)) copy.Dispose();
        _snapshots.Clear();
    }

    // Each sampled image the draw also draws into is replaced by its snapshot in the open segment; false when
    // impossible
    public bool Unfed(VulkanFrame.Target target, List<SharedImage> sampled, out string why)
    {
        why = "";
        if (!Fed(target, sampled)) return true;
        if (!Frame.Open && !Frame.Begin(out why)) return false;
        Holding(target, sampled);
        for (var i = 0; i < Math.Min(sampled.Count, MaxSamplers); i++)
        {
            if (!Feeds(target, sampled[i])) continue;
            if (Snapshot(sampled[i]) is not { } copy) return Refuse(out why, "no snapshot of an image it samples and draws into");
            sampled[i] = copy;
        }

        return Assert(Frame.Open) && Assert(sampled.Count <= MaxSamplers);
    }

    // Asked by every draw, so a loop, no closure
    public static bool Fed(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        if (!NotNull(target) || !Assert(sampled.Count <= MaxSamplers)) return false;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (Feeds(target, image))
                return true;
        return false;
    }

    private static bool Feeds(VulkanFrame.Target target, SharedImage image) =>
        NotNull(image) && NotNull(target) && (Array.IndexOf(target.Colors, image) >= 0 || (image == target.Depth && !target.DepthReadOnly));
}
