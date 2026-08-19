using FileTracert.Data;
using FileTracert.Data.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// The FTS5 table, for tests that need a working search index.
///
/// <para><c>EnsureCreated</c> builds the EF tables and not the virtual one, so every such test
/// had to run the <c>CREATE VIRTUAL TABLE</c> itself — seven pasted copies of the migration's
/// DDL, none of which would notice if the migration changed. The tokenizer is exactly what a
/// search test is asserting about (<c>remove_diacritics 2</c>, the <c>. _ -</c> separators), so a
/// copy that drifts is a suite that keeps passing against an index the product does not build.
/// The statement now comes from <see cref="FileSearchIndexSchema"/>, which is also what the
/// migration runs.</para>
/// </summary>
internal static class SqliteFts
{
    /// <summary>Creates the virtual table on this context's connection. Idempotent.</summary>
    public static void Create(FileTracertDbContext db) =>
        db.Database.ExecuteSqlRaw(FileSearchIndexSchema.CreateTableSql);

    /// <summary>Every row of the real FTS table, as (rowid, name, path), ordered by rowid.</summary>
    public static List<(int Rowid, string Name, string Path)> Rows(SqliteInMemoryContext harness)
    {
        using var db = harness.CreateContext();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        db.Database.OpenConnection();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT rowid, name, path FROM {FileSearchIndexSchema.TableName} ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            var rows = new List<(int, string, string)>();
            while (reader.Read())
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }
        finally { db.Database.CloseConnection(); }
    }
}
