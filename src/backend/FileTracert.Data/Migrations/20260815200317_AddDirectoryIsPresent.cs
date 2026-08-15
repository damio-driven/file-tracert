using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryIsPresent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPresent",
                table: "Directories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Backfill: every directory already in the catalog was seen on disk by the
            // scan that created it. The column default stays 0 (CLR default, so the model
            // snapshot and the schema agree) — the CLR property defaults to true and the
            // scan merge is what flips rows to 0 from now on.
            migrationBuilder.Sql("UPDATE Directories SET IsPresent = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPresent",
                table: "Directories");
        }
    }
}
