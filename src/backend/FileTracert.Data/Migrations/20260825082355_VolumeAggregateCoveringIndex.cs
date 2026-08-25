using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class VolumeAggregateCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Files_VolumeId_IsIncluded_IsPresent_SizeBytes",
                table: "Files",
                columns: new[] { "VolumeId", "IsIncluded", "IsPresent", "SizeBytes" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Files_VolumeId_IsIncluded_IsPresent_SizeBytes",
                table: "Files");
        }
    }
}
