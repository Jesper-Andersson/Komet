using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Chunk boxes of a pool's drawn ranges from the mirror's visible rows, not the location list walk (~1 ms a frame). Each
// range is checked against its row; a pool culled elsewhere falls back to the walk.
internal static partial class FrustumSweep
{
    // -1 when the mirror did not write them
    internal static int Boxes(MeshDataPool pool, Span<Occlusion.Range> into, (double X, double Y, double Z) camera)
    {
        if (!Enabled || !NotNull(pool) || Locations(pool) is not { } locations) return -1;
        var slot = SlotOf(pool);
        if (slot == Foreign || slot >= _mirrors.Length || _mirrors[slot] is not { } m ||
            !ReferenceEquals(m.Owner, locations) || m.ListVersion != Version(locations)) return -1;
        var (groups, starts, sizes) = (pool.indicesGroupsCount, pool.indicesStartsByte, pool.indicesSizes);
        if (groups > m.Emitted || groups > into.Length || !Assert(m.Emitted <= m.Rows.Length) ||
            starts.Length < 2 * groups || sizes.Length < groups) return -1;
        for (var g = 0; g < Math.Min(groups, MaxLocations); g++)
        {
            var row = m.Rows[g];
            if ((uint)row >= (uint)m.Length || m.Start[row] != starts[2 * g] || m.Count[row] != sizes[g]) return -1;
            var s = m.Slot[row];
            into[g] = Occlusion.Box((m.Cx[s], m.Cy[s], m.Cz[s]), (m.Hx[s], m.Hy[s], m.Hz[s]), sizes[g],
                starts[2 * g] / 4, camera);
        }

        return groups;
    }
}
