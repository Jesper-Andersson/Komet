using System.Numerics;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// Arrays only grow, with room for Diff's rows behind the grid, so no diff allocates.
internal static partial class FrustumSweep
{
    // Relays a quarter-stale mirror from its own arrays, a few a frame (a strip unload touches every pool)
    private static void Tidy(Mirror m, bool quiet)
    {
        var w = Vector<double>.Count;
        if (!Assert(m.Length <= m.Refs.Length) || !Assert(m.Slots <= m.Cx.Length)) return;
        if (quiet && m.Cells == 0 && !m.Tried && m.Length >= MinBucketed && ++m.Quiet >= QuietCulls)
        {
            Settle(m, w);
            return;
        }

        if (4 * (m.Dead + m.Fresh) <= m.Length + Churn || !Relayout()) return;
        if (Counting.Hud) _ = Interlocked.Increment(ref _relayouts);
        if (m.Cells > 0 && m.Length >= MinBucketed) Settle(m, w);
        else if (Gather(m, m.Length)) Lay(m, m.Length, w);
    }

    // One of RelayoutsPerFrame, taken by CAS (workers tidy in parallel)
    private static bool Relayout()
    {
        var seen = Volatile.Read(ref _frameRelayouts);
        for (var i = 0; i < MaxRelayoutTries && seen < RelayoutsPerFrame; i++)
        {
            var was = Interlocked.CompareExchange(ref _frameRelayouts, seen + 1, seen);
            if (was == seen) return true;
            seen = was;
        }

        return false;
    }

    private static void Settle(Mirror m, int w)
    {
        var n = m.Length;
        if (!Assert(n >= MinBucketed) || !Gather(m, n)) return;
        var (minX, maxX, minZ, maxZ) = (int.MaxValue, int.MinValue, int.MaxValue, int.MinValue);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            (minX, maxX) = (Math.Min(minX, (int)m.Sx[i]), Math.Max(maxX, (int)m.Sx[i]));
            (minZ, maxZ) = (Math.Min(minZ, (int)m.Sz[i]), Math.Max(maxZ, (int)m.Sz[i]));
        }

        if (Assert(minX <= maxX) && Group(m, n, w, minX, maxX, minZ, maxZ))
        {
            (m.Base, m.Used, m.Dead, m.Fresh) = (m.Slots, m.Slots, 0, 0);
            if (Counting.Hud) _ = Interlocked.Increment(ref _settles);
            return;
        }

