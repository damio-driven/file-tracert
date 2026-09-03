using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <summary>
    /// Step 18: two EFFECTIVE exclusion causes on <c>Directories</c> (this folder or an
    /// ancestor), so the USN delta can inherit an exclusion the record itself does not carry.
    /// <para><b>No backfill, and that is the backfill.</b> A folder indexed and then hidden has
    /// a row the catalog cannot tell apart from a visible one — the fact lives on the disk. The
    /// next full scan writes both columns for every catalog directory of the volume it walks
    /// (<c>ScanSkipAreas</c>); until then the delta behaves as before on those rows, which is
    /// the visible, reversible error rather than the silent one. <c>ExcludedByPath</c> is
    /// derivable and the first Setup save or scan writes it too.</para>
    /// </summary>
    public partial class AddDirectoryExclusionCauses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludedByPath",
                table: "Directories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

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
                name: "ExcludedByPath",
                table: "Directories");

            migrationBuilder.DropColumn(
                name: "ExcludedByScan",
                table: "Directories");
        }
    }
}
