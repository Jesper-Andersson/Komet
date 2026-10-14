using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Past where a block covers under Pixels pixels (window height x resolution scale, FOV), leaves thin to a 3D
// checkerboard and plants (Cross, collision-free JSON plants) drop; leaf blocks are two crossed quads two blocks wide,
// so the outline stays at half the triangles and overdraw (far cost, night-lod VD 1536: over half of OpaqueNoCull). The
// band comes from the chunk centre distance at pass start; edge-only passes keep it. Scan re-marks chunks whose band no
// longer fits, with hysteresis; the near band reaches Margin past the threshold. Dimension 0.
internal static class FarFoliage
{
    public const int MaxPixels = 2, Engine = 0;
    private const int None = 0, Leaves = 1, Plant = 2, Near = 1, Far = 2, Dimension = 1024, MaxMarks = 256, MaxKinds = 1 << 20, MaxTracked = 1 << 22;
    private const double HalfDiagonal = 23, Margin = 32, Band = 32, MinDistance = 128, ScanSeconds = 0.5, SweepSeconds = 30;
    private const double PendingMs = 20_000;
    private const int ThinnedCounter = 0, DroppedCounter = 1, MarkedCounter = 2, Counters = 3;

    private static readonly Tally Counts = new(Counters);
    private static readonly ConcurrentDictionary<long, byte> Bands = new();
    private static readonly ConcurrentDictionary<long, long> Pending = new();

    [ThreadStatic] private static bool _active, _far, _seen;
    [ThreadStatic] private static long _key;

    private static byte[] _kinds = [];
    private static double _x, _z, _threshold = double.MaxValue;
    private static ICoreClientAPI? _api;
    private static long _tick;
    private static double _sinceSweep;

    // 0 off, else the size in pixels below which a block counts as far
    public static int Pixels { get; set; } = 1;
    public static bool Installed { get; private set; }

    public static long Thinned => Counts.Total(ThinnedCounter);
    public static long Dropped => Counts.Total(DroppedCounter);
    public static long Marked => Counts.Total(MarkedCounter);
    // Of the chunks with foliage, those drawn far at the last scan
    public static int FarChunks { get; private set; }

