using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Tessellation;

// Shared by the fast paths (ExtendedRows, VisibleFaces, FaceLight, OwnTessellation, OccludedChunks): 34^3 halo,
// neighbour moves, engine fields, and foreign-patch rechecks at install, first pass and every RecheckMs after
internal static class TessSeams
{
    public const int Size = 32, Ext = 34, Plane = Ext * Ext, ExtCells = Ext * Ext * Ext, Cells = Size * Size * Size;
    public const int Faces = 6, RecheckMs = 2000;

    // TileSideEnum's sides, and the halo index of the chunk's first cell
    public const int North = 0, West = 3, Up = 4, Down = 5, Origin = Plane + Ext + 1;

    // TileSideEnum.MoveIndex as ChunkTesselator.Start sets it: north, east, south, west, up, down
    public static readonly int[] Moves = [-Ext, 1, Ext, -1, Plane, -Plane];

    private static long _nextCheck;

    internal static ILogger? Logger { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger)
    {
        (Logger, _nextCheck) = (logger, 0);
        var pass = AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.NowProcessChunk));
        if (!NotNull(harmony) || !NotNull(pass)) return; // the features keep what they found at install
        _ = NotNull(harmony.Patch(pass, new HarmonyMethod(Pass) { priority = Priority.First }));
    }

    // Before skipping prefixes (OccludedChunks), every thread; the time's advancer rechecks; flags are plain bools
    private static void Pass()
    {
        var now = Environment.TickCount64;
        var due = Volatile.Read(ref _nextCheck);
        if (now < due || Interlocked.CompareExchange(ref _nextCheck, now + RecheckMs, due) != due) return;
        _ = Assert(now + RecheckMs > now);
        Recheck();
    }

    public static void Recheck()
    {
        ExtendedRows.Recheck();
        VisibleFaces.Recheck();
        FaceLight.Recheck();
        OwnTessellation.Recheck();
        OccludedChunks.Recheck();
    }

    // Load or flight backlog: memory-only work (DeepCulling, FarFoliage going leaner, PoolTrim) waits; geometry doesn't
    public static bool Backlogged =>
        Vintagestory.Client.RuntimeStats.chunksAwaitingTesselation > 150 || Vintagestory.Client.RuntimeStats.chunksAwaitingPooling > 100;

    public static bool Standard(int[]? moves) =>
        NotNull(moves) && moves.Length == Faces && Assert(Moves.Length == Faces) && moves.AsSpan().SequenceEqual(Moves);

    // Accessor fields with types: a renamed one would throw MissingFieldException on the tessellation thread
    public static bool Accessible(string feature, ILogger? logger)
    {
        var (tesselator, layer) = (typeof(ChunkTesselator), typeof(ChunkDataLayer));
        (Type Owner, string Name, Type Type)[] fields =
        [
            (tesselator, "currentChunkBlocksExt", typeof(Block[])),
            (tesselator, "currentChunkFluidBlocksExt", typeof(Block[])),
            (tesselator, "currentChunkRgbsExt", typeof(int[])), (tesselator, "currentChunkDraw32", typeof(byte[])),
            (tesselator, "blocksFast", typeof(Block[])), (tesselator, "tmpPos", typeof(BlockPos)),
            (tesselator, "game", typeof(ClientMain)),
            (layer, "dataBits", typeof(int[][])), (layer, "bitsize", typeof(int))
        ];
        return Fields(feature, logger, "the engine's version runs", fields);
    }

    public static bool Fields(string feature, ILogger? logger, string instead, (Type Owner, string Name, Type Type)[] fields)
    {
        foreach (var (owner, name, type) in fields.Bounded(Size))
        {
            if (AccessTools.DeclaredField(owner, name)?.FieldType == type) continue;
            logger?.Warning("Komet {0}: the engine field {1}.{2} is missing or of another type, {3}", feature, owner.Name, name,
                instead);
            return false;
        }

        return NotNull(feature) && NotNull(instead) && Assert(fields.Length < Size);
    }

    // The method or null, for bodies to fingerprint or seams to watch (EngineShape counts a missing one as mismatch)
    public static MethodInfo? Method(Type? type, string name, params Type[] parameters) =>
        type is null || !NotNull(name) ? null : AccessTools.DeclaredMethod(type, name, parameters);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkBlocksExt")]
    internal static extern ref Block[]? BlocksExt(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkFluidBlocksExt")]
    internal static extern ref Block[]? FluidsExt(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkRgbsExt")]
    internal static extern ref int[]? RgbsExt(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkDraw32")]
    internal static extern ref byte[]? Draw(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blocksFast")]
    internal static extern ref Block[]? BlocksFast(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "tmpPos")]
    internal static extern ref BlockPos? TmpPos(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    internal static extern ref ClientMain? Game(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "started")]
    internal static extern ref bool Started(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBits")]
    internal static extern ref int[]?[]? DataBits(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "bitsize")]
    internal static extern ref int Bitsize(ChunkDataLayer layer);
}
