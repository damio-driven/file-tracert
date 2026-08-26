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

        // 15a — a projected Copy destination row is the only FileEntry that does not stand for a
        // file on disk, so every row that predates the column, and every row any other writer
        // inserts without naming it, must read as materialized.
        builder.Property(x => x.IsMaterialized).HasDefaultValue(true);

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
        // discriminator the first branch tests for NULL, then the flags.
        //
        // 15a appends IsMaterialized, and it is the covering property that pays for it. The
        // Catalog now has to show a destination row a queued Copy projected — §5 — so its
        // predicate gains a third term. Measured on 20 000 rows, the hot branch of the counter:
        //
        //   no disjunct (the 11e baseline)       SEARCH … USING COVERING INDEX …
        //   OR PendingState <> 'None'            SEARCH … USING INDEX …
        //   OR NOT IsMaterialized, not indexed   SEARCH … USING INDEX …
        //   OR NOT IsMaterialized, indexed here  SEARCH … USING COVERING INDEX …
        //
        // Losing COVERING is one table-row lookup per counted file, i.e. the ~300 000 per listing
        // this index exists to remove. PendingState — the obvious spelling — cannot be indexed
        // cheaply: it is a STRING, and refusing it is the decision 11e wrote down. IsMaterialized
        // is a boolean, appended last because nothing ever seeks on it.
        builder.HasIndex(x => new { x.DirectoryId, x.PendingDirectoryId, x.IsIncluded, x.IsPresent, x.IsMaterialized });
        builder.HasIndex(x => new { x.PendingDirectoryId, x.IsIncluded, x.IsPresent, x.IsMaterialized });

        // Kept as-is, and deliberately not touched: the scan merge resolves a staged row with
        // `VolumeId = ? AND DirectoryId = ? AND Name = ? ORDER BY Id LIMIT 1`, once per file in
        // every batch. Because Id is the rowid and this index stops at DirectoryId, the entries of
        // one directory are already in Id order, so that ORDER BY costs nothing and the LIMIT can
        // stop at the first name that matches.
        //
        // <see cref="ScanMergePlanTests"/> asserts exactly that, because it is the plan that must
        // NOT change and the failure mode is silent: a re-scan would simply get slower, with the
        // suite green.
        builder.HasIndex(x => new { x.VolumeId, x.DirectoryId });

        // 14c — the per-volume aggregate, covering. Both Volumes screens ask the same question of
        // this table: "how many included, still-present files does volume V hold" (the list, for
        // every volume at once) and "and how many bytes" (the detail, for one). On the real
        // 742 033-file catalog those cost 1 571 ms and 1 768 ms against 373 ms for a Dashboard that
        // aggregates the SAME table — the difference was never the data.
        //
        // What it was: the list scanned IX_Files_VolumeId_DirectoryId and then fetched the table
        // row of every file in the catalog to read two booleans — the shape step 11e found on the
        // Catalog counters one level down. SizeBytes is the last column so the detail's SUM never
        // leaves the index either; the two screens then differ only in whether VolumeId is a seek
        // or a scan.
        //
        // ADDITION, not a widening, and the honest reason is not "DirectoryId must stay second":
        // widening the index above to (VolumeId, DirectoryId, IsIncluded, IsPresent, SizeBytes)
        // keeps it second and wins both plans at no extra B-tree, which is the trade 11e made. What
        // it would also do is change the merge's per-staged-file lookup, on both counts that matter
        // there: Id stops being the next sort key inside a (VolumeId, DirectoryId) group, so the
        // ORDER BY needs a sorter, and every entry of the group carries three more columns to read
        // while scanning it for the name. That is the hottest path of a re-scan, traded for a
        // write-side cost that was measured and found invisible (the 14c paragraph in CLAUDE.md has
        // the harness A/B). The alternative is written down rather than hidden, so a later hand with
        // a measurement of that path can take it.
        //
        // The write side is more than inserts: the merge's UPDATE of a matched row sets DirectoryId,
        // SizeBytes, IsIncluded and IsPresent, and FilterReconciler sets IsIncluded across a whole
        // volume — every one of those rewrites this index entry too, inside the transaction that
        // holds SQLite's single writer lock.
        builder.HasIndex(x => new { x.VolumeId, x.IsIncluded, x.IsPresent, x.SizeBytes });
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
