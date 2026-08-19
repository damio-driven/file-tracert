namespace FileTracert.Data.Search;

/// <summary>
/// The DDL of the FTS5 virtual table, in one place.
///
/// <para>It is not an ordinary table: EF does not model it, so <c>EnsureCreated</c> does not
/// build it and the migration writes the SQL by hand. That meant every integration test that
/// needs a working index pasted the same <c>CREATE VIRTUAL TABLE</c> — seven copies at the time
/// this was extracted — and each one is a chance for a test suite to keep passing against a
/// tokenizer the product no longer uses. The tokenizer is the point: <c>remove_diacritics 2</c>
/// and the <c>. _ -</c> separators decide what a search for "foto" matches, and a test that
/// disagrees with the migration proves nothing about the product.</para>
/// </summary>
public static class FileSearchIndexSchema
{
    public const string TableName = "FileSearchIndex";

    /// <summary>
    /// <c>IF NOT EXISTS</c> so it is safe on a database that already has it — the migration is
    /// re-run on every startup and a test harness may create it more than once.
    /// </summary>
    public const string CreateTableSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
            name,
            path,
            tokenize="unicode61 remove_diacritics 2 separators '\._-'"
        );
        """;

    public const string DropTableSql = "DROP TABLE IF EXISTS FileSearchIndex;";
}
