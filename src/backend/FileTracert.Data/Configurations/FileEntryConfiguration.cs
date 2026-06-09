using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class FileEntryConfiguration : IEntityTypeConfiguration<FileEntry>
{
    public void Configure(EntityTypeBuilder<FileEntry> builder)
    {
        builder.ToTable("Files");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired();
        builder.Property(x => x.Extension).IsRequired();
        builder.Property(x => x.Category).HasConversion<string>();
        builder.Property(x => x.PendingState).HasConversion<string>();

        // FileAttributes is a [Flags] enum → store as int.
        builder.Property(x => x.Attributes).HasConversion<int>();

        // The file's own timestamps map to DB columns CreatedUtc / ModifiedUtc.
        builder.Property(x => x.FileCreatedUtc).HasColumnName("CreatedUtc");
        builder.Property(x => x.FileModifiedUtc).HasColumnName("ModifiedUtc");

        // The IAuditable row-audit timestamps map to RowCreatedUtc / RowUpdatedUtc.
        builder.Property(x => x.CreatedUtc).HasColumnName("RowCreatedUtc");
        builder.Property(x => x.UpdatedUtc).HasColumnName("RowUpdatedUtc");

        // Volume / Directory FK relationships configured on the principal side (Restrict).

        // Pending directory (nullable, Restrict, no navigation).
        builder.HasOne<DirectoryNode>()
            .WithMany()
            .HasForeignKey(x => x.PendingDirectoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.VolumeId, x.DirectoryId });
        builder.HasIndex(x => x.Extension);
        builder.HasIndex(x => x.Category);
        builder.HasIndex(x => x.SizeBytes);
        builder.HasIndex(x => x.FileModifiedUtc);

        // Unique USN file reference per volume, filtered to non-null values.
        builder.HasIndex(x => new { x.VolumeId, x.UsnFileRef })
            .IsUnique()
            .HasFilter("[UsnFileRef] IS NOT NULL");
    }
}
