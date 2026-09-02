using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <summary>
    /// Step 16 — the fourth exclusion cause. <c>ExcludedByScan</c> used to hold two facts of
    /// different natures: the attributes the scan read off the disk, and an excluded segment in the
    /// path. Only the first is unknowable outside a scan; the second is sitting on
    /// <c>Directories.MaterializedPath</c>, which is why <c>FilterReconciler</c> can decide it on
    /// its own — and why, while the two shared a column, adding a segment to <c>ExcludedPaths</c>
    /// excluded nothing that was already in the catalog. Step 11h separated the causes precisely so
    /// each could be cleared by its owner; these two were still fused.
    ///
    /// <para><b>No backfill, and that IS the backfill.</b> The column lands at 0 on every existing
    /// row, so a row excluded today by a path segment keeps carrying <c>ExcludedByScan</c> and
    /// stays out until a scan looks at it again. Deliberately the pessimistic answer, the same one
    /// step 11h chose: guessing "this one was only path-excluded" and re-admitting it silently is
    /// the invisible mistake, keeping a row out one scan longer is the visible and reversible one.
    /// The merge clears all four causes when it sees the file (§4).</para>
    /// </summary>
    public partial class AddFileExcludedByPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludedByPath",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludedByPath",
                table: "Files");
        }
    }
}
