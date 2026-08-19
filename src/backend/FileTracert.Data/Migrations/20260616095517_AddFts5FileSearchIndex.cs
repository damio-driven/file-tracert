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
            // The SQL is byte-identical to what this migration shipped with.
            migrationBuilder.Sql(FileSearchIndexSchema.CreateTableSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(FileSearchIndexSchema.DropTableSql);
        }
    }
}
