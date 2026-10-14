using System.Runtime.CompilerServices;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Common to cube, topsoil and cross replacements: switch and stand-down, counting, and the replaced engine tesselator
// for every block Draw declines (Draw writes nothing before declining)
internal abstract class OwnSlot(IBlockTesselator engine) : IBlockTesselator
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Tesselate(TCTCache vars)
    {
        _ = Assert(engine is not OwnSlot); // Swap replaces only the engine's own types
        if (OwnTessellation.Active && NotNull(vars) && vars.block is { } block && Draw(vars, block))
        {
            OwnTessellation.Count(true);
            return;
        }

        OwnTessellation.Count(false);
        engine.Tesselate(vars);
    }

    // The block drawn, or false with nothing written
    protected abstract bool Draw(TCTCache vars, Block block);
}
