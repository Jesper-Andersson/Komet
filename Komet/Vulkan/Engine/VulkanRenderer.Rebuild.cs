namespace Komet.Vulkan;

// TerrainRebuild's Vulkan side. Before pools go: frame closed, GPU idle, pending pool writes dropped (pools go with
// their VAO, Disposing). After: pools packed per CompactFaces, chunk programs re-ported on change.
internal static partial class VulkanRenderer
{
    internal static void Rebuilding()
    {
        if (_renderer is not { } renderer || !NotNull(renderer.Frame)) return;
        if (renderer.Frame.Open) renderer.Frame.Close("the terrain rebuilt");
        renderer.PoolWritesDropped();
        _ = renderer.Device.WaitIdle() && Assert(!renderer.Frame.Open);
    }

    // The pools are gone: false when one is still held (the packing stays as it was)
    internal static bool Rebuilt()
    {
        if (_renderer is not { } renderer) return true;
        var compact = CompactFaces.Enabled;
        _ = Assert(Programs.Length == Built.Length) && Assert(Ports.Length == Porting.Length);
        if (renderer.Pools.Compact == compact) return true;
        if (!renderer.Pools.Repack(compact)) return false;
        CompactFaces.Active = renderer.Pools.Compact;
        for (var i = 0; i < Programs.Length; i++)
        {
            if (!CompactFaces.Reads(Programs[i].Name) || Built[i] <= 0) continue;
            _scene?.Forget(Built[i]);
            renderer.Forget(Built[i]);
            Porting[i]?.Drop();
            (Built[i], Ports[i], Porting[i]) = (0, null, null); // Update ports it again from the next frame
        }

        return Assert(CompactFaces.Active == compact);
    }
}
