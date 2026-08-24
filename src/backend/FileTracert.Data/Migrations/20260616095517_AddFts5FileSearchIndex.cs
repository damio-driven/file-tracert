using FileTracert.Data.Search;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFts5FileSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The DDL itself lives in FileSearchIndexSchema so the tests that have to create this
            // table by hand (EnsureCreated does not build virtual tables) cannot drift from it.
            //
            // It is the CURRENT shape, not the one this migration shipped with: since 14a that is
            // the three-column table. Pointing here at today's DDL is safe precisely because
            // AddFtsTagsColumn drops and recreates it right afterwards — a fresh database gets the
            // right table one migration early, an existing one gets it from the later migration,
            // and both end up identical. What must not follow the constant is the DOWN of 14a,
            // which spells the two-column shape out by hand.
            migrationBuilder.Sql(FileSearchIndexSchema.CreateTableSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FileSearchIndexSchema.DropTableSql);
        }
    }
}
