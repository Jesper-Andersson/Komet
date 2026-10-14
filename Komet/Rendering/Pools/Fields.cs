using System.Runtime.CompilerServices;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// Engine private fields shared by culling and pool patches (an accessor on a missing field throws at first use)
internal static class Fields
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    internal static extern ref ClientMain? Game(ClientSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentRenderStage")]
    internal static extern ref EnumRenderStage Stage(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkRenderer")]
    internal static extern ref ChunkRenderer? TerrainOf(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolsByRenderPass")]
    internal static extern ref MeshDataPoolManager[][] PassPools(ChunkRenderer renderer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
    internal static extern ref List<MeshDataPool> Pools(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    internal static extern ref List<ModelDataPoolLocation> Locations(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dimensionId")]
    internal static extern ref int Dimension(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelRef")]
    internal static extern ref MeshRef ModelRef(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolOrigin")]
    internal static extern ref Vec3i? PoolOrigin(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolId")]
    internal static extern ref int PoolId(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    internal static extern ref int Version(List<ModelDataPoolLocation> list);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustumCuller")]
    internal static extern ref FrustumCulling Culler(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustum")]
    internal static extern ref Plane[] Planes(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    internal static extern ref BlockPos PlayerPos(FrustumCulling culler);
}
