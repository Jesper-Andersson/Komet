using System.Runtime.CompilerServices;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Rebuilds the terrain in-world: chunks forget pool locations, the chunk renderer and its pools go (GL buffers, Vulkan
// copies: VulkanRenderer.Disposing), a new renderer is made as after block texture load, and loaded chunks retessellate
// into fresh pools that take current switches (Vulkan-only packed faces, lean's buffers). Requested by the tools page
// and those switches; runs in next frame's Before stage, after pool writes flushed and before any pool is written.
internal static class TerrainRebuild
{
    public const double Order = 0.5; // after VulkanRenderer's StartingThen (0.01), before its Preparing (0.993)
    private static ICoreClientAPI? _api;
    private static Hook? _hook;
    private static bool _asked, _ready;

    public static long Rebuilds { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunks")]
    private static extern ref Dictionary<long, ClientChunk>? Chunks(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunksLock")]
    private static extern ref object? ChunksLock(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModelPoolLocations")]
    private static extern ref ModelDataPoolLocation[]? Center(ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModelPoolLocations")]
    private static extern ref ModelDataPoolLocation[]? Edge(ClientChunk chunk);

    public static void Install(ICoreClientAPI api)
    {
        if (!NotNull(api) || _hook is not null) return;
        (_api, _asked, _ready) = (api, false, false);
        _ = Assert(Rebuilds >= 0) && NotNull(api.Event);
        _hook = new Hook();
        api.Event.RegisterRenderer(_hook, EnumRenderStage.Before, "komet-rebuild");
    }

    public static void Stop()
    {
        if (_api is { } api && _hook is { } hook)
        {
            api.Event.UnregisterRenderer(hook, EnumRenderStage.Before);
            hook.Dispose();
        }

        (_api, _hook, _asked, _ready) = (null, null, false, false);
        _ = Assert(Rebuilds >= 0);
    }

    // From a world's second frame on (switches read at world load ask for nothing)
    public static void Request()
    {
        if (_ready && _api is not null) _asked = true;
        _ = Assert(!_asked || _ready);
    }

    internal static void Run()
    {
        if (!_ready)
        {
            _ready = true;
            return;
        }

        if (_api?.World is ClientMain world && !_asked)
        {
            PoolTrim.Run(_api, world);
            Komet.Chunks.ChunkPacking.Spares(world);
            if (Komet.Chunks.RecyclerTrim.Level > 0) Komet.Tessellation.ScratchTrim.Run(world);
        }

        if (!_asked || _api?.World is not ClientMain game || TerrainOf(game) is not { } old) return;
        _asked = false;
        var started = Environment.TickCount64;
        _ = Assert(started > 0) && NotNull(game.BlockAtlasManager);
        Komet.Vulkan.VulkanRenderer.Rebuilding();
        var chunks = Forgotten(game);
        old.Dispose();
        TerrainOf(game) = new ChunkRenderer([.. game.BlockAtlasManager.AtlasTextures.Select(static t => t.TextureId)], game);
        var repacked = Komet.Vulkan.VulkanRenderer.Rebuilt();
        OcclusionCulling.Renewed();
        FrustumSweep.Renewed();
        FaceSorting.Clear();
        FarShadowReuse.Clear();
        NearShadowReuse.Clear();
        game.RedrawAllBlocks();
        Rebuilds++;
        _ = Assert(Rebuilds > 0) && Assert(!_asked);
        game.Logger.Notification("Komet: the terrain is made anew: {0} chunks tessellated again into new pools ({1} ms){2}", chunks,
            Environment.TickCount64 - started, repacked ? "" : ", the face data packed as before (a pool was still held)");
    }

    // Loaded and packed chunks (protocol, main thread)
    internal static (int Packed, int Total) Census(ClientMain game)
    {
        if (!NotNull(game) || game.WorldMap is not { } map || Chunks(map) is not { } all || ChunksLock(map) is not { } gate) return (0, 0);
        var (packed, total) = (0, 0);
        lock (gate)
        {
            using var chunks = all.Values.GetEnumerator();
            for (var i = 0; i < 1 << 22 && chunks.MoveNext(); i++)
            {
                total++;
                if (chunks.Current?.IsPacked() == true) packed++;
            }
        }

        return Assert(packed <= total) ? (packed, total) : (0, 0);
    }

    // Loaded chunks without pool locations: they lay in the pools about to go
    private static int Forgotten(ClientMain game)
    {
        var map = game.WorldMap;
        if (!NotNull(map) || Chunks(map) is not { } all || ChunksLock(map) is not { } gate) return 0;
        var count = 0;
        lock (gate)
        {
            using var chunks = all.Values.GetEnumerator();
            for (var i = 0; i < 1 << 22 && chunks.MoveNext(); i++)
            {
                if (chunks.Current is not { } chunk) continue;
                (Center(chunk), Edge(chunk)) = (null, null);
                count++;
            }
        }

        return Assert(count >= 0) ? count : 0;
    }

    private sealed class Hook : IRenderer
    {
        public double RenderOrder => Order;
        public int RenderRange => 0;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (Assert(stage == EnumRenderStage.Before) && Assert(deltaTime >= 0)) Run();
        }

        public void Dispose() => _ = Assert(RenderRange == 0) && Assert(Order > 0);
    }
}
