using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Komet.Core;
using Vintagestory.API.Datastructures;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Between culls a list only gains and loses entries (no row rewritten after pooling), so it is diffed: survivors
// matched by reference keep their slot, only new rows are read.
internal static partial class FrustumSweep
{
    // By pool id; the first list keeps an id, another list under it is keyed weakly by list
    private static Mirror?[] _mirrors = new Mirror[64];
    private static readonly ConditionalWeakTable<List<ModelDataPoolLocation>, Mirror> Unnumbered = [];

    private static Mirror? MirrorOf(int slot, List<ModelDataPoolLocation> locations) =>
        Owned(slot, locations) is { } m ? Update(m, locations) : null;

    // Stage batches run this on workers, one job per mirror (lists change only before stages; counts Interlocked)
    private static Mirror? Update(Mirror m, List<ModelDataPoolLocation> locations)
    {
        if (!NotNull(m) || !NotNull(locations) || !Assert(locations.Count <= MaxLocations)) return null;
        var current = CollectionsMarshal.AsSpan(locations);
        var same = ReferenceEquals(m.Owner, locations);
        if (same && Version(locations) == m.ListVersion)
        {
            Tidy(m, true);
            return m;
        }

        var diffed = same && m.Length > 0 && Diff(m, current);
        if ((!diffed && !Rebuild(m, locations, current)) || !Assert(m.Length == current.Length)) return null;
        (m.ListVersion, m.Quiet) = (Version(locations), 0);
        if (Counting.Hud)
        {
            _ = Interlocked.Increment(ref _rebuilds);
            if (diffed) _ = Interlocked.Increment(ref _diffed);
            _ = Assert(Interlocked.Increment(ref _frameRebuilds) > 0);
        }

        if (diffed) Tidy(m, false);
        return m;
    }

    private static Mirror? Owned(int slot, List<ModelDataPoolLocation> locations)
    {
        if (!NotNull(locations)) return null;
        if (slot == Foreign) return Unnumbered.GetOrCreateValue(locations);
        if (!Index(slot, MaxPools)) return null;
        if (slot >= _mirrors.Length) Array.Resize(ref _mirrors, Capacity(slot + 1, _mirrors.Length, MaxPools));
        var m = _mirrors[slot] ??= new Mirror();
        return m.Owner is null || ReferenceEquals(m.Owner, locations) ? m : Unnumbered.GetOrCreateValue(locations);
    }

    private static bool Rebuild(Mirror m, List<ModelDataPoolLocation> owner,
        ReadOnlySpan<ModelDataPoolLocation> locations)
    {
        var (n, w) = (locations.Length, Vector<double>.Count);
        (m.Owner, m.Length) = (null, 0); // unowned until every row is in
        if (!NotNull(owner) || !Assert(n <= MaxLocations) || !Grow(m, n, w)) return false;
        Array.Clear(m.Refs, n, m.Refs.Length - n); // no reference keeps a removed location alive
        var (allocated, ordered) = (0, true);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var loc = locations[i];
            // Emit reads Hide and CullVisible: null goes to the engine, which throws as it would
            if (!NotNull(loc) || !NotNull(loc.CullVisible))
            {
                Array.Clear(m.Refs, 0, i);
                return false;
            }

            // LodLevel and CullVisible are written once, right after InsertAt
            m.Refs[i] = new LocRef(loc, loc.CullVisible);
            (m.Start[i], m.Count[i], m.Lod[i]) =
                (loc.IndicesStart * 4, loc.IndicesEnd - loc.IndicesStart, loc.LodLevel);
            allocated += m.Count[i] / 3;
            ordered &= i == 0 || m.Start[i - 1] <= m.Start[i];
            Place(m, loc.FrustumCullSphere, i);
            (m.Slot[i], m.Inv[i]) = (i, i);
        }

