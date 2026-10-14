using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Tessellation;

// Replaces BuildExtendedChunkData's GetRange_Faster/GetRange calls with whole-row decoders, each layer's read lock once
// per row in the engine's nesting (blocks, light2Lock, light, fluids), reproducing what the engine finds:
// GetBlockAsBlock's decoder (getBlockOne..Five/GeneralCase over its layer, or getBlockAir) with the per-thread
// TessSafety.Palette table and stale entries, the GetRange hand-over when paletteCount != blocksArrayCount; GetUnsafe
// on the live bitsize (0 reads 0 even with a palette) and its blocksFast[0] -> blockAir store; light2 planes with the
// live light palette, else the light Get; fluids via Get and blocksFast. Anything unexpected (unknown or foreign
// delegate, short plane or array, index past a palette, row overrun, short LightUtil) goes to the engine unwritten. A
// row lock delays a writer by <= 32 cells; readers don't wait on each other and writers take blocks then fluids as
// here: no cycle. Racy fields are read once per row.
[SkipLocalsInit]
internal static class ExtendedRows
{
    private const int RowLength = 32, RowCount = 1024, Cells = RowLength * RowCount, MaxPlanes = 15, FieldPlanes = 4;
    private const int FasterSites = 4, RangeSites = 1, MaxDecoders = 16;
    private const int LightLevels = 32, Hues = 64, Saturations = 8, BytePlanes = 8, SpreadEntries = 256;

    private const string ClientData = "Vintagestory.Client.NoObf.ClientChunkData, VintagestoryLib";

    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0xFA0A3FB468FEEF46UL;

    private const int RowsCounter = 0, CellsCounter = 1, FallbacksCounter = 2;

    internal static readonly Type? ClientType =
        typeof(ClientChunk).Assembly.GetType("Vintagestory.Client.NoObf.ClientChunkData");

    private static readonly (MethodInfo? Method, Decoder Kind)[] Decoders = DecoderTable();
    private static readonly ulong[] Spreads = SpreadTable(); // bit j of a byte moved to bit 0 of byte j

    private static readonly Tally Counts = new(FallbacksCounter + 1);

    // Per tesselator, so per thread: the last one whose tables were long enough
    [ThreadStatic] private static ColorUtil.LightUtil? _converter;

    private static MethodBase?[] _bypassed = [];

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    // Another mod patches a method the rows would bypass: the engine's methods run
    public static bool Blocked { get; private set; }

