using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <summary>
    /// Step 18: one EFFECTIVE exclusion cause on <c>Directories</c> — this folder, or an ancestor,
    /// is Hidden/System as the last scan or USN delta saw it — so the delta can inherit an
    /// exclusion the record itself does not carry. The only cause a directory needs: a path
    /// segment is re-derived from the item's own path, an inactive root from the settings.
    /// <para><b>No backfill, and that is the backfill.</b> A folder indexed and then hidden has
    /// a row the catalog cannot tell apart from a visible one — the fact lives on the disk. The
    /// next full scan writes the column for every catalog directory of the volume it walks
    /// (<c>ScanSkipAreas</c>); until then the delta behaves as before on those rows, which is
    /// the visible, reversible error rather than the silent one.</para>
    /// </summary>
    public partial class AddDirectoryExcludedByScan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludedByScan",
                table: "Directories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludedByScan",
                table: "Directories");
        }
    }
}
