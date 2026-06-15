using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVolumeKindAndCatalogable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCatalogable",
                table: "Volumes",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "Volumes",
                type: "TEXT",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateIndex(
                name: "IX_Volumes_IsCatalogable",
                table: "Volumes",
                column: "IsCatalogable");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Volumes_IsCatalogable",
                table: "Volumes");

            migrationBuilder.DropColumn(
                name: "IsCatalogable",
                table: "Volumes");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Volumes");
        }
    }
}
