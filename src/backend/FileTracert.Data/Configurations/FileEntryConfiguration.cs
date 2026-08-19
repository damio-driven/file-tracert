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

        // E5 — the catalog's per-directory counters, made COVERING.
        //
        // These two replace the FK indexes EF would create on its own (`DirectoryId` and
        // `PendingDirectoryId`): each starts with the foreign key, so EF's convention leaves the
        // narrow one out and the table keeps exactly as many indexes as before. What changes is
        // that the flags the counters filter on now travel WITH the key.
        //
        // Why it matters: the Catalog counts files per sub-directory with the projected predicate
        // `(DirectoryId = d AND PendingDirectoryId IS NULL) OR PendingDirectoryId = d`, and then
        // `IsIncluded AND IsPresent`. SQLite already answered the first half from the index — but
        // had to fetch the table row of EVERY counted file just to read two booleans. Listing a
        // folder of 500 sub-directories holding 600 files each meant ~300 000 row lookups for a
        // pair of numbers on a badge. With the flags in the index the count never leaves it
        // (`SEARCH … USING COVERING INDEX`), and the lookups drop to zero.
        //
        // Column order is the predicate's order, not a guess: the equality key first, then the
        // discriminator the first branch tests for NULL, then the two flags.
        builder.HasIndex(x => new { x.DirectoryId, x.PendingDirectoryId, x.IsIncluded, x.IsPresent });
        builder.HasIndex(x => new { x.PendingDirectoryId, x.IsIncluded, x.IsPresent });

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
