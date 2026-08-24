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
///
/// <para>Step 14a — the third column, <c>tags</c>, holds no words. It holds the synthetic tokens
/// of <see cref="FileSearchTags"/>, so a category or volume filter is answered by intersecting
/// two doclists inside the index instead of resolving every match on <c>Files</c> and throwing
/// most of them away. It is never full-text searched: every user MATCH is column-scoped to
/// <c>{name path}</c>, and bm25 gives it weight 0 so it cannot move the relevance order.</para>
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
            tags,
            tokenize="unicode61 remove_diacritics 2 separators '\._-'"
        );
        """;

    public const string DropTableSql = "DROP TABLE IF EXISTS FileSearchIndex;";
}