    public static long Decoded => Counts.Total(RowsCounter);
    public static long CellsDecoded => Counts.Total(CellsCounter);
    public static long Fallbacks => Counts.Total(FallbacksCounter);

    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape)
    {
        (Rewritten, Blocked, _converter) = (false, false, null);
        var target = Target();
        if (!NotNull(harmony) || !NotNull(target) || !TessSeams.Accessible(nameof(ExtendedRows), logger) ||
            !EngineShape.Matches(Shaped(), shape, nameof(ExtendedRows), logger)) return;
        _bypassed = [.. EngineMethods(), .. Seams()];
        _ = NotNull(harmony.Patch(target, transpiler: new HarmonyMethod(Rewrite)));
        Recheck();
    }

    internal static void Recheck()
    {
        if (!Rewritten) return;
        var foreign = EngineShape.Foreign(_bypassed, PatchKinds.All, null, typeof(ExtendedRows),
            typeof(TessSafety));
        Blocked = EngineShape.Report(TessSeams.Logger, nameof(ExtendedRows), Blocked, foreign);
    }

    internal static MethodInfo? Target()
    {
        var method = TessSeams.Method(typeof(ChunkTesselator), "BuildExtendedChunkData", typeof(ClientChunk),
            typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool));
        return NotNull(method) && Assert(method.ReturnType == typeof(void)) ? method : null;
    }

    internal static MethodInfo?[] EngineMethods()
    {
        Type[] row =
        [
            typeof(Block[]), typeof(Block[]), typeof(int[]), typeof(int), typeof(int), typeof(int), typeof(Block[]),
            typeof(ColorUtil.LightUtil)
        ];
        MethodInfo?[] methods =
            [TessSeams.Method(ClientType, "GetRange_Faster", row), TessSeams.Method(ClientType, "GetRange", row)];
        _ = Assert(methods.Length == 2) && NotNull(ClientType);
        return methods;
    }

    private static MethodBase?[] Seams()
    {
        var data = typeof(ChunkData);
        MethodBase?[] seams =
        [
            TessSeams.Method(ClientType, "Light2", typeof(int)), TessSeams.Method(data, "Light", typeof(int)),
            TessSeams.Method(data, nameof(ChunkData.GetFluid), typeof(int)),
            TessSeams.Method(typeof(ChunkDataLayer), nameof(ChunkDataLayer.GetUnsafe), typeof(int)),
            TessSeams.Method(typeof(ColorUtil.LightUtil), nameof(ColorUtil.LightUtil.ToRgba), typeof(uint),
                typeof(int)),
            .. Decoders.Select(entry => entry.Method)
        ];
        _ = Assert(seams.Length < EngineShape.MaxMethods) && Assert(Decoders.Length > 0);
        return seams;
    }

    // Bodies the rows reproduce, the rewritten method, and BuildFastBlockAccessArray (fills GetBlockAsBlock's table)
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] methods =
        [
            Target(), .. EngineMethods(), .. Seams(),
            TessSeams.Method(ClientType, "BuildFastBlockAccessArray", typeof(Block[]))
        ];
        _ = Assert(methods.Length <= EngineShape.MaxMethods) && Assert(methods.Length > Decoders.Length);
        return methods;
    }

    private static (MethodInfo? Method, Decoder Kind)[] DecoderTable()
    {
        var (layer, blocks) = (typeof(ChunkDataLayer), typeof(BlockChunkDataLayer));
        (MethodInfo? Method, Decoder Kind)[] table =
        [
            (TessSeams.Method(layer, "GetFromBits0", typeof(int)), Decoder.Zero),
            (TessSeams.Method(layer, "GetFromBits1", typeof(int)), Decoder.One),
            (TessSeams.Method(layer, "GetFromBits2", typeof(int)), Decoder.Two),
            (TessSeams.Method(layer, "GetFromBits3", typeof(int)), Decoder.Three),
            (TessSeams.Method(layer, "GetFromBits4", typeof(int)), Decoder.Four),
            (TessSeams.Method(layer, "GetFromBits5", typeof(int)), Decoder.Five),
            (TessSeams.Method(layer, "GetGeneralCase", typeof(int)), Decoder.General),
            (TessSeams.Method(ClientType, "getBlockAir", typeof(int)), Decoder.Air),
            (TessSeams.Method(blocks, "getBlockOne", typeof(int)), Decoder.One),
            (TessSeams.Method(blocks, "getBlockTwo", typeof(int)), Decoder.Two),
            (TessSeams.Method(blocks, "getBlockThree", typeof(int)), Decoder.Three),
            (TessSeams.Method(blocks, "getBlockFour", typeof(int)), Decoder.Four),
            (TessSeams.Method(blocks, "getBlockFive", typeof(int)), Decoder.Five),
            (TessSeams.Method(blocks, "getBlockGeneralCase", typeof(int)), Decoder.General)
        ];
        return Assert(table.Length <= MaxDecoders) && Assert(table[7].Kind == Decoder.Air) ? table : [];
    }

    // Exactly four GetRange_Faster and one GetRange call become the helpers below, same stack; else the IL stays
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var (engine, self) = (EngineMethods(), typeof(ExtendedRows));
        var (faster, range) = (AccessTools.Method(self, nameof(Faster)), AccessTools.Method(self, nameof(Range)));
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code)) ||
            engine is not [{ } engineFaster, { } engineRange] ||
            !NotNull(faster) || !NotNull(range) || !Assert(Il.Count(code, c => c.Calls(engineFaster)) == FasterSites) ||
            !Assert(Il.Count(code, c => c.Calls(engineRange)) == RangeSites)) return code;
        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
            if (code[i].Calls(engineFaster) && Assert(Il.Substitute(code, i, faster))) sites++;
            else if (code[i].Calls(engineRange) && Assert(Il.Substitute(code, i, range))) sites++;

        // All or none: every site has the stack effect of Faster and Range
        _ = Assert(sites is 0 or FasterSites + RangeSites);
        Rewritten = sites > 0; // rows run from any substituted site, so Recheck has to guard them
        return code;
    }

    // Stands in for data.GetRange_Faster(...): 32 or 2 cells of one row of the chunk
    internal static void Faster(ChunkData data, Block[] blocksExt, Block[] fluidsExt, int[] rgbsExt, int extIndex3D,
        int index3D, int index3DEnd, Block[] blocksFast, ColorUtil.LightUtil lightConverter)
    {
        // An empty range is the engine's (it writes one cell)
        _ = Assert(index3DEnd - index3D <= RowLength) && NotNull(data);
        if (!Row(data, false, blocksExt, fluidsExt, rgbsExt, extIndex3D, index3D, index3DEnd, blocksFast,
                lightConverter))
            EngineFaster(data, blocksExt, fluidsExt, rgbsExt, extIndex3D, index3D, index3DEnd, blocksFast,
                lightConverter);
    }

    // Stands in for data.GetRange(...): whole rows of the neighbours' shell
    internal static void Range(ChunkData data, Block[] blocksExt, Block[] fluidsExt, int[] rgbsExt, int extIndex3D,
        int index3D, int index3DEnd, Block[] blocksFast, ColorUtil.LightUtil lightConverter)
    {
        _ = Assert(index3DEnd - index3D <= RowLength) && NotNull(data);
        if (!Row(data, true, blocksExt, fluidsExt, rgbsExt, extIndex3D, index3D, index3DEnd, blocksFast,
                lightConverter))
            EngineRange(data, blocksExt, fluidsExt, rgbsExt, extIndex3D, index3D, index3DEnd, blocksFast,
                lightConverter);
    }

    // True when the row was decoded into the arrays; false leaves them unwritten for the engine's method
    private static bool Row(ChunkData data, bool range, Block[] blocksExt, Block[] fluidsExt, int[] rgbsExt, int ext,
        int index3D, int end, Block[] blocksFast, ColorUtil.LightUtil converter)
    {
        if (!Enabled || Blocked) return false;
        var n = end - index3D;
        if (!Callable(data, blocksExt, fluidsExt, rgbsExt, ext, index3D, end, blocksFast, converter) ||
            !Decode(data, range, index3D, blocksExt.AsSpan(ext + 1, n), fluidsExt.AsSpan(ext + 1, n),
                rgbsExt.AsSpan(ext + 1, n), blocksFast, converter))
        {
            if (Counting.Hud) Counts.Add(FallbacksCounter, 1);
            return false;
        }

        if (!Counting.Hud) return true;
        Counts.Add(RowsCounter, 1);
        Counts.Add(CellsCounter, n);
        return Assert(n > 0);
    }

    private static bool Callable(ChunkData? data, Block[]? blocksExt, Block[]? fluidsExt, int[]? rgbsExt, int ext,
        int index3D, int end, Block[]? blocksFast, ColorUtil.LightUtil? converter)
    {
        if (data is null || data.GetType() != ClientType || blocksFast is null || !Converts(converter)) return false;
        if (blocksExt?.GetType() != typeof(Block[]) || fluidsExt?.GetType() != typeof(Block[]) ||
            rgbsExt is null) return false;
        var n = end - index3D;
        if (index3D < 0 || index3D >= Cells || n <= 0 || (index3D & (RowLength - 1)) + n > RowLength ||
            ext < -1) return false;
        var room = Math.Min(Math.Min(blocksExt.Length, fluidsExt.Length), rgbsExt.Length);
        return ext < room - n && Assert(n <= RowLength) && Index(ext + n, room);
    }

    // ToRgba never throws for a LightUtil whose level tables cover every light value: one check per LightUtil instance
    private static bool Converts(ColorUtil.LightUtil? converter)
    {
        if (converter is null) return false;
        if (ReferenceEquals(converter, _converter)) return true;
        if (BlockLevels(converter) is not { Length: >= LightLevels } ||
            SunLevels(converter) is not { Length: >= LightLevels } || HueLevels(converter) is not { Length: >= Hues } ||
            SatLevels(converter) is not { Length: >= Saturations }) return false;
        _converter = converter;
        return Assert(ReferenceEquals(_converter, converter));
    }

    // Nothing is written unless every cell decoded
    private static bool Decode(ChunkData data, bool range, int index3D, Span<Block> blocks, Span<Block> fluids,
        Span<int> rgbs, Block[] blocksFast, ColorUtil.LightUtil converter)
    {
        var n = blocks.Length;
        if (!Assert(n is > 0 and <= RowLength) || !Assert(fluids.Length == n && rgbs.Length == n)) return false;
        Span<int> cells = stackalloc int[3 * RowLength];
        var solid = cells[..n];
        var light = cells.Slice(RowLength, n);
        var fluid = cells.Slice(2 * RowLength, n);
        var (row, x0) = (index3D >> 5, index3D & (RowLength - 1));
        if (!Index(row, RowCount) || !Assert(x0 + n <= RowLength)) return false;
        var layer = data.blocksLayer;
        range |= layer is not null &&
                 layer.paletteCount != BlocksArrayCount(data); // GetRange_Faster hands over to GetRange
        if (layer is null && range)
        {
            if (blocksFast.Length == 0) return false;
            BlockAir(data) = blocksFast[0]; // GetRange's side effect, before its lock
        }

        ref var gate = ref Light2Lock(data);
        if (layer is not null) layer.readWriteLock.AcquireReadLock();
        gate.AcquireReadLock();
        bool ok, solidSame, lightSame = false, fluidSame = false;
        SolidMode mode;
        Block? air;
        Block[]? table;
        try
        {
            ok = Solid(data, layer, range, row, x0, solid, out mode, out air, out table, out solidSame) &&
                 LightRow(data, row, x0, light, out lightSame) &&
                 LayerRow(data.fluidsLayer, row, x0, fluid, out fluidSame);
        }
        finally
        {
            gate.ReleaseReadLock();
            if (layer is not null) layer.readWriteLock.ReleaseReadLock();
        }

        if (!ok || !Fits(mode, table, blocksFast, solid, solidSame) ||
            !Fits(SolidMode.Values, null, blocksFast, fluid, fluidSame)) return false;
        // Built on the stack and copied in one move: card marks once per range instead of per cell
        var (solidRow, fluidRow) = (new BlockRow(), new BlockRow());
        Span<Block> solidCells = solidRow[..n], fluidCells = fluidRow[..n];
        if (mode == SolidMode.Air) solidCells.Fill(air!);
        else WriteBlocks(mode == SolidMode.Values || !NotNull(table) ? blocksFast : table, solid, solidSame, solidCells);
        WriteBlocks(blocksFast, fluid, fluidSame, fluidCells);
        solidCells.CopyTo(blocks);
        fluidCells.CopyTo(fluids);
        Rgba(converter, light, lightSame, rgbs);
        return true;
    }

    // Solid blocks: air (no layer, getBlockAir), table indices (GetBlockAsBlock) or blocksFast values (GetUnsafe)
    private static bool Solid(ChunkData data, BlockChunkDataLayer? layer, bool range, int row, int x0, Span<int> solid,
        out SolidMode mode, out Block? air, out Block[]? table, out bool same)
    {
        (mode, air, table, same) = (SolidMode.Air, null, null, true);
        if (!Index(row, RowCount) || !Assert(solid.Length > 0)) return false;
        if (layer is null)
        {
            air = BlockAir(data);
            return true;
        }

        Span<int> words = stackalloc int[MaxPlanes];
        if (range)
        {
            mode = SolidMode.Values;
            var bits = TessSeams.Bitsize(layer);
            if (bits == 0) return Zero(solid, out same); // GetUnsafe: 0 without looking at the palette
            var kind = bits is > 0 and <= 5 ? Decoder.Zero + (byte)bits : Decoder.UnsafeGeneral;
            return Words(layer, kind, row, words, out var count) &&
                   Lookup(layer.palette, words[..count], x0, solid, out same);
        }

        var get = GetBlockAsBlock(data);
        var decoder = get is null ? Decoder.Unknown : Classify(get);
        if (decoder == Decoder.Air && get!.Target is ChunkData owner && owner.GetType() == ClientType)
        {
            air = BlockAir(owner);
            return true;
        }

        if (decoder is Decoder.Unknown or Decoder.Zero or Decoder.Air ||
            get!.Target is not BlockChunkDataLayer source) return false;
        (mode, table) = (SolidMode.Table, TessSafety.Palette());
        if (table is null || !Words(source, decoder, row, words, out var planes)) return false;
        same = Indices(words[..planes], x0, solid);
        return Assert(planes <= MaxPlanes);
    }

    // As GetRange under light2Lock: light2 planes with the live light palette, else the light layer's Get
    private static bool LightRow(ChunkData data, int row, int x0, Span<int> values, out bool same)
    {
        _ = Assert(values.Length is > 0 and <= RowLength) && Assert(x0 + values.Length <= RowLength);
        var buffer = Light2(data);
        if (buffer is null) return LayerRow(data.lightLayer, row, x0, values, out same);
        var palette = data.lightLayer?.palette;
        if (palette is null) return Zero(values, out same);
        same = false;
        if (!Index(row, RowCount) || buffer.Length > MaxPlanes) return false;
        Span<int> words = stackalloc int[MaxPlanes];
        return FromArray(buffer, 0, buffer.Length, row, words) &&
               Lookup(palette, words[..buffer.Length], x0, values, out same);
    }

    // A layer's Get over the row: 0 for no layer or GetFromBits0, else palette[index], locked as the engine's delegate
    private static bool LayerRow(ChunkDataLayer? layer, int row, int x0, Span<int> values, out bool same)
    {
        same = false;
        _ = Assert(values.Length is > 0 and <= RowLength) && Assert(x0 + values.Length <= RowLength);
        if (layer is null) return Zero(values, out same);
        var get = layer.Get;
        var kind = get is not null && ReferenceEquals(get.Target, layer) ? Classify(get) : Decoder.Unknown;
        if (kind == Decoder.Zero) return Zero(values, out same);
        if (kind is Decoder.Unknown or Decoder.Air || !Index(row, RowCount)) return false;
        Span<int> words = stackalloc int[MaxPlanes];
        int count;
        bool read;
        int[]? palette;
        // GetFromBits2..5 and GetGeneralCase read the words and the palette under the lock
        var locked = kind != Decoder.One;
        if (locked) layer.readWriteLock.AcquireReadLock();
        try
        {
            read = Words(layer, kind, row, words, out count);
            palette = layer.palette;
        }
        finally
        {
            if (locked) layer.readWriteLock.ReleaseReadLock();
        }

        return read && Lookup(palette, words[..count], x0, values, out same);
    }

    private static bool Zero(Span<int> values, out bool same)
    {
        values.Clear();
        same = true;
        return Assert(values.Length > 0);
    }

    // Row words of the planes a decoder reads (1-5 planes, general, GetUnsafe); false where the engine would throw
    private static bool Words(ChunkDataLayer layer, Decoder kind, int row, Span<int> words, out int count)
    {
        count = kind switch
        {
            >= Decoder.One and <= Decoder.Five => kind - Decoder.Zero,
            Decoder.General or Decoder.UnsafeGeneral => TessSeams.Bitsize(layer),
            _ => -1
        };
        if ((uint)count > MaxPlanes || !Index(row, RowCount) || !Assert(words.Length >= MaxPlanes)) return false;
        if (kind == Decoder.General) return FromArray(TessSeams.DataBits(layer), 0, count, row, words);
        var fields = Math.Min(count, kind == Decoder.UnsafeGeneral ? 1 : FieldPlanes);
        for (var k = 0; k < Math.Min(fields, FieldPlanes); k++)
        {
            var plane = k switch
            {
                0 => DataBit0(layer),
                1 => DataBit1(layer),
                2 => DataBit2(layer),
                _ => DataBit3(layer)
            };
            if (plane is null || (uint)row >= (uint)plane.Length) return false;
            words[k] = plane[row];
        }

        return FromArray(TessSeams.DataBits(layer), fields, count, row, words);
    }

    private static bool FromArray(int[]?[]? planes, int from, int count, int row, Span<int> words)
    {
        if (!Assert(from >= 0) || count <= from) return from >= 0;
        if (planes is null || planes.Length < count || !Assert(count <= words.Length)) return false;
        for (var k = from; k < Math.Min(count, MaxPlanes); k++)
        {
            var plane = planes[k];
            if (plane is null || (uint)row >= (uint)plane.Length) return false;
            words[k] = plane[row];
        }

        return true;
    }

    // palette[index]; false without palette or past it (engine throws). A 2^planes palette holds any index.
    private static bool Lookup(int[]? palette, ReadOnlySpan<int> words, int x0, Span<int> values, out bool same)
    {
        same = Indices(words, x0, values);
        if (palette is null || !Assert(values.Length <= RowLength)) return false;
        if (same)
        {
            if ((uint)values[0] >= (uint)palette.Length) return false;
            values.Fill(palette[values[0]]);
            return true;
        }

        if (!Assert(words.Length <= MaxPlanes) || palette.Length < 1 << words.Length)
            for (var x = 0; x < Math.Min(values.Length, RowLength); x++)
                if ((uint)values[x] >= (uint)palette.Length) return false;
        for (var x = 0; x < Math.Min(values.Length, RowLength); x++) values[x] = palette[values[x]];
        return true;
    }

    // Index of cells x0..: plane k's bit x0 + x is index bit k; true when all share one (sure if all words are 0 or -1)
    private static bool Indices(ReadOnlySpan<int> words, int x0, Span<int> indices)
    {
        _ = Assert(words.Length <= MaxPlanes) && Assert(x0 + indices.Length <= RowLength);
        if (indices.Length == 1)
        {
            var index = 0;
            for (var k = 0; k < Math.Min(words.Length, MaxPlanes); k++) index |= (int)(((uint)words[k] >> x0) & 1) << k;
            indices[0] = index;
            return true;
        }

        int constant = 0, mixed = 0;
        for (var k = 0; k < Math.Min(words.Length, MaxPlanes); k++)
        {
            var word = words[k];
            if (word == -1) constant |= 1 << k;
            else if (word != 0) mixed |= 1 << k;
        }

        if (mixed == 0)
        {
            indices.Fill(constant);
            return true;
        }

        if (words.Length <= BytePlanes && BitConverter.IsLittleEndian) Spread(words, x0, indices);
        else Scatter(words, x0, indices, constant, mixed);
        return false;
    }

    // <= 8 planes: each plane byte spreads to eight cell bytes, shifted to the plane's bit
    private static void Spread(ReadOnlySpan<int> words, int x0, Span<int> indices)
    {
        var spread = Spreads;
        _ = Assert(spread.Length == SpreadEntries) && Assert(words.Length <= BytePlanes);
        ulong a = 0, b = 0, c = 0, d = 0;
        for (var k = 0; k < Math.Min(words.Length, BytePlanes); k++)
        {
            var word = (uint)words[k] >> x0;
            a |= spread[word & 0xFF] << k;
            b |= spread[(word >> 8) & 0xFF] << k;
            c |= spread[(word >> 16) & 0xFF] << k;
            d |= spread[word >> 24] << k;
        }

        var bytes = MemoryMarshal.AsBytes(stackalloc ulong[] { a, b, c, d });
        for (var x = 0; x < Math.Min(indices.Length, RowLength); x++) indices[x] = bytes[x];
    }

    // More than eight planes: one pass per plane whose word is neither 0 nor -1
    private static void Scatter(ReadOnlySpan<int> words, int x0, Span<int> indices, int constant, int mixed)
    {
        _ = Assert(mixed != 0) && Assert((constant & mixed) == 0);
        indices.Fill(constant);
        for (var k = 0; k < Math.Min(words.Length, MaxPlanes); k++)
        {
            if ((mixed & (1 << k)) == 0) continue;
            var bits = (int)((uint)words[k] >> x0);
            for (var x = 0; x < Math.Min(indices.Length, RowLength); x++) indices[x] |= ((bits >> x) & 1) << k;
        }
    }

    private static ulong[] SpreadTable()
    {
        var table = new ulong[SpreadEntries];
        for (var value = 0; value < SpreadEntries; value++)
            for (var bit = 0; bit < BytePlanes; bit++)
                if ((value & (1 << bit)) != 0) table[value] |= 1UL << (8 * bit);
        return Assert(table[255] == ulong.MaxValue / 255) && Assert(table[1] == 1) ? table : new ulong[SpreadEntries];
    }

    private static bool Fits(SolidMode mode, Block[]? table, Block[] blocksFast, ReadOnlySpan<int> values, bool same)
    {
        if (mode == SolidMode.Air) return true;
        var limit = (uint)(mode == SolidMode.Table ? table?.Length ?? 0 : blocksFast.Length);
        if (same) return (uint)values[0] < limit;
        for (var x = 0; x < Math.Min(values.Length, RowLength); x++)
            if ((uint)values[x] >= limit) return false;
        return Assert(limit > 0);
    }

    private static void WriteBlocks(Block[] lookup, ReadOnlySpan<int> values, bool same, Span<Block> blocks)
    {
        if (!Assert(values.Length == blocks.Length)) return;
        if (same) blocks.Fill(lookup[values[0]]);
        else
            for (var x = 0; x < Math.Min(blocks.Length, RowLength); x++)
                blocks[x] = lookup[values[x]];
    }

    // ToRgba((ushort)light, (light >> 16) & 7) per cell, once per run of equal values (ToRgba is pure)
    private static void Rgba(ColorUtil.LightUtil converter, ReadOnlySpan<int> light, bool same, Span<int> rgbs)
    {
        if (!Assert(light.Length == rgbs.Length) || !NotNull(converter)) return;
        var (last, rgba) = (light[0], Convert(converter, light[0]));
        if (same)
        {
            rgbs.Fill(rgba);
            return;
        }

        for (var x = 0; x < Math.Min(rgbs.Length, RowLength); x++)
        {
            if (light[x] != last) (last, rgba) = (light[x], Convert(converter, light[x]));
            rgbs[x] = rgba;
        }
    }

    private static int Convert(ColorUtil.LightUtil converter, int value)
    {
        var light = (uint)value;
        return converter.ToRgba((ushort)light, (int)((light >> 16) & 7));
    }

    // Which engine decoder a delegate calls, by method identity (Delegate.Method is cached)
    private static Decoder Classify(Delegate get)
    {
        if (!get.HasSingleTarget) return Decoder.Unknown;
        var method = get.Method;
        var decoders = Decoders;
        for (var k = 0; k < Math.Min(decoders.Length, MaxDecoders); k++)
            if (ReferenceEquals(decoders[k].Method, method)) return decoders[k].Kind;
        return Decoder.Unknown;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetRange_Faster")]
    private static extern void EngineFaster([UnsafeAccessorType(ClientData)] object data, Block[] blocksExt,
        Block[] fluidsExt, int[] rgbsExt, int extIndex3D, int index3D, int index3DEnd, Block[] blocksFast,
        ColorUtil.LightUtil lightConverter);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetRange")]
    private static extern void EngineRange([UnsafeAccessorType(ClientData)] object data, Block[] blocksExt,
        Block[] fluidsExt, int[] rgbsExt, int extIndex3D, int index3D, int index3DEnd, Block[] blocksFast,
        ColorUtil.LightUtil lightConverter);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "light2")]
    private static extern ref int[][]? Light2([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "light2Lock")]
    private static extern ref FastRWLock Light2Lock([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blocksArrayCount")]
    private static extern ref int BlocksArrayCount([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockAir")]
    private static extern ref Block? BlockAir([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "GetBlockAsBlock")]
    private static extern ref System.Func<int, Block>? GetBlockAsBlock([UnsafeAccessorType(ClientData)] object data);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBit0")]
    private static extern ref int[]? DataBit0(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBit1")]
    private static extern ref int[]? DataBit1(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBit2")]
    private static extern ref int[]? DataBit2(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBit3")]
    private static extern ref int[]? DataBit3(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockLightlevelsByte")]
    private static extern ref byte[]? BlockLevels(ColorUtil.LightUtil converter);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "sunLightlevelsByte")]
    private static extern ref byte[]? SunLevels(ColorUtil.LightUtil converter);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "hueLevels")]
    private static extern ref byte[]? HueLevels(ColorUtil.LightUtil converter);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "satLevels")]
    private static extern ref byte[]? SatLevels(ColorUtil.LightUtil converter);

    // One..Five follow Zero (planes counted from it). General: dataBits[0..n); UnsafeGeneral: dataBit0, dataBits[1..n)
    private enum Decoder : byte { Unknown, Zero, One, Two, Three, Four, Five, General, UnsafeGeneral, Air }

    private enum SolidMode : byte { Air, Table, Values }

    [InlineArray(RowLength)]
    private struct BlockRow { private Block _cell; }
}
