using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// CalcBlockFaceLight's AO for plain blocks. In vanilla only BlockMicroBlock overrides the AO virtuals and
// BlockForFluidsLayer makes ForFluidsLayer constant true, so a kind table by BlockId (holding the block against swaps)
// marks blocks using Block's own field tests; other blocks, or any face while a mod patches those methods, go to the
// engine. Decoding and float ops follow the engine, including the reversed corner front flags (corner 0: sample 7's
// flag, sample 4's ambient) and the multiplier quotient (occ or front ambient / 2, 3, 4, or full occlusion), each
// channel (int)((float)sum * multiplier); only plain faces write the engine's scratch. The fused SetUpLightRGBs reads
// the 26 cells once. Four Vector128 lanes repeat the engine's int and float steps (never fused): exact only for finite
// multipliers in [0, 16] (vanilla occ 0.67f), else, or without vector hardware, the engine.
internal static class FaceLight
{
    private const int Samples = 8, Edges = 4, Reach = TessSeams.Plane + Ext + 1, Around = 27, JsonLights = 25;
    private const int MaxBlocks = 1 << 20, MaxTypes = 1 << 16, Slots = Faces * Samples, AllFaces = (1 << Faces) - 1;
    private const int Channels = 0x00FF00FF, Factors5 = 5;

    private const int AbsorbsBit = 9, FluidsBit = 11, Leaves = 1 << 8, Absorbs = 1 << AbsorbsBit, Custom = 1 << 10;
    private const int FluidsAbsorb = 1 << FluidsBit, MaxOverrides = EngineShape.MaxMethods;
    private const byte ReturnTrue = 0x17, ReturnFalse = 0x16, Return = 0x2A; // ldc.i4.1 / ldc.i4.0, ret

    // The engine's 1f / (float)4 for a front that does not occlude
    private const float Quarter = 1f / 4, MaxFactor = 16;

    // EngineShape of Shaped() and FusedShaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x937CF4F5C7CB716DUL, FusedShape = 0xEE59F74AD862E162UL;

    private const int FastCounter = 0, EngineCounter = 1, FusedCounter = 2;

    private static readonly Tally Counts = new(FusedCounter + 1);

    // Both built whole before publishing, never written after: any tessellation thread may read another's
    private static Tables? _tables;
    private static Quotients _quotients = new(0.67f);
    private static MethodBase?[] _face = [], _fused = [], _corner = [], _ao = [];
    private static bool _foreign, _fusedForeign;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static bool FusedInstalled { get; private set; }

    // Another patch on a method: the engine lights all faces or JSON blocks
    public static bool StoodDown => _foreign || _fusedForeign;