    // Where the far band begins this frame, in blocks; NaN while off
    public static double Threshold { get; private set; } = double.NaN;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "vars")]
    private static extern ref TCTCache? Vars(ChunkTesselator tesselator);

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger? logger = null)
    {
        Installed = false;
        var tesselator = typeof(ChunkTesselator);
        var pass = Method(tesselator, nameof(ChunkTesselator.NowProcessChunk), typeof(int), typeof(int), typeof(int),
            typeof(TesselatedChunk), typeof(bool));
        var block = Method(tesselator, "TesselateBlock", typeof(Block), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
        if (!NotNull(harmony) || !NotNull(api) || pass is null || block is null ||
            !Fields(nameof(FarFoliage), logger, "foliage is drawn in full at every distance",
                [(tesselator, "vars", typeof(TCTCache))]) ||
            !Accessible(nameof(FarFoliage), logger)) return;
        _ = NotNull(harmony.Patch(pass, new HarmonyMethod(Begin) { priority = Priority.First }, new HarmonyMethod(End)));
        _ = NotNull(harmony.Patch(block, new HarmonyMethod(Prefix)));
        _api = api;
        _ = Assert(_tick == 0);
        _tick = api.Event.RegisterGameTickListener(Scan, (int)(ScanSeconds * 1000));
        Installed = true;
    }

    public static void Stop()
    {
        if (_api is { } api && _tick != 0) api.Event.UnregisterGameTickListener(_tick);
        _ = Assert(_tick >= 0);
        (_api, _tick, _kinds, Installed, Threshold) = (null, 0, [], false, double.NaN);
        _threshold = double.MaxValue;
        Bands.Clear();
        Pending.Clear();
        _ = Assert(Bands.IsEmpty) && Assert(Pending.IsEmpty);
    }

    // Distance in blocks where a block covers `pixels` pixels: screen height / (tangent span * pixels)
    internal static double Distance(double height, double fovDegrees, int pixels)
    {
        if (!Assert(pixels >= 0) || !Assert(pixels <= MaxPixels) || pixels == 0 || !(height > 0) || !(fovDegrees is > 1 and < 179)) return double.NaN;
        var span = 2 * Math.Tan(fovDegrees * Math.PI / 360);
        return Assert(span > 0) ? Math.Max(MinDistance, height / span / pixels) : double.NaN;
    }

    // Band wanted at centre distance d given the current band (0: none), with near/far hysteresis
    internal static int Want(double d, int band, double threshold)
    {
        if (!double.IsFinite(threshold) || !Assert(band is >= 0 and <= Far)) return Near;
        var (near, far) = (threshold + HalfDiagonal + Margin, threshold + HalfDiagonal + Margin + Band);
        return band switch
        {
            Far => d < near ? Near : Far,
            Near => d > far ? Far : Near,
            _ => d > (near + far) / 2 ? Far : Near
        };
    }

    // -1 for a position NowProcessChunk turns away (negative coordinates)
    private static long Key(int x, int y, int z) =>
        x < 0 || y < 0 || z < 0 ? -1 : ((long)y << 42) | ((long)(z & 0x1FFFFF) << 21) | (long)(x & 0x1FFFFF);

    private static double CentreDistance(int cx, int cz, double x, double z)
    {
        double dx = cx * Size + Size / 2 - x, dz = cz * Size + Size / 2 - z;
        return Assert(double.IsFinite(dx)) && Assert(double.IsFinite(dz)) ? Math.Sqrt(dx * dx + dz * dz) : double.MaxValue;
    }

    // NowProcessChunk's prefix and postfix, on the tessellation thread
    private static void Begin(int chunkX, int chunkY, int chunkZ, bool skipChunkCenter)
    {
        (_key, _seen) = (Key(chunkX, chunkY, chunkZ), false);
        _ = Assert(_key >= -1);
        var threshold = Volatile.Read(ref _threshold);
        _active = Pixels > 0 && Installed && chunkY < Dimension && _key >= 0 && threshold < double.MaxValue;
        if (!_active) return;
        if (skipChunkCenter && Bands.TryGetValue(_key, out var band))
        {
            _far = band == Far;
            return;
        }

        var d = CentreDistance(chunkX, chunkZ, Volatile.Read(ref _x), Volatile.Read(ref _z));
        _far = Want(d, 0, threshold) == Far;
    }

    private static void End(bool skipChunkCenter)
    {
        if (_active && (_seen || (skipChunkCenter && Bands.ContainsKey(_key)))) Bands[_key] = _far ? (byte)Far : (byte)Near;
        else _ = Bands.TryRemove(_key, out _);
        _ = Pending.TryRemove(_key, out _);
        _active = false;
        _ = Assert(!_active) && Assert(_key >= -1);
    }

    // TesselateBlock prefix: far plants, and far leaves on odd 3D checkerboard cells, are not drawn
    private static bool Prefix(ChunkTesselator __instance, Block block, int posX, int posZ)
    {
        if (!_active || block is null) return true;
        var kind = KindOf(block);
        _ = Assert(kind is >= None and <= Plant);
        if (kind == None) return true;
        _seen = true;
        if (!_far) return true;
        if (kind == Leaves && (Vars(__instance) is not { } vars || ((posX + vars.posY + posZ) & 1) == 0)) return true;
        if (Counting.On) Counts.Add(kind == Leaves ? ThinnedCounter : DroppedCounter, 1);
        return false;
    }

    private static int KindOf(Block block)
    {
        var kinds = _kinds;
        var id = block.BlockId;
        if ((uint)id >= (uint)kinds.Length) kinds = Classify(id);
        return NotNull(kinds) && (uint)id < (uint)kinds.Length ? kinds[id] : None;
    }

    // Every block's kind, rebuilt when an id lies past the table (swapped whole; threads read either)
    private static byte[] Classify(int id)
    {
        if (_api?.World?.Blocks is not { } blocks || !Index(id, MaxKinds)) return [];
        var kinds = new byte[Math.Max(blocks.Count, id + 1)];
        for (var i = 0; i < Math.Min(blocks.Count, MaxKinds); i++)
            if (blocks[i] is { } b && b.BlockId == i) kinds[i] = (byte)Kind(b);
        _kinds = kinds;
        return Assert(kinds.Length > id) ? kinds : [];
    }

    internal static int Kind(Block b)
    {
        if (!NotNull(b) || b.RenderPass != EnumChunkRenderPass.OpaqueNoCull) return None;
        if (b.DrawType == EnumDrawType.JSON && b.BlockMaterial == EnumBlockMaterial.Leaves) return Leaves;
        if (b.DrawType == EnumDrawType.Cross) return Plant;
        var walkable = b.CollisionBoxes is { Length: > 0 };
        return Assert(b.BlockId >= 0) && b.DrawType == EnumDrawType.JSON && b.BlockMaterial == EnumBlockMaterial.Plant && !walkable
            ? Plant
            : None;
    }

    // The main thread's half: publish the player and the thresholds, then mark the chunks whose band no longer fits
    private static void Scan(float dt)
    {
        if (_api is not { World.Player.Entity: { } entity } api || api.World is not ClientMain game) return;
        var threshold = Distance(api.Render.FrameHeight * FrameSettings.Ssaa, FrameSettings.FieldOfView, Pixels);
        Threshold = threshold;
        Volatile.Write(ref _x, entity.Pos.X);
        Volatile.Write(ref _z, entity.Pos.Z);
        Volatile.Write(ref _threshold, double.IsFinite(threshold) ? threshold : double.MaxValue);
        _sinceSweep += dt;
        var sweep = _sinceSweep >= SweepSeconds;
        if (sweep) _sinceSweep = 0;
        var (marks, now) = (0, Environment.TickCount64);
        var busy = TessSeams.Backlogged;
        _ = Assert(now > 0) && Assert(_sinceSweep >= 0);
        var far = 0;
        using var entries = Bands.GetEnumerator(); // no copy of the table, unlike Keys, Values or ToArray
        for (var n = 0; n < MaxTracked && entries.MoveNext(); n++)
        {
            var (key, band) = entries.Current;
            far += band == Far ? 1 : 0;
            var (x, y, z) = ((int)(key & 0x1FFFFF), (int)(key >> 42), (int)((key >> 21) & 0x1FFFFF));
            if (sweep && api.World.BlockAccessor.GetChunk(x, y, z) is null)
            {
                _ = Bands.TryRemove(key, out _);
                _ = Pending.TryRemove(key, out _);
                continue;
            }

            if (Pending.TryGetValue(key, out var since))
            {
                if (now - since < PendingMs) continue;
                _ = Pending.TryRemove(key, out _);
            }

            var want = Pixels > 0 ? Want(CentreDistance(x, z, entity.Pos.X, entity.Pos.Z), band, threshold) : Near;
            if (want == band || marks >= MaxMarks || (want == Far && busy) || !Pending.TryAdd(key, now)) continue; // leaner waits
            game.WorldMap.MarkChunkDirty(x, y, z, fireEvent: false);
            marks++;
        }

        FarChunks = far;
        if (marks > 0 && Counting.On) Counts.Add(MarkedCounter, marks);
        _ = Assert(marks <= MaxMarks);
    }
}
