using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class SpaceLedgerEntryConfiguration : IEntityTypeConfiguration<SpaceLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SpaceLedgerEntry> builder)
    {
        builder.ToTable("SpaceLedgerEntries");
        builder.HasKey(x => x.Id);

        // Job FK relationship configured on the OperationJob side (Cascade).

        builder.HasOne(x => x.Volume)
            .WithMany()
            .HasForeignKey(x => x.VolumeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.VolumeId, x.IsActive });
    }
}
