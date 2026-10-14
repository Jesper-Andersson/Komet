using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Microsoft.Data.Sqlite;
using SQLitePCL;
using Vintagestory.API.MathTools;

namespace Komet.World;

// MapDB.SetMapPieces serializes each piece via protobuf-net and inserts through Microsoft.Data.Sqlite: ~40 MB/s on the
// map thread while chunks stream. The prefix writes the same rows with protobuf-net's exact bytes from the thread's
// buffer.
internal static class MapSaveScratch
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xF8710161ED68084EUL;

    private const string Db = "Vintagestory.GameContent.MapDB", Piece = "Vintagestory.GameContent.MapPieceDB";
    private const string Insert = "INSERT OR REPLACE INTO mappiece (position, data) VALUES (@pos, @data)";
    private const byte Tag = 1 << 3; // field 1, varint
    private const int MaxVarint = 10, MaxPieces = 1 << 20, MaxPixels = 1 << 16;

    [ThreadStatic] private static byte[]? _buffer;
    private static readonly SeamGuard Guard = new(nameof(MapSaveScratch), PatchKinds.Replacing, [typeof(MapSaveScratch)]);
    private static Action<object, sqlite3>? _write;

    public static bool Enabled { get; set; } = true;

    // Body unverified, essentials missing or another mod patches it: the engine saves
    internal static bool Blocked => Guard.Blocked;

    internal static bool Matched => Guard.Matched;

    internal static Action Recheck { get; } = static () => Guard.Recheck();

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        _write = null;
        var seams = Guard.Reset(Seams(), logger);
        if (!NotNull(harmony) || !Assert(seams.Length == 1) || seams[0] is null ||
            AccessTools.TypeByName(Piece) is not { } piece || !Guard.Match(seams, fingerprint)) return;
        _write = AccessTools.Method(typeof(MapSaveScratch), nameof(Write)).MakeGenericMethod(piece)
            .CreateDelegate<Action<object, sqlite3>>();
        _ = NotNull(harmony.Patch(seams[0], new HarmonyMethod(Save)));
        Recheck();
    }

    internal static MethodBase?[] Seams()
    {
        var (db, piece) = (AccessTools.TypeByName(Db), AccessTools.TypeByName(Piece));
        MethodBase?[] seams =
        [
            db is null || piece is null
                ? null
                : AccessTools.DeclaredMethod(db, "SetMapPieces",
                    [typeof(Dictionary<,>).MakeGenericType(typeof(FastVec2i), piece)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    internal static bool Save(object pieces, SqliteConnection? ___sqliteConn)
    {
        if (!Enabled || Blocked || _write is null || pieces is null || ___sqliteConn?.Handle is not { } db) return true;
        using var transaction = ___sqliteConn.BeginTransaction();
        _write(pieces, db);
        transaction.Commit();
        return false;
    }

    private static void Write<T>(object pieces, sqlite3 db) where T : class
    {
        SqliteException.ThrowExceptionForRC(raw.sqlite3_prepare_v2(db, Insert, out var statement), db);
        var rows = (Dictionary<FastVec2i, T>)pieces;
        _ = Assert(rows.Count <= MaxPieces);
        using var each = rows.GetEnumerator();
        using (statement)
            for (var i = 0; i < MaxPieces && each.MoveNext(); i++)
            {
                var (key, piece) = each.Current;
                var pixels = MapPixels.Pixels<T>.Of(piece);
                var length = Encode(pixels, ref _buffer);
                MapPixels.Release(pixels); // encoded: the save is done with it
                SqliteException.ThrowExceptionForRC(raw.sqlite3_bind_int64(statement, 1, (long)key.ToChunkIndex()), db);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_bind_blob(statement, 2, _buffer.AsSpan(0, length)), db);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_step(statement), db);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_reset(statement), db);
            }
    }

    // MapPieceDB as protobuf-net writes it: nothing for no pixels, else each pixel tagged, a sign-extended varint
    internal static int Encode(int[]? pixels, ref byte[]? buffer)
    {
        if (pixels is null || !Assert(pixels.Length <= MaxPixels)) return 0;
        var need = pixels.Length * (1 + MaxVarint);
        if (buffer is null || buffer.Length < need) buffer = new byte[need];
        var at = 0;
        for (var i = 0; i < Math.Min(pixels.Length, MaxPixels); i++)
        {
            buffer[at++] = Tag;
            var value = (ulong)(long)pixels[i];
            for (var b = 1; b < MaxVarint && value >= 0x80; b++, value >>= 7) buffer[at++] = (byte)(value | 0x80);
            buffer[at++] = (byte)value;
        }

        return at;
    }
}
