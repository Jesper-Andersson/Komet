using Komet.Core;
using Komet.World;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// MapDB.SetMapPieces writes the rows the engine writes: the same keys and, byte for byte, the blobs protobuf-net makes of MapPieceDB,
// which the engine's GetMapPiece reads back
[NonParallelizable]
public sealed class MapSaveScratchTests
{
    private string? _folder;

    [TearDown]
    public void Restore()
    {
        MapSaveScratch.Enabled = true;
        SqliteConnection.ClearAllPools();
        if (_folder is not null) Directory.Delete(_folder, true);
        _folder = null;
    }

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-mapsavescratch");
        MapSaveScratch.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(MapSaveScratch.Seams()[0], Is.Not.Null, "the essentials mod's map database");
            Assert.That(MapSaveScratch.Matched, Is.True,
                $"MapDB.SetMapPieces is not the body verified: 0x{EngineShape.Of(MapSaveScratch.Seams()):X16}UL");
            Assert.That(MapSaveScratch.Blocked, Is.False);
            Assert.That(Harmony.GetPatchInfo(MapSaveScratch.Seams()[0])?.Prefixes.Select(p => p.owner),
                Is.EqualTo([harmony.Id]));
        });
    }

    private static IEnumerable<int[]?> Pieces()
    {
        var random = new Random(7);
        yield return null;
        yield return [];
        yield return [0, 1, -1, 127, 128, 16383, 16384, int.MaxValue, int.MinValue];
        yield return [.. Enumerable.Range(0, 1024).Select(_ => random.Next(int.MinValue, int.MaxValue))];
        yield return [.. Enumerable.Range(0, 1024).Select(i => unchecked((int)0xFF000000) | i * 4099)];
    }

    [TestCaseSource(nameof(Pieces))]
    public void EncodesAsProtobufNet(int[]? pixels)
    {
        byte[]? buffer = null;
        var length = MapSaveScratch.Encode(pixels, ref buffer);
        Assert.That(buffer is null ? [] : buffer[..length],
            Is.EqualTo(SerializerUtil.Serialize(new MapPieceDB { Pixels = pixels! })));
    }

    [Test]
    public void WritesTheRowsTheEngineWrites()
    {
        var pieces = new Dictionary<FastVec2i, MapPieceDB>
        {
            [new FastVec2i(3, -2)] = new() { Pixels = [.. Enumerable.Range(0, 1024).Select(i => -i * 77)] },
            [new FastVec2i(-40, 9)] = new() { Pixels = [.. Enumerable.Range(0, 1024).Select(i => i)] }
        };
        var (engine, komet) = (Rows(pieces, false), Rows(pieces, true));
        Assert.Multiple(() =>
        {
            Assert.That(komet.Rows, Is.EqualTo(engine.Rows));
            foreach (var (key, piece) in pieces)
                Assert.That(komet.Read[key], Is.EqualTo(piece.Pixels), $"piece {key.X}/{key.Y} read back by the engine");
        });
    }

    // The table after SetMapPieces, the patch on or off, and each piece as the engine's GetMapPiece reads it
    private (List<(long, byte[])> Rows, Dictionary<FastVec2i, int[]> Read) Rows(Dictionary<FastVec2i, MapPieceDB> pieces,
        bool patched)
    {
        _folder ??= Directory.CreateTempSubdirectory("komet-mapdb").FullName;
        using var harmony = new TestHarmony("komet-test-mapsavescratch");
        MapSaveScratch.Install(harmony, new QuietLogger());
        MapSaveScratch.Enabled = patched;
        var db = new MapDB(new QuietLogger());
        var error = "";
        Assert.That(db.OpenOrCreate(Path.Combine(_folder, $"map-{patched}.db"), ref error, true, false, false), Is.True,
            error);
        try
        {
            db.SetMapPieces(pieces);
            var read = pieces.Keys.ToDictionary(k => k, k => db.GetMapPiece(k).Pixels);
            var connection = (SqliteConnection)AccessTools.Field(typeof(SQLiteDBConnection), "sqliteConn").GetValue(db)!;
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT position, data FROM mappiece ORDER BY position";
            using var reader = select.ExecuteReader();
            var rows = new List<(long, byte[])>();
            while (reader.Read()) rows.Add((reader.GetInt64(0), (byte[])reader["data"]));
            return (rows, read);
        }
        finally
        {
            db.Dispose();
        }
    }
}
