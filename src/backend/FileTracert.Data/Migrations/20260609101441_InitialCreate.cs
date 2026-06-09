using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace FileTracert.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultExtensionFilter = table.Column<string>(type: "TEXT", nullable: false),
                    ExcludedPaths = table.Column<string>(type: "TEXT", nullable: false),
                    ApiToken = table.Column<string>(type: "TEXT", nullable: false),
                    SpaceMarginPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExtensionCategories",
                columns: table => new
                {
                    Extension = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtensionCategories", x => x.Extension);
                });

            migrationBuilder.CreateTable(
                name: "Volumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolumeGuid = table.Column<string>(type: "TEXT", nullable: false),
                    SerialNumber = table.Column<string>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", nullable: true),
                    FileSystem = table.Column<string>(type: "TEXT", nullable: false),
                    IsRemovable = table.Column<bool>(type: "INTEGER", nullable: false),
                    PhysicalDiskId = table.Column<string>(type: "TEXT", nullable: true),
                    LastDriveLetter = table.Column<string>(type: "TEXT", nullable: true),
                    CapacityBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FreeBytesLastKnown = table.Column<long>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsOnline = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScanEngine = table.Column<string>(type: "TEXT", nullable: false),
                    LastUsn = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFullScanUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Volumes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Directories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MaterializedPath = table.Column<string>(type: "TEXT", nullable: false),
                    UsnFileRef = table.Column<long>(type: "INTEGER", nullable: true),
                    IsMaterialized = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingName = table.Column<string>(type: "TEXT", nullable: true),
                    PendingParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingState = table.Column<string>(type: "TEXT", nullable: false),
                    PendingJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Directories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Directories_Directories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Directories_Directories_PendingParentId",
                        column: x => x.PendingParentId,
                        principalTable: "Directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Directories_Volumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationJobs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Type = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    BlockReason = table.Column<string>(type: "TEXT", nullable: false),
                    SourceVolumeId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetVolumeId = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetRelativePath = table.Column<string>(type: "TEXT", nullable: true),
                    IsIntraVolume = table.Column<bool>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesProcessed = table.Column<long>(type: "INTEGER", nullable: false),
                    RequiredBytesTarget = table.Column<long>(type: "INTEGER", nullable: false),
                    FreedBytesSource = table.Column<long>(type: "INTEGER", nullable: false),
                    EstimateIsLive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SequenceOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DependsOnJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationJobs_OperationJobs_DependsOnJobId",
                        column: x => x.DependsOnJobId,
                        principalTable: "OperationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationJobs_Volumes_SourceVolumeId",
                        column: x => x.SourceVolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperationJobs_Volumes_TargetVolumeId",
                        column: x => x.TargetVolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WatchedRoots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    FilterOverrideJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchedRoots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchedRoots_Volumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Files",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Extension = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ModifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attributes = table.Column<int>(type: "INTEGER", nullable: false),
                    UsnFileRef = table.Column<long>(type: "INTEGER", nullable: true),
                    QuickHash = table.Column<string>(type: "TEXT", nullable: true),
                    Hash = table.Column<string>(type: "TEXT", nullable: true),
                    IsIncluded = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPresent = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastIndexedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PendingName = table.Column<string>(type: "TEXT", nullable: true),
                    PendingDirectoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    PendingState = table.Column<string>(type: "TEXT", nullable: false),
                    PendingJobId = table.Column<int>(type: "INTEGER", nullable: true),
                    RowCreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RowUpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Files_Directories_DirectoryId",
                        column: x => x.DirectoryId,
                        principalTable: "Directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Files_Directories_PendingDirectoryId",
                        column: x => x.PendingDirectoryId,
                        principalTable: "Directories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Files_Volumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperationJobItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    TargetRelativePath = table.Column<string>(type: "TEXT", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    TempPath = table.Column<string>(type: "TEXT", nullable: true),
                    BytesCopied = table.Column<long>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperationJobItems_OperationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "OperationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpaceLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    JobId = table.Column<int>(type: "INTEGER", nullable: false),
                    VolumeId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeltaBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpaceLedgerEntries_OperationJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "OperationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SpaceLedgerEntries_Volumes_VolumeId",
                        column: x => x.VolumeId,
                        principalTable: "Volumes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "ExtensionCategories",
                columns: new[] { "Extension", "Category" },
                values: new object[,]
                {
                    { "3gp", "Video" },
                    { "7z", "Archive" },
                    { "aac", "Audio" },
                    { "aiff", "Audio" },
                    { "alac", "Audio" },
                    { "arw", "Image" },
                    { "avi", "Video" },
                    { "bmp", "Image" },
                    { "bz2", "Archive" },
                    { "cr2", "Image" },
                    { "cr3", "Image" },
                    { "csv", "Document" },
                    { "dng", "Image" },
                    { "doc", "Document" },
                    { "docx", "Document" },
                    { "epub", "Document" },
                    { "flac", "Audio" },
                    { "flv", "Video" },
                    { "gif", "Image" },
                    { "gz", "Archive" },
                    { "heic", "Image" },
                    { "heif", "Image" },
                    { "iso", "Archive" },
                    { "jpeg", "Image" },
                    { "jpg", "Image" },
                    { "m2ts", "Video" },
                    { "m4a", "Audio" },
                    { "m4v", "Video" },
                    { "md", "Document" },
                    { "mkv", "Video" },
                    { "mov", "Video" },
                    { "mp3", "Audio" },
                    { "mp4", "Video" },
                    { "mpeg", "Video" },
                    { "mpg", "Video" },
                    { "mts", "Video" },
                    { "nef", "Image" },
                    { "odp", "Document" },
                    { "ods", "Document" },
                    { "odt", "Document" },
                    { "ogg", "Audio" },
                    { "opus", "Audio" },
                    { "orf", "Image" },
                    { "pdf", "Document" },
                    { "png", "Image" },
                    { "ppt", "Document" },
                    { "pptx", "Document" },
                    { "raf", "Image" },
                    { "rar", "Archive" },
                    { "raw", "Image" },
                    { "rtf", "Document" },
                    { "svg", "Image" },
                    { "tar", "Archive" },
                    { "tif", "Image" },
                    { "tiff", "Image" },
                    { "txt", "Document" },
                    { "wav", "Audio" },
                    { "webm", "Video" },
                    { "webp", "Image" },
                    { "wma", "Audio" },
                    { "wmv", "Video" },
                    { "xls", "Document" },
                    { "xlsx", "Document" },
                    { "xz", "Archive" },
                    { "zip", "Archive" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Directories_ParentId",
                table: "Directories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Directories_PendingParentId",
                table: "Directories",
                column: "PendingParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Directories_VolumeId_ParentId",
                table: "Directories",
                columns: new[] { "VolumeId", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_Category",
                table: "Files",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Files_DirectoryId",
                table: "Files",
                column: "DirectoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_Extension",
                table: "Files",
                column: "Extension");

            migrationBuilder.CreateIndex(
                name: "IX_Files_ModifiedUtc",
                table: "Files",
                column: "ModifiedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Files_PendingDirectoryId",
                table: "Files",
                column: "PendingDirectoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Files_SizeBytes",
                table: "Files",
                column: "SizeBytes");

            migrationBuilder.CreateIndex(
                name: "IX_Files_VolumeId_DirectoryId",
                table: "Files",
                columns: new[] { "VolumeId", "DirectoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Files_VolumeId_UsnFileRef",
                table: "Files",
                columns: new[] { "VolumeId", "UsnFileRef" },
                unique: true,
                filter: "[UsnFileRef] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobItems_JobId",
                table: "OperationJobItems",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_DependsOnJobId",
                table: "OperationJobs",
                column: "DependsOnJobId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_SequenceOrder",
                table: "OperationJobs",
                column: "SequenceOrder");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_SourceVolumeId",
                table: "OperationJobs",
                column: "SourceVolumeId");

            migrationBuilder.CreateIndex(
                name: "IX_OperationJobs_TargetVolumeId",
                table: "OperationJobs",
                column: "TargetVolumeId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceLedgerEntries_JobId",
                table: "SpaceLedgerEntries",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceLedgerEntries_VolumeId_IsActive",
                table: "SpaceLedgerEntries",
                columns: new[] { "VolumeId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Volumes_VolumeGuid",
                table: "Volumes",
                column: "VolumeGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchedRoots_VolumeId",
                table: "WatchedRoots",
                column: "VolumeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "ExtensionCategories");

            migrationBuilder.DropTable(
                name: "Files");

            migrationBuilder.DropTable(
                name: "OperationJobItems");

            migrationBuilder.DropTable(
                name: "SpaceLedgerEntries");

            migrationBuilder.DropTable(
                name: "WatchedRoots");

            migrationBuilder.DropTable(
                name: "Directories");

            migrationBuilder.DropTable(
                name: "OperationJobs");

            migrationBuilder.DropTable(
                name: "Volumes");
        }
    }
}
