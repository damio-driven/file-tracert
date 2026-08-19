using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class CatalogCountCoveringIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_DirectoryId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_PendingDirectoryId",
                table: "Files");

            migrationBuilder.CreateIndex(
                name: "IX_Files_DirectoryId_PendingDirectoryId_IsIncluded_IsPresent",
                table: "Files",
                columns: new[] { "DirectoryId", "PendingDirectoryId", "IsIncluded", "IsPresent" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_PendingDirectoryId_IsIncluded_IsPresent",
                table: "Files",
                columns: new[] { "PendingDirectoryId", "IsIncluded", "IsPresent" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_DirectoryId_PendingDirectoryId_IsIncluded_IsPresent",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Files_PendingDirectoryId_IsIncluded_IsPresent",
                table: "Files");

            migrationBuilder.CreateIndex(
                name: "IX_Files_DirectoryId",
                table: "Files",
                column: "DirectoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PendingDirectoryId",
                table: "Files",
                column: "PendingDirectoryId");
        }
    }
}
