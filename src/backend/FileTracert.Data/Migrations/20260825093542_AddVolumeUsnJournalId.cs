using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumeUsnJournalId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "UsnJournalId",
                table: "Volumes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UsnJournalId",
                table: "Volumes");
        }
    }
}
