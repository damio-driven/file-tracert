using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class DirectoryNodeConfiguration : IEntityTypeConfiguration<DirectoryNode>
{
    public void Configure(EntityTypeBuilder<DirectoryNode> builder)
    {
        builder.ToTable("Directories");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired();
        // P2 — ONE collation for the path. Every in-memory cache and predicate that touches
        // MaterializedPath is OrdinalIgnoreCase (Windows paths are case-insensitive), while SQL
        // equality used the column's default BINARY collation. The two disagreed on a case
        // variant, and the find-or-create walk, finding nothing, inserted a second row for a
        // folder that was already there. Fixed at the column so every caller inherits it —
        // including the ones nobody audits.
        //
        // Known limit, same one recorded for the scan merge at step 9a: SQLite's NOCASE folds
        // ASCII only. A path whose only difference is the case of a NON-ASCII character still
        // compares as two different paths. That costs an extra row, never a lost one.
        builder.Property(x => x.MaterializedPath).IsRequired().UseCollation("NOCASE");
        builder.Property(x => x.PendingState).HasConversion<string>();

        // Volume FK configured on the Volume side (Restrict).

        // Self-referencing parent (Restrict).
        builder.HasOne(x => x.Parent)
            .WithMany(x => x.Children)
            .HasForeignKey(x => x.ParentId)
            .OnDelete(DeleteBehavior.Restrict);

        // Pending parent (nullable self-ref, Restrict, no navigation).
        builder.HasOne<DirectoryNode>()
            .WithMany()
            .HasForeignKey(x => x.PendingParentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Files)
            .WithOne(x => x.Directory)
            .HasForeignKey(x => x.DirectoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.VolumeId, x.ParentId });

        // Subtree queries (move-folder expansion, overlap guard, source-subtree delete) filter on
        // MaterializedPath with prefix/StartsWith predicates — index it so those are seeks, not scans (C4).
        builder.HasIndex(x => x.MaterializedPath);
    }
}
