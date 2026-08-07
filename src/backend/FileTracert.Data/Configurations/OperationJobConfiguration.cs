using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class OperationJobConfiguration : IEntityTypeConfiguration<OperationJob>
{
    public void Configure(EntityTypeBuilder<OperationJob> builder)
    {
        builder.ToTable("OperationJobs");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Type).HasConversion<string>();
        // Concurrency token (finding #2): every UPDATE of a job row carries
        // WHERE State = <original>, so a state transition committed by another
        // DbContext (user Cancel from the API) can never be blindly overwritten
        // by the engine — the stale write throws DbUpdateConcurrencyException.
        builder.Property(x => x.State).HasConversion<string>().IsConcurrencyToken();
        builder.Property(x => x.BlockReason).HasConversion<string>();

        builder.HasOne(x => x.SourceVolume)
            .WithMany()
            .HasForeignKey(x => x.SourceVolumeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.TargetVolume)
            .WithMany()
            .HasForeignKey(x => x.TargetVolumeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.DependsOnJob)
            .WithMany()
            .HasForeignKey(x => x.DependsOnJobId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Items)
            .WithOne(x => x.Job)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.LedgerEntries)
            .WithOne(x => x.Job)
            .HasForeignKey(x => x.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.SequenceOrder);
    }
}
