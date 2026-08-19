using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// P2 — one collation for Directories.MaterializedPath. SQL equality used the column's
    /// default BINARY collation while every in-memory rule is OrdinalIgnoreCase, so a case
    /// variant produced a duplicate DirectoryNode instead of reusing the existing row.
    ///
    /// SQLite cannot alter a column in place: the provider rebuilds the table and recreates its
    /// indexes from the model, so the index on MaterializedPath comes back with the new
    /// collation (an index keeps the collation it was built with, which is exactly why it has
    /// to be rebuilt rather than left alone).
    ///
    /// Rows a database already accumulated are NOT merged here: two case-variant rows for one
    /// folder stay two rows and the resolver simply picks one from now on. Merging them would
    /// mean re-pointing files, jobs and pending overlays — a data migration, not a schema one.
    /// </summary>
    public partial class MaterializedPathNoCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "MaterializedPath",
                table: "Directories",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "MaterializedPath",
                table: "Directories",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");
        }
    }
}
