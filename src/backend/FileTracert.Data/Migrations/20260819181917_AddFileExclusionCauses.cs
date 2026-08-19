using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <summary>
    /// Step 11h — a row remembers WHY it is excluded, so reconciliation can undo only what it is
    /// entitled to undo. Three flags rather than one value, because the causes genuinely combine
    /// (a <c>.tmp</c> inside a hidden folder is excluded twice over) and each has to be cleared by
    /// its own owner; with a single value, "undo the type filter" would have to guess whether the
    /// row is also outside the perimeter.
    ///
    /// <para><b>The backfill.</b> Existing rows carry <c>IsIncluded = 0</c> and no memory of why.
    /// They are stamped <c>ExcludedByScan</c> — the one cause reconciliation never undoes — so that
    /// nothing silently walks back into the Catalog on the next filter change. It is deliberately
    /// the pessimistic answer: a row that was in fact only type-filtered stays out one scan longer
    /// than it needs to, and the first scan that sees the file again clears the flag (the merge
    /// writes all three to 0). The opposite mistake — assuming "type" and re-including the content
    /// of a hidden folder — is the very defect this step closes, and it is not visible to the user
    /// as a mistake.</para>
    /// </summary>
    public partial class AddFileExclusionCauses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludedByRoot",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExcludedByScan",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ExcludedByType",
                table: "Files",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // One set-based statement: the table holds millions of rows on a system volume.
            migrationBuilder.Sql("UPDATE Files SET ExcludedByScan = 1 WHERE IsIncluded = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExcludedByRoot",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ExcludedByScan",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ExcludedByType",
                table: "Files");
        }
    }
}
