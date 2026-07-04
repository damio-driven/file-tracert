using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueuePerfIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_OperationJobItems_FileId",
                table: "OperationJobItems",
                column: "FileId");

            migrationBuilder.CreateIndex(
                name: "IX_Directories_MaterializedPath",
                table: "Directories",
                column: "MaterializedPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperationJobItems_FileId",
                table: "OperationJobItems");

            migrationBuilder.DropIndex(
                name: "IX_Directories_MaterializedPath",
                table: "Directories");
        }
    }
}