    public static long FastFaces => Counts.Total(FastCounter);
    public static long EngineFaces => Counts.Total(EngineCounter);
    public static long FusedBlocks => Counts.Total(FusedCounter);

    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape,
        ulong fusedShape = FusedShape)
    {
        (Installed, FusedInstalled, _foreign, _fusedForeign, _tables) = (false, false, false, false, null);
        var (face, fused) = (Target(), FusedTarget());
        if (!NotNull(harmony) || !NotNull(face) || !Accessible(nameof(FaceLight), logger) ||
            !EngineShape.Matches(Shaped(), shape, nameof(FaceLight), logger)) return;
        if (!Vector128.IsHardwareAccelerated)
        {
            logger?.Notification("Komet FaceLight: no vector hardware, the engine lights every face");
            return;
        }

        var shaped = fused is not null &&
                     EngineShape.Matches(FusedShaped(), fusedShape, nameof(FaceLight) + " fused", logger);
        (_face, _fused, _corner, _ao) = ([face], shaped ? [fused] : [], [CornerAo()], AoMethods());
        _ = NotNull(harmony.Patch(face, new HarmonyMethod(Prefix)));
        Installed = true;
        if (shaped) FusedInstalled = NotNull(harmony.Patch(fused, new HarmonyMethod(FusedPrefix)));
        Recheck();
    }

    // Replacing patches are skipped with the original; postfixes see the same result but miss bypassed calls
    internal static void Recheck()
    {
        if (!Installed) return;
        const PatchKinds replacing = PatchKinds.Replacing, all = PatchKinds.All;
        var (own, overrides) = (typeof(FaceLight), Volatile.Read(ref _tables)?.Overrides ?? []);
        var face = EngineShape.Foreign(_face, replacing, null, own) || EngineShape.Foreign(_corner, all, null, own) ||
                   EngineShape.Foreign(_ao, all, null) || EngineShape.Foreign(overrides, all, null);
        var fused = EngineShape.Foreign(_fused, replacing, null, own) || EngineShape.Foreign(_face, all, null, own);
        _foreign = EngineShape.Report(Logger, nameof(FaceLight), _foreign, face);
        _fusedForeign = EngineShape.Report(Logger, nameof(FaceLight) + " fused", _fusedForeign, fused);
    }

    internal static MethodInfo? Target()
    {
        var method = Method(typeof(TCTCache), "CalcBlockFaceLight", typeof(int), typeof(int));
        return NotNull(method) && Assert(method.ReturnType == typeof(long)) ? method : null;
    }

    internal static MethodInfo? FusedTarget()
    {
        var method = Method(typeof(JsonTesselator), nameof(JsonTesselator.SetUpLightRGBs), typeof(TCTCache));
        return NotNull(method) && Assert(method.ReturnType == typeof(long)) ? method : null;
    }

    private static MethodInfo? CornerAo()
    {
        var method = AccessTools.DeclaredMethod(typeof(TCTCache), "CornerAoRGB");
        return NotNull(method) && Assert(method.ReturnType == typeof(int)) ? method : null;
    }

    private static MethodBase?[] AoMethods()
    {
        MethodBase?[] methods =
        [
            Method(typeof(Block), nameof(Block.DoEmitSideAo), typeof(IGeometryTester), typeof(BlockFacing)),
            Method(typeof(Block), nameof(Block.DoEmitSideAoByFlag), typeof(IGeometryTester),
                typeof(Vec3iAndFacingFlags), typeof(int)),
            AccessTools.DeclaredPropertyGetter(typeof(Block), nameof(Block.ForFluidsLayer))
        ];
        _ = Assert(methods.Length == 3) && Assert(methods.Length < MaxOverrides);
        return methods;
    }

    // Fast face reproduces: CalcBlockFaceLight, CornerAoRGB, Block's AO methods, SmallBoolArray SideAo reads
    internal static MethodBase?[] Shaped()
    {
        var bits = typeof(SmallBoolArray);
        MethodBase?[] methods =
        [
            Target(), CornerAo(), .. AoMethods(), AccessTools.DeclaredPropertyGetter(bits, "Item"),
            Method(bits, "op_Implicit", bits)
        ];
        // A missing one fingerprints as 0
        _ = Assert(methods.Length <= EngineShape.MaxMethods) && Assert(bits.IsValueType);
        return methods;
    }

    internal static MethodBase?[] FusedShaped()
    {
        MethodBase?[] methods = [FusedTarget()];
        _ = Assert(methods.Length == 1) && Assert(methods.Length <= EngineShape.MaxMethods);
        return methods;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Prefix(TCTCache __instance, int tileSide, int extNeibIndex3d, ref long __result)
    {
        if (!Enabled || _foreign || !NotNull(__instance) || !NotNull(__instance.CurrentLightRGBByCorner)) return true;
        if (!Face(__instance, tileSide, extNeibIndex3d, __instance.CurrentLightRGBByCorner, Neighbours(__instance),
                out var sum))
        {
            if (Counting.Hud) Counts.Add(EngineCounter, 1);
            return true;
        }

        if (Counting.Hud) Counts.Add(FastCounter, 1);
        __result = sum;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool FusedPrefix(JsonTesselator __instance, TCTCache vars, ref long __result)
    {
        if (!Enabled || _foreign || _fusedForeign || !NotNull(__instance) || !NotNull(vars)) return true;
        if (!Six(vars, JsonLight(__instance), out var sum)) return true;
        __result = sum;
        return false;
    }

    // Face for OwnTessellation cubes and topsoil (no engine entry), gated and counted as the prefix; false: engine
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Lit(TCTCache vars, int tileSide, int front)
    {
        if (!Enabled || !Installed || _foreign || !NotNull(vars) || !NotNull(vars.CurrentLightRGBByCorner) ||
            !Face(vars, tileSide, front, vars.CurrentLightRGBByCorner, Neighbours(vars), out _)) return false;
        if (Counting.Hud) Counts.Add(FastCounter, 1);
        return true;
    }

    // Six for OwnTessellation JSON blocks (no SetUpLightRGBs), gated as the fused prefix; false: engine's loop
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Fused(TCTCache vars, int[] json, out long sum)
    {
        sum = 0;
        if (!Enabled || !FusedInstalled || _foreign || _fusedForeign || !NotNull(vars)) return false;
        return Assert(json.Length >= JsonLights) && Six(vars, json, out sum);
    }

    // One face's AO into corners[0..3], neighbours[1..8]; false, unwritten: no AO, non-plain, bad multiplier or index
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Face(TCTCache vars, int tileSide, int front, int[] corners, int[] neighbours, out long sum)
    {
        sum = 0;
        var self = vars.block;
        if (!vars.aoAndSmoothShadows || self is null || (uint)tileSide >= Faces ||
            (self.SideAo & (1 << tileSide)) == 0) return false;
        var tables = Current(vars.tct);
        // The engine's callers pass a cell of the chunk and its neighbour: never near the halo's edge
        var e = vars.extIndex3d;
        if (tables is null || !Arrays(vars.tct, out var solid, out var fluid, out var rgb) || !Index(front, ExtCells) ||
            !Index(e - Reach, ExtCells - 2 * Reach) || !Assert(corners.Length >= Edges) ||
            !Assert(neighbours.Length > Samples)) return false;
        ref var side = ref tables.Geometry.Sides[tileSide];
        var kinds = tables.Kinds;
        var facing = Front(kinds, solid[front], fluid[front]);
        if ((facing & Custom) != 0) return false;
        Cells cells = default;
        Block? known = null; // the last block found plain: neighbours repeat
        // e is Reach away from both ends and |offset| <= Reach
        ref var solids = ref MemoryMarshal.GetArrayDataReference(solid);
        ref var fluids = ref MemoryMarshal.GetArrayDataReference(fluid);
        ref var lights = ref MemoryMarshal.GetArrayDataReference(rgb);
        for (var k = 0; k < Samples; k++)
        {
            var p = e + side.Offset[k];
            var block = Unsafe.Add(ref solids, p);
            if (!ReferenceEquals(block, known))
            {
                if (!Plain(kinds, block)) return false;
                known = block;
            }

            (cells.Desc[k], cells.Rgb[k]) = (Sample(block, Unsafe.Add(ref fluids, p)), Unsafe.Add(ref lights, p));
        }

        if (!Multipliers(vars, self, out var factors)) return false;
        var leaves = self.BlockMaterial == EnumBlockMaterial.Leaves ? Leaves : 0;
        sum = Shade(ref side, tileSide, leaves, facing, rgb[front], ref cells, ref factors, corners, neighbours);
        return true;
    }

    // SetUpLightRGBs into json[0..24], each face lit here or by the engine; false, unwritten: engine (also without AO)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Six(TCTCache vars, int[] json, out long sum)
    {
        sum = 0;
        var self = vars.block;
        if (!vars.aoAndSmoothShadows || self is null || (self.SideAo & AllFaces) == 0 ||
            !Multipliers(vars, self, out var factors)) return false;
        var (tables, corners, neighbours, e) = (Current(vars.tct), vars.CurrentLightRGBByCorner, Neighbours(vars),
            vars.extIndex3d);
        if (tables is null || !Arrays(vars.tct, out var solid, out var fluid, out var rgb) ||
            !Index(e - Reach, ExtCells - 2 * Reach) || !Assert(json.Length >= JsonLights) ||
            !Assert(corners.Length >= Edges) || !Assert(neighbours.Length > Samples) ||
            !Standard(TileSideEnum.MoveIndex)) return false;
        Block27 desc = default, light = default;
        Gather(tables, solid, fluid, rgb, e, ref desc, ref light);
        var (sideAo, leaves, lit) = ((int)self.SideAo, self.BlockMaterial == EnumBlockMaterial.Leaves ? Leaves : 0, 0);
        for (var t = 0; t < Faces; t++)
        {
            ref var side = ref tables.Geometry.Sides[t];
            var p = e + Moves[t];
            var facing = Front(tables.Kinds, solid[p], fluid[p]);
            Cells cells = default;
            var custom = facing;
            for (var k = 0; k < Samples; k++)
            {
                var cell = side.Local[k];
                (cells.Desc[k], cells.Rgb[k]) = (desc[cell], light[cell]);
                custom |= desc[cell];
            }

            var fast = (sideAo & (1 << t)) != 0 && (custom & Custom) == 0;
            sum += fast
                ? Shade(ref side, t, leaves, facing, rgb[p], ref cells, ref factors, corners, neighbours)
                : Calc(vars, t, p);
            lit += fast ? 1 : 0;
            corners.AsSpan(0, Edges).CopyTo(json.AsSpan(t * Edges));
        }

        json[JsonLights - 1] = rgb[e];
        sum += json[JsonLights - 1];
        // The engine's faces count themselves in the prefix they pass through
        if (Counting.Hud && Assert(lit is >= 0 and <= Faces))
        {
            Counts.Add(FusedCounter, 1);
            Counts.Add(FastCounter, lit);
        }

        return true;
    }

    // The 20 cells the faces sample, each read once; a block that is not plain marks its cell
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Gather(Tables tables, Block[] solid, Block[] fluid, int[] rgb, int e, ref Block27 desc,
        ref Block27 light)
    {
        var (cells, kinds) = (tables.Geometry.Cells, tables.Kinds);
        if (!Assert(cells.Length <= Around)) return;
        Block? known = null;
        for (var i = 0; i < Math.Min(cells.Length, Around); i++)
        {
            var cell = cells[i];
            var p = e + Geometry.OffsetOf(cell);
            var block = solid[p];
            var custom = 0;
            if (!ReferenceEquals(block, known)) (known, custom) = Plain(kinds, block) ? (block, 0) : (known, Custom);
            (desc[cell], light[cell]) = (Sample(block, fluid[p]) | custom, rgb[p]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sample(Block solid, Block fluid) => solid.EmitSideAo |
        (solid.BlockMaterial == EnumBlockMaterial.Leaves ? Leaves : 0) | (fluid.LightAbsorption > 0 ? Absorbs : 0);

    // Front cell: its fluid when that absorbs light, else its solid block, as the engine picks it
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Front(Entry[] kinds, Block solid, Block fluid)
    {
        var absorbs = fluid.LightAbsorption > 0;
        var block = absorbs ? fluid : solid;
        var id = block.BlockId;
        if ((uint)id >= (uint)kinds.Length || !ReferenceEquals(kinds[id].Owner, block)) return Custom;
        var fluids = kinds[id].FluidsLayer && block.LightAbsorption > 0;
        return block.EmitSideAo | (absorbs ? Absorbs : 0) | (fluids ? FluidsAbsorb : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Plain(Entry[] kinds, Block block)
    {
        var id = block.BlockId;
        return (uint)id < (uint)kinds.Length && ReferenceEquals(kinds[id].Owner, block);
    }

    // CornerAoRGB multipliers: [0] full occlusion min(occ, 1 - halfoccInverted * clamp(LightAbsorption, 0, 32)), [1..3]
    // occ / 2, 3, 4 for 2-4 lights, [4] 1f / 4 for front ambient 1. False unless all in [0, MaxFactor].
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Multipliers(TCTCache vars, Block self, out Floats5 factors)
    {
        var quotients = Divisions(vars.occ);
        factors = default;
        var full = Math.Min(vars.occ, 1f - vars.halfoccInverted * GameMath.Clamp(self.LightAbsorption, 0, 32));
        (factors[0], factors[1], factors[2], factors[3], factors[4]) =
            (full, quotients.Half, quotients.Third, quotients.Fourth, Quarter);
        return quotients.Bounded && full is >= 0 and <= MaxFactor && Assert(Vector128.IsHardwareAccelerated);
    }

    // Engine's 8-sample loop (TCTCache.cs:198-274) and decode (275-300) on four lanes: edges (samples 0-3) and corner
    // samples (4-7) one Vector128 each, lanes = face corners; set bit = side doesn't occlude. Corner 0: 0's
    // lower-or-right side (upper-or-left for up/down), 2's upper-or-left, sample 4 with its ambient and 7's front flag,
    // 1: other side of 0, 3's upper-or-left, 5, 6's flag; 2: 1's lower-or-right (upper-or-left), 2's lower-or-right, 6,
    // 5's flag; 3: other side of 1, 3's lower-or-right, 7, 4's flag.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Shade(ref Side side, int t, int leaves, int front, int frontLight, ref Cells cells,
        ref Floats5 factors, int[] corners, int[] neighbours)
    {
        if (!Assert(corners.Length >= Edges) || !Assert(neighbours.Length > Samples)) return 0;
        var (zero, absorbs, leaf) = (Vector128<int>.Zero, Vector128.Create(Absorbs), Vector128.Create(leaves));
        var (frontV, own) = (Vector128.Create(front), Vector128.Create(frontLight));
        var edges = Vector128.LoadUnsafe(ref cells.Desc[0]);
        var shown = Vector128.Equals(edges & absorbs, zero); // no light-absorbing fluid over the sample
        var first = shown &
                    ~Vector128.Equals(edges & (Vector128.LoadUnsafe(ref side.First[0]) | leaf), zero); // occludes
        var second = shown & ~Vector128.Equals(edges & (Vector128.LoadUnsafe(ref side.Second[0]) | leaf), zero);
        var edgeLights = Vector128.LoadUnsafe(ref cells.Rgb[0]);
        var cornerLights = Vector128.LoadUnsafe(ref cells.Rgb[Edges]);
        Vector128.ConditionalSelect(first & second, own, edgeLights).StoreUnsafe(ref neighbours[1]);
        var cornerDesc = Vector128.LoadUnsafe(ref cells.Desc[Edges]);
        var cornerShown = Vector128.Equals(cornerDesc & absorbs, zero);
        var occludes = cornerShown &
                       ~Vector128.Equals(cornerDesc & (Vector128.LoadUnsafe(ref side.First[Edges]) | leaf), zero);
        var emits = ~Vector128.Equals(frontV & Vector128.LoadUnsafe(ref side.Second[Edges]), zero);
        Vector128.ConditionalSelect(occludes, own, cornerLights).StoreUnsafe(ref neighbours[1 + Edges]);
        var upperLeft = (int)~first.ExtractMostSignificantBits() & 15; // set: the side does not occlude
        var lowerRight = (int)~second.ExtractMostSignificantBits() & 15;
        var open = (int)~occludes.ExtractMostSignificantBits() & 15;
        var dark = (int)~emits.ExtractMostSignificantBits() & 15;
        var hidden = (int)~cornerShown.ExtractMostSignificantBits() & 15;
        var frontFlags = dark | ((front & FluidsAbsorb) != 0 ? 15 : 0);
        var ambientOne = ((~hidden & dark) | ((front & (Absorbs | side.Toward)) == 0 ? 15 : 0)) & 15;
        // per corner one bit each: side 1, side 2, corner sample, front flag (reversed: corner 0 has 7's), ambient 1f
        var side1 = t >= Up
            ? (upperLeft & 1) | ((lowerRight & 1) << 1) | ((upperLeft & 2) << 1) | ((lowerRight & 2) << 2)
            : (lowerRight & 1) | ((upperLeft & 1) << 1) | ((lowerRight & 2) << 1) | ((upperLeft & 2) << 2);
        var side2 = ((upperLeft >> 2) & 3) | (((lowerRight >> 2) & 3) << 2);
        var frontCorner = ((frontFlags & 8) >> 3) | ((frontFlags & 4) >> 1) | ((frontFlags & 2) << 1) |
                          ((frontFlags & 1) << 3);
        var keep = (side1 | side2) & (frontCorner | open);
        var (m1, m2, mb) = (Lanes(side1 & keep), Lanes(side2 & keep), Lanes(open & keep));
        var light1 = Vector128.Shuffle(edgeLights, Vector128.Create(0, 0, 1, 1));
        var light2 = Vector128.Shuffle(edgeLights, Vector128.Create(2, 3, 2, 3));
        var (channels, mask) = (Vector128.Create(Channels), Vector128.Create(0xFFFF));
        var low = (own & channels) + (light1 & channels & m1) + (light2 & channels & m2) +
                  (cornerLights & channels & mb);
        var high = (Vector128.ShiftRightLogical(own, 8) & channels) +
                   (Vector128.ShiftRightLogical(light1, 8) & channels & m1) +
                   (Vector128.ShiftRightLogical(light2, 8) & channels & m2) +
                   (Vector128.ShiftRightLogical(cornerLights, 8) & channels & mb);
        // Lights added besides the front's, when not fully occluded
        var added = -(Lanes(side1) + Lanes(side2) + Lanes(open));
        var factor = Vector128.ConditionalSelect(Vector128.Equals(added, Vector128<int>.One).AsSingle(),
            Vector128.Create(factors[1]), Vector128.Create(factors[2]));
        var ambient = Vector128.ConditionalSelect(Lanes(ambientOne).AsSingle(), Vector128.Create(factors[4]),
            Vector128.Create(factors[3]));
        factor = Vector128.ConditionalSelect(Lanes(side1 & side2 & open).AsSingle(), ambient, factor);
        factor = Vector128.ConditionalSelect(Lanes(keep).AsSingle(), factor, Vector128.Create(factors[0]));
        var result = (Scale(Vector128.ShiftRightLogical(high, 16), factor) << 24) |
                     (Scale(Vector128.ShiftRightLogical(low, 16), factor) << 16) |
                     (Scale(high & mask, factor) << 8) | Scale(low & mask, factor);
        result.StoreUnsafe(ref corners[0]);
        return (long)result.GetElement(0) + result.GetElement(1) + result.GetElement(2) + result.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Scale(Vector128<int> sums, Vector128<float> factor) =>
        Vector128.ConvertToInt32(Vector128.ConvertToSingle(sums) * factor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Lanes(int bits)
    {
        var lane = Vector128.Create(1, 2, 4, 8);
        return Vector128.Equals(Vector128.Create(bits) & lane, lane);
    }

    // occ is a public field: the quotients are the engine's divisions of its current value, made again when it changes
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Quotients Divisions(float occ)
    {
        var quotients = Volatile.Read(ref _quotients);
        if (NotNull(quotients) && BitConverter.SingleToInt32Bits(quotients.Occ) == BitConverter.SingleToInt32Bits(occ))
            return quotients;
        quotients = new Quotients(occ);
        Volatile.Write(ref _quotients, quotients);
        return quotients;
    }

    // The tesselator's halo: 34^3 cells in all three, as its constructor makes them
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Arrays(ChunkTesselator? tesselator, [NotNullWhen(true)] out Block[]? solid,
        [NotNullWhen(true)] out Block[]? fluid, [NotNullWhen(true)] out int[]? rgb)
    {
        (solid, fluid, rgb) = (null, null, null);
        if (!NotNull(tesselator)) return false;
        (solid, fluid, rgb) = (BlocksExt(tesselator), FluidsExt(tesselator), RgbsExt(tesselator));
        return Assert(solid is { Length: ExtCells }) && Assert(fluid is { Length: ExtCells }) &&
               Assert(rgb is { Length: ExtCells });
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Tables? Current(ChunkTesselator? tesselator)
    {
        if (!NotNull(tesselator)) return null;
        var (tables, blocks) = (Volatile.Read(ref _tables), BlocksFast(tesselator));
        if (!NotNull(blocks)) return null;
        if (tables is not null && ReferenceEquals(tables.Source, blocks)) return tables.Valid ? tables : null;
        tables = Tables.Build(blocks); // two threads may both build; either table is the same
        Volatile.Write(ref _tables, tables);
        return tables.Valid ? tables : null;
    }

    // 1: Block's AO methods; 2: plus constant-true ForFluidsLayer (kept to check its patches); 0: engine-lit
    internal static int Classify(Type type, List<MethodBase> overrides)
    {
        if (!NotNull(type) || !NotNull(overrides)) return 0;
        const BindingFlags instance = BindingFlags.Public | BindingFlags.Instance;
        try
        {
            if (!type.IsAssignableTo(typeof(Block))) return 0;
            var emit = type.GetMethod(nameof(Block.DoEmitSideAo), instance,
                [typeof(IGeometryTester), typeof(BlockFacing)]);
            var byFlag = type.GetMethod(nameof(Block.DoEmitSideAoByFlag), instance,
                [typeof(IGeometryTester), typeof(Vec3iAndFacingFlags), typeof(int)]);
            var fluids = type.GetMethod("get_" + nameof(Block.ForFluidsLayer), instance, Type.EmptyTypes);
            if (emit?.DeclaringType != typeof(Block) || byFlag?.DeclaringType != typeof(Block) || fluids is null) return 0;
            if (fluids.DeclaringType == typeof(Block)) return 1;
            if (fluids.GetBaseDefinition().DeclaringType != typeof(Block) ||
                EngineShape.Foreign([fluids], PatchKinds.All, null)) return 0;
            var kind = fluids.GetMethodBody()?.GetILAsByteArray() switch
            {
                [ReturnTrue, Return] => 2,
                [ReturnFalse, Return] => 1,
                _ => 0
            };
            if (kind == 0 || overrides.Contains(fluids)) return kind;
            if (overrides.Count >= MaxOverrides) return 0;
            overrides.Add(fluids);
            return kind;
        }
        catch (Exception e) when (e is AmbiguousMatchException or TypeLoadException or FileNotFoundException or
                                      FileLoadException or BadImageFormatException or MissingMemberException or
                                      InvalidOperationException)
        {
            return 0;
        }
    }

    // The engine's face for Six and the own tesselators, via the method entry and so the prefix above
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CalcBlockFaceLight")]
    internal static extern long Calc(TCTCache vars, int tileSide, int extNeibIndex3D);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "neighbourLightRGBS")]
    private static extern ref int[] Neighbours(TCTCache vars);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jsonLightRGB")]
    private static extern ref int[] JsonLight(JsonTesselator tesselator);

    private readonly record struct Entry(Block? Owner, bool FluidsLayer);

    [InlineArray(Samples)]
    internal struct Eight { private int _element; }

    [InlineArray(Around)]
    private struct Block27 { private int _element; }

    [InlineArray(Factors5)]
    private struct Floats5 { private float _element; }

    // One face's 8 samples: what the cell holds (EmitSideAo, Leaves, Absorbs, Custom bits) and its light
    internal struct Cells
    {
        public Eight Desc;
        public Eight Rgb;
    }

    // Face of blockFaceVerticesCentered: sample offsets, edge/corner side flags, 3x3x3 cell, DoEmitSideAo flag to test
    internal struct Side
    {
        public Eight Offset;
        public Eight First;
        public Eight Second;
        public Eight Local;
        public int Toward;
    }

    private sealed record Quotients(float Occ)
    {
        public float Half { get; } = Occ / 2;
        public float Third { get; } = Occ / 3;
        public float Fourth { get; } = Occ / 4;
        public bool Bounded { get; } = Occ is >= 0 and <= MaxFactor;
    }

    private sealed record Tables(Block[] Source, Entry[] Kinds, Geometry Geometry, MethodBase[] Overrides)
    {
        public bool Valid { get; } = Geometry.Valid && Kinds.Length <= MaxBlocks;

        // blockFaceVerticesCentered is written only in its static constructor: read with the kinds, never again
        public static Tables Build(Block[] blocks)
        {
            var kinds = new Entry[Math.Min(blocks.Length, MaxBlocks)];
            List<MethodBase> overrides = [];
            Dictionary<Type, int> byType = [];
            for (var i = 0; i < Math.Min(blocks.Length, MaxBlocks); i++)
            {
                var block = blocks[i];
                // The engine indexes blocksFast by id; a stray entry stays custom
                if (block is null || block.BlockId != i) continue;
                var type = block.GetType();
                if (!byType.TryGetValue(type, out var kind) && byType.Count < MaxTypes)
                    byType[type] = kind = Classify(type, overrides);
                if (kind != 0) kinds[i] = new Entry(block, kind == 2);
            }

            _ = Assert(overrides.Count <= MaxOverrides) && Assert(byType.Count <= MaxTypes);
            return new Tables(blocks, kinds, Geometry.Read(), [.. overrides]);
        }
    }

    private sealed class Geometry
    {
        private const int Rows = Samples + 1;

        private Geometry(Vec3iAndFacingFlags[][]? source)
        {
            Valid = source is { Length: Faces } && Assert(Sides.Length == Faces) && Fill(source);
            Cells = Valid ? Distinct() : [];
            _ = Assert(Cells.Length <= Around);
        }

        public Side[] Sides { get; } = new Side[Faces];
        public byte[] Cells { get; } // the cells some face samples, each once
        public bool Valid { get; }

        public static Geometry Read()
        {
            var source = CubeFaceVertices.blockFaceVerticesCentered;
            return new Geometry(NotNull(source) && Assert(source.Length == Faces) ? source : null);
        }

        // The index offset of a cell of the 3x3x3 block, numbered (y + 1) * 9 + (z + 1) * 3 + x + 1
        public static int OffsetOf(int cell) =>
            Index(cell, Around) ? (cell / 9 - 1) * TessSeams.Plane + (cell / 3 % 3 - 1) * Ext + (cell % 3 - 1) : 0;

        private byte[] Distinct()
        {
            var seen = new bool[Around];
            List<byte> cells = [];
            for (var i = 0; i < Slots; i++)
            {
                var cell = Sides[i / Samples].Local[i % Samples];
                if (!Index(cell, Around) || seen[cell]) continue;
                seen[cell] = true;
                cells.Add((byte)cell);
            }

            return Assert(cells.Count <= Around) ? [.. cells] : [];
        }

        private bool Fill(Vec3iAndFacingFlags[][] source)
        {
            if (!NotNull(source) || !Assert(source.Length == Faces)) return false;
            for (var t = 0; t < Faces; t++)
            {
                var row = source[t];
                var toward = BlockFacing.ALLFACES[t]?.Opposite?.Flag ?? -1;
                if (row is not { Length: >= Rows } || !Assert(toward is > 0 and < 64)) return false;
                Sides[t].Toward = toward;
                for (var k = 0; k < Samples; k++)
                    if (!Take(t, k, row[k])) return false;
            }

            return true;
        }

        // A sample lies within one cell of the lit one, offset and flags as the engine's constructor sets them
        private bool Take(int t, int k, Vec3iAndFacingFlags? vec)
        {
            if (!NotNull(vec) || !Index(t * Samples + k, Slots)) return false;
            ref var side = ref Sides[t];
            var edge = k < Edges;
            (side.Offset[k], side.First[k], side.Second[k]) = (vec.extIndexOffset,
                edge ? vec.OppositeFlagsUpperOrLeft : vec.OppositeFlags,
                edge ? vec.OppositeFlagsLowerOrRight : vec.FacingFlags);
            side.Local[k] = (vec.Y + 1) * 9 + (vec.Z + 1) * 3 + vec.X + 1;
            return Math.Max(Math.Abs(vec.X), Math.Max(Math.Abs(vec.Y), Math.Abs(vec.Z))) <= 1 &&
                   vec.extIndexOffset == (vec.Y * Ext + vec.Z) * Ext + vec.X &&
                   Assert(OffsetOf(side.Local[k]) == vec.extIndexOffset) &&
                   (uint)side.First[k] < 64 && (uint)side.Second[k] < 64;
        }
    }
}