        m.Tried = true; // the sort may have half-moved the geometry: back to list order, no second try
        Lay(m, n, w);
    }

    private static bool Gather(Mirror m, int n)
    {
        if (!Assert(n > 0) || !Unsorted(m, n)) return false;
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var s = m.Slot[i];
            if (!Index(s, m.Slots)) return false;
            (m.Sx[i], m.Sy[i], m.Sz[i]) = (m.Cx[s], m.Cy[s], m.Cz[s]);
            (m.Shx[i], m.Shy[i], m.Shz[i]) = (m.Hx[s], m.Hy[s], m.Hz[s]);
        }

        return true;
    }

    private static void Lay(Mirror m, int n, int w)
    {
        m.Cells = 0;
        if (!Assert(n <= m.Sx.Length) || !Assert(n <= m.Cx.Length)) return;
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            (m.Cx[i], m.Cy[i], m.Cz[i]) = (m.Sx[i], m.Sy[i], m.Sz[i]);
            (m.Hx[i], m.Hy[i], m.Hz[i]) = (m.Shx[i], m.Shy[i], m.Shz[i]);
            (m.Slot[i], m.Inv[i]) = (i, i);
        }

        Tail(m, n, w);
        (m.Dead, m.Fresh) = (0, 0);
    }

    private static bool Unsorted(Mirror m, int n)
    {
        if (!Assert(n > 0)) return false;
        if (m.Sx.Length >= n) return true;
        var room = Capacity(n, m.Sx.Length, MaxLocations);
        if (!Assert(room >= n)) return false;
        (m.Sx, m.Sy, m.Sz) = (new float[room], new float[room], new float[room]);
        (m.Shx, m.Shy, m.Shz) = (new float[room], new float[room], new float[room]);
        return true;
    }

    private static bool Grow(Mirror m, int n, int w)
    {
        var slots = Geometry(n, w);
        if (!Assert(slots >= n) || !Assert(slots <= MaxSlots)) return false;
        if (m.Refs.Length < n)
        {
            var cap = Capacity(n, m.Refs.Length, MaxLocations);
            if (!Assert(cap >= n)) return false;
            (m.Refs, m.NRefs, m.Cand) = (new LocRef[cap], new LocRef[cap], new ulong[(cap + WordBits - 1) / WordBits]);
            (m.Start, m.Count, m.Lod, m.Slot) = (new int[cap], new int[cap], new int[cap], new int[cap]);
            (m.NStart, m.NCount, m.NLod, m.NSlot, m.Match) =
                (new int[cap], new int[cap], new int[cap], new int[cap], new int[cap]);
        }

        if (m.Cx.Length >= slots) return true;
        var room = Capacity(slots, m.Cx.Length, MaxSlots);
        if (!Assert(room >= slots)) return false;
        (m.Cx, m.Cy, m.Cz) = (new float[room], new float[room], new float[room]);
        (m.Hx, m.Hy, m.Hz) = (new float[room], new float[room], new float[room]);
        (m.Outside, m.Inv) = (new long[room], new int[room]);
        return true;
    }

    // Rows, full grid padding (PerCell per cell, rounded to a vector) and Diff's rows behind
    private static int Geometry(int n, int w)
    {
        _ = Assert(n is >= 0 and <= MaxLocations) && Assert(w is > 0 and <= MaxLanes);
        return n + (n / PerCell + 1) * w + n / 4 + 2 * w;
    }

    private static void Tail(Mirror m, int n, int w)
    {
        if (!Assert(w > 0)) return;
        m.Slots = (n + w - 1) / w * w;
        if (!Assert(m.Cx.Length >= m.Slots)) return;
        for (var i = n; i < Math.Min(m.Slots, MaxSlots); i++) Blank(m, i);
        (m.Base, m.Used) = (m.Slots, m.Slots);
    }

    // Zero-sized box at the origin, or at the cell centre for padding
    private static void Blank(Mirror m, int slot, float x = 0, float y = 0, float z = 0)
    {
        if (!Index(slot, m.Cx.Length) || !Index(slot, m.Inv.Length)) return;
        (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = (x, y, z);
        (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (0, 0, 0);
        m.Inv[slot] = Gone;
    }

    private static bool Group(Mirror m, int n, int w, int minX, int maxX, int minZ, int maxZ)
    {
        m.Cells = 0;
        if (!Assert(n >= MinBucketed) || minX > maxX || minZ > maxZ) return false;
        var want = Math.Min(MaxCells, Math.Max(1, n / PerCell));
        var shift = MaxShift; // each step halves the grid, so the span ends well before MaxShift
        for (var s = CellShift; s < MaxShift; s++)
            if (Span(minX, maxX, s) * Span(minZ, maxZ, s) <= want)
            {
                shift = s;
                break;
            }

        if (!Assert(shift < MaxShift)) return false;
        var (wide, deep) = (Span(minX, maxX, shift), Span(minZ, maxZ, shift));
        var cells = wide * deep;
        if (cells <= 1 || cells > MaxCells) return false;
        (m.Shift, m.OriginX, m.OriginZ, m.Wide) = (shift, minX >> shift, minZ >> shift, (int)wide);
        return Bin(m, n, w, (int)cells);
    }

    private static long Span(int min, int max, int shift)
    {
        if (!Assert(min <= max) || !Assert(shift is >= 0 and < MaxShift)) return 1;
        return (long)(max >> shift) - (min >> shift) + 1;
    }

    // Counts become first slots (whole vectors); empty cells marked, as Box can't tell them from start 0 afterwards
    private static bool Bin(Mirror m, int n, int w, int cells)
    {
        if (!Room(m, n, cells)) return false;
        Array.Clear(m.Bins, 0, cells);
        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var cell = (((int)m.Sz[i] >> m.Shift) - m.OriginZ) * m.Wide + ((int)m.Sx[i] >> m.Shift) - m.OriginX;
            if (!Index(cell, cells)) return false;
            (m.Key[i], m.Bins[cell]) = (cell, m.Bins[cell] + 1);
        }

        var (at, used) = (0, 0);
        for (var c = 0; c < Math.Min(cells, MaxCells); c++)
        {
            var count = m.Bins[c];
            m.Bins[c] = count == 0 ? Empty : at;
            if (count == 0) continue;
            at += (count + w - 1) / w * w;
            used++;
        }

        m.Slots = at;
        return Assert(at <= m.Cx.Length) && Grid(m, used, w) && Scatter(m, n, cells) && Box(m, cells, w);
    }

    private static bool Scatter(Mirror m, int n, int cells)
    {
        if (!Assert(cells <= m.LoX.Length)) return false;
        for (var c = 0; c < Math.Min(cells, MaxCells); c++)
        {
            (m.LoX[c], m.HiX[c]) = (double.MaxValue, double.MinValue);
            (m.LoY[c], m.HiY[c]) = (double.MaxValue, double.MinValue);
            (m.LoZ[c], m.HiZ[c]) = (double.MaxValue, double.MinValue);
        }

        for (var i = 0; i < Math.Min(n, MaxLocations); i++)
        {
            var cell = m.Key[i];
            var slot = m.Bins[cell]++;
            if (!Index(slot, m.Slots)) return false;
            (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = (m.Sx[i], m.Sy[i], m.Sz[i]);
            (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (m.Shx[i], m.Shy[i], m.Shz[i]);
            double cx = m.Sx[i], cy = m.Sy[i], cz = m.Sz[i];
            double hx = m.Shx[i], hy = m.Shy[i], hz = m.Shz[i];
            (m.Slot[i], m.Inv[slot]) = (slot, i);
            (m.LoX[cell], m.HiX[cell]) = (Math.Min(m.LoX[cell], cx - hx), Math.Max(m.HiX[cell], cx + hx));
            (m.LoY[cell], m.HiY[cell]) = (Math.Min(m.LoY[cell], cy - hy), Math.Max(m.HiY[cell], cy + hy));
            (m.LoZ[cell], m.HiZ[cell]) = (Math.Min(m.LoZ[cell], cz - hz), Math.Max(m.HiZ[cell], cz + hz));
        }

        return true;
    }

    private static bool Room(Mirror m, int n, int cells)
    {
        if (m.Key.Length < n) m.Key = new int[Capacity(n, m.Key.Length, MaxLocations)];
        if (m.Bins.Length < cells)
        {
            var room = Capacity(cells, m.Bins.Length, MaxCells);
            (m.Bins, m.LoX, m.HiX, m.LoY) = (new int[room], new double[room], new double[room], new double[room]);
            (m.HiY, m.LoZ, m.HiZ) = (new double[room], new double[room], new double[room]);
        }

        return Assert(m.Bins.Length >= cells) && Assert(m.Key.Length >= n);
    }

    private static bool Grid(Mirror m, int cells, int w)
    {
        var padded = (cells + w - 1) / w * w;
        if (!Assert(cells > 0) || !Assert(padded <= MaxCells + MaxLanes)) return false;
        if (m.BStart.Length < padded)
        {
            var room = Capacity(padded, m.BStart.Length, MaxCells + MaxLanes);
            if (!Assert(room >= padded)) return false;
            (m.BStart, m.BCount) = (new int[room], new int[room]);
            (m.BCx, m.BCy, m.BCz) = (new double[room], new double[room], new double[room]);
            (m.BHx, m.BHy, m.BHz, m.BOutside) = (new double[room], new double[room], new double[room], new long[room]);
        }

        m.Cells = cells;
        return true;
    }

    private static bool Box(Mirror m, int cells, int w)
    {
        var (b, at) = (0, 0);
        for (var c = 0; c < Math.Min(cells, MaxCells) && b < m.Cells; c++)
        {
            if (m.Bins[c] == Empty) continue;
            var count = m.Bins[c] - at; // the cursor stopped past the cell's last member
            if (!Assert(count > 0) || !Index(at, m.Slots) || !Assert(at + count <= m.Slots)) return false;
            (m.BStart[b], m.BCount[b]) = (at, count);
            (m.BCx[b], m.BHx[b]) = ((m.LoX[c] + m.HiX[c]) / 2, (m.HiX[c] - m.LoX[c]) / 2 + Slack);
            (m.BCy[b], m.BHy[b]) = ((m.LoY[c] + m.HiY[c]) / 2, (m.HiY[c] - m.LoY[c]) / 2 + Slack);
            (m.BCz[b], m.BHz[b]) = ((m.LoZ[c] + m.HiZ[c]) / 2, (m.HiZ[c] - m.LoZ[c]) / 2 + Slack);
            var padded = (count + w - 1) / w * w;
            // at the cell's centre a padding lane costs at most its vector
            for (var i = at + count; i < Math.Min(at + padded, MaxSlots); i++)
                Blank(m, i, (float)m.BCx[b], (float)m.BCy[b], (float)m.BCz[b]);
            (at, b) = (at + padded, b + 1);
        }

        return Assert(b == m.Cells) && Assert(at == m.Slots);
    }

    private static int Capacity(int need, int have, int max)
    {
        _ = Assert(need <= max);
        return Math.Min(max, Math.Max(need, 2 * have));
    }

    private static void Place(Mirror m, Sphere s, int slot)
    {
        if (!Index(slot, m.Cx.Length)) return;
        (m.Cx[slot], m.Cy[slot], m.Cz[slot]) = (s.x, s.y, s.z);
        // AABBisOutside's float division; the kernel widens it as the engine does
        (m.Hx[slot], m.Hy[slot], m.Hz[slot]) = (s.radius / Sqrt3, s.radiusY / Sqrt3, s.radiusZ / Sqrt3);
    }
}
