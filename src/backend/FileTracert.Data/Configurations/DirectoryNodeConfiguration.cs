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
        builder.Property(x => x.MaterializedPath).IsRequired();
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
    }
}
