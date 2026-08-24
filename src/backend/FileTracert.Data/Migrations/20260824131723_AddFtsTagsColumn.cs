using FileTracert.Data.Search;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <summary>
    /// Step 14a — the FTS5 table gains its third column, <c>tags</c>, so that a category or volume
    /// filter is answered by the index instead of by resolving every match on <c>Files</c>.
    ///
    /// <para>An FTS5 virtual table has no <c>ALTER TABLE … ADD COLUMN</c>: the column list is part
    /// of its shadow-table layout, so the only way to add one is to drop the table and create it
    /// again. The rows are NOT rewritten here. The index is left empty and the startup backfill
    /// that already exists (<c>DatabaseInitializer.BackfillFtsIfNeededAsync</c> — "there are files
    /// but no entries, rebuild") fills it from <c>Files</c>. That is deliberate: repopulating here
    /// would mean a second copy of the projected-name and tag SQL inside a migration, and §5 is
    /// explicit that those rules have exactly one definition
    /// (<c>FileSearchIndex.InsertProjectedSql</c>). Migrations run before the backfill, so the
    /// window in which the index is empty never reaches a request.</para>
    ///
    /// <para><b>What it costs the user, once.</b> Measured on the real catalog (742 033 indexed
    /// files, 731 MB database): the rebuild takes <b>~10 s</b> at the first startup after the
    /// update, and nothing after that — the emptiness probe stops at the first row. The search is
    /// unavailable for that one startup, not the catalog.</para>
    ///
    /// <para>The DDL itself is <see cref="FileSearchIndexSchema"/>, shared with the original
    /// migration and with the tests, so the tokenizer cannot drift between them.</para>
    /// </summary>
    public partial class AddFtsTagsColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DROP first: on a database created before this migration the table exists with two
            // columns, and CreateTableSql is IF NOT EXISTS — without the drop it would keep the
            // old shape and every tag filter would silently match nothing.
            migrationBuilder.Sql(FileSearchIndexSchema.DropTableSql);
            migrationBuilder.Sql(FileSearchIndexSchema.CreateTableSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Symmetric: back to an empty index, which the older build's own backfill refills.
            migrationBuilder.Sql(FileSearchIndexSchema.DropTableSql);
            migrationBuilder.Sql(TwoColumnCreateTableSql);
        }

        /// <summary>
        /// The shape this migration replaces, spelled out rather than referenced: rolling back has
        /// to produce the table the PREVIOUS build expects, and that is a fact of the past, not
        /// whatever <see cref="FileSearchIndexSchema"/> happens to say today.
        /// </summary>
        private const string TwoColumnCreateTableSql = """
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
                name,
                path,
                tokenize="unicode61 remove_diacritics 2 separators '\._-'"
            );
            """;
    }
}