        (m.Owner, m.Length, m.AllocatedTris, m.Ordered) = (owner, n, allocated, ordered);
        (m.Cells, m.Dead, m.Fresh, m.Tried) = (0, 0, 0, false);
        Tail(m, n, w);
        return true;
    }

    // false when a rebuild is cheaper
    private static bool Diff(Mirror m, ReadOnlySpan<ModelDataPoolLocation> list)
    {
        var (n, w) = (list.Length, Vector<double>.Count);
        // a Settle over rows beyond Grow's sizing would find no room for its grid padding
        if (!Assert(m.Length <= m.Refs.Length) || !Assert(m.NRefs.Length == m.Refs.Length) || n == 0 ||
            n > m.Refs.Length || Geometry(n, w) > m.Cx.Length) return false;
        var (j, fresh, budget, old) = (0, 0, ScanBudget * (long)n + MaxLocations / 16, m.Length);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var p = j < old && ReferenceEquals(m.Refs[j].Loc, list[i]) ? j : Find(m, list[i], j, ref budget);
            if (p == Lost) return false;
            m.Match[i] = p;
            if (p == Unseen) fresh++;
            else j = p + 1;
        }

        var slots = (m.Used + fresh + w - 1) / w * w;
        return 4 * fresh <= n + Churn && slots <= m.Cx.Length && Merge(m, list, fresh, slots);
    }

    // Location's old row from j on; Unseen when new, Lost when searching costs more than a rebuild
    private static int Find(Mirror m, ModelDataPoolLocation? target, int j, ref long budget)
    {
        var old = m.Length;
        // the engine throws on a null; Rebuild lets it
        if (!NotNull(target) || !Assert(old <= m.Refs.Length)) return Lost;
        var near = Math.Min(old, j + Window);
        for (var p = j; p < Math.Min(near, MaxLocations); p++)
            if (ReferenceEquals(m.Refs[p].Loc, target)) return p;
        if (near >= old) return Unseen;
        if (m.Ordered) return Search(m, target, near);
        budget -= old - near;
        if (budget < 0) return Lost;
        for (var p = near; p < Math.Min(old, MaxLocations); p++)
            if (ReferenceEquals(m.Refs[p].Loc, target)) return p;
        return Unseen;
    }

    // Ordered by IndicesStart (TryAppend past the last, TrySqueezeInbetween behind the gap); equal starts by ref
    private static int Search(Mirror m, ModelDataPoolLocation target, int from)
    {
        var (lo, hi, start) = (from, m.Length, target.IndicesStart * 4);
        if (!Assert(from <= hi) || !Assert(hi <= m.Start.Length)) return Lost;
        for (var step = 0; step < MaxSearch && lo < hi; step++)
        {
            var mid = (lo + hi) >>> 1;
            if (m.Start[mid] < start) lo = mid + 1;
            else hi = mid;
        }

        for (var p = lo; p < Math.Min(m.Length, MaxLocations) && m.Start[p] == start; p++)
            if (ReferenceEquals(m.Refs[p].Loc, target)) return p;
        return Unseen;
    }

    // New rows into spare arrays, then swapped; on false Rebuild overwrites the garbage
    private static bool Merge(Mirror m, ReadOnlySpan<ModelDataPoolLocation> list, int fresh, int slots)
    {
        var (n, old, at, gone, copied) = (list.Length, m.Length, m.Used, 0, 0);
        var (allocated, ordered) = (m.AllocatedTris, m.Ordered);
        if (!Assert(slots <= m.Inv.Length) || !Assert(n <= m.NRefs.Length)) return false;
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var p = m.Match[i];
            if (p < 0)
            {
                if (!NewRow(m, list[i], i, at++, ref allocated)) return Abandon(m, n);
            }
            else if (i >= copied)
            {
                for (var d = gone; d < Math.Min(p, MaxLocations); d++) allocated -= Drop(m, d);
                var run = Run(m, i, p, n);
                if (run == 0) return Abandon(m, n);
                Array.Copy(m.Refs, p, m.NRefs, i, run);
                Array.Copy(m.Start, p, m.NStart, i, run);
                Array.Copy(m.Count, p, m.NCount, i, run);
                Array.Copy(m.Lod, p, m.NLod, i, run);
                Array.Copy(m.Slot, p, m.NSlot, i, run);
                (gone, copied) = (p + run, i + run);
            }

            m.Inv[m.NSlot[i]] = i;
            ordered &= i == 0 || m.NStart[i - 1] <= m.NStart[i];
        }

        for (var d = gone; d < Math.Min(old, MaxLocations); d++) allocated -= Drop(m, d);
        if (!Assert(at - m.Used == fresh)) return Abandon(m, n);
        for (var s = at; s < Math.Min(slots, MaxSlots); s++) Blank(m, s);
        (m.Refs, m.NRefs, m.Start, m.NStart, m.Count, m.NCount) =
            (m.NRefs, m.Refs, m.NStart, m.Start, m.NCount, m.Count);
        (m.Lod, m.NLod, m.Slot, m.NSlot) = (m.NLod, m.Lod, m.NSlot, m.Slot);
        Array.Clear(m.NRefs, 0, old);
        (m.Length, m.Used, m.Slots, m.AllocatedTris, m.Ordered) = (n, at, slots, allocated, ordered);
        (m.Dead, m.Fresh) = (m.Dead + old - (n - fresh), m.Fresh + fresh);
        return true;
    }

    private static bool NewRow(Mirror m, ModelDataPoolLocation loc, int i, int slot, ref int allocated)
    {
        // Rebuild hands the pool to the engine
        if (!NotNull(loc.CullVisible) || !Index(i, m.NRefs.Length)) return false;
        m.NRefs[i] = new LocRef(loc, loc.CullVisible);
        (m.NStart[i], m.NCount[i]) = (loc.IndicesStart * 4, loc.IndicesEnd - loc.IndicesStart);
        (m.NLod[i], m.NSlot[i]) = (loc.LodLevel, slot);
        Place(m, loc.FrustumCullSphere, slot);
        allocated += m.NCount[i] / 3;
        return true;
    }

    private static bool Abandon(Mirror m, int n)
    {
        if (Assert(n <= m.NRefs.Length)) Array.Clear(m.NRefs, 0, n);
        _ = Assert(m.Length <= m.Refs.Length);
        return false;
    }

    private static int Run(Mirror m, int i, int p, int n)
    {
        if (!Index(i, n) || !Assert(m.Match[i] == p)) return 0;
        var run = 1;
        for (var k = 1; k < Math.Min(n - i, MaxLocations) && m.Match[i + k] == p + k; k++) run++;
        return Assert(p + run <= m.Length) ? run : 0;
    }

    private static int Drop(Mirror m, int row)
    {
        if (!Index(row, m.Length) || !Index(m.Slot[row], m.Inv.Length)) return 0;
        m.Inv[m.Slot[row]] = Gone;
        return m.Count[row] / 3;
    }

    // ModelDataPoolLocation[]/Bools[] stores pay the covariant check (unsealed: a method-table miss per row); struct
    // arrays only a write barrier.
    private readonly record struct LocRef(ModelDataPoolLocation Loc, Bools Vis);

    // Rows in list order, slots in geometry order (grid or list): Slot and Inv map between them
    private sealed class Mirror
    {
        // Whose list at which version, and the Render call and stage batch the result belongs to
        public object? Owner;
        public int ListVersion, Pass = -1, Groups, Batch = -1, Rendered;

        public int Length, AllocatedTris;
        public LocRef[] Refs = [];
        public int[] Start = [], Count = [], Lod = [];
        public int[] Slot = []; // row -> slot
        public bool Ordered; // the rows' starts never decrease, so Diff may bisect

        // Diff: each new row's old row, and the spare arrays it merges into
        public int[] Match = [];
        public LocRef[] NRefs = [];
        public int[] NStart = [], NCount = [], NLod = [], NSlot = [];
        public int Dead, Fresh, Quiet;

        // By slot: [0, Base) grid or list order, [Base, Used) added by Diff, Slots padded to whole vectors
        public float[] Cx = [], Cy = [], Cz = [], Hx = [], Hy = [], Hz = [];
        public int[] Inv = []; // slot -> row, Gone for padding and removed rows
        public int Base, Used, Slots;

        // Same geometry by row, while a relayout sorts
        public float[] Sx = [], Sy = [], Sz = [], Shx = [], Shy = [], Shz = [];

        // Cell size and origin, each row's cell (Key), cells' first slots (Bins) and bounds; Cells 0 = no grid
        public int Cells, Shift, OriginX, OriginZ, Wide;
        public bool Tried; // Settle found no grid worth having for this list
        public int[] Key = [], Bins = [];
        public double[] LoX = [], HiX = [], LoY = [], HiY = [], LoZ = [], HiZ = [];
        public int[] BStart = [], BCount = [];
        public double[] BCx = [], BCy = [], BCz = [], BHx = [], BHy = [], BHz = [];

        public long[] Outside = [], BOutside = [];
        public ulong[] Cand = [];
        public bool Dirty; // Cand may hold bits Candidates did not take (and clear)
        public int[] Rows = [];
        public bool[] Ok = [];
        public int Emitted; // Rows[..Emitted]: the rows the last Emit wrote, in order
        public long SkippedRows;
    }
}
