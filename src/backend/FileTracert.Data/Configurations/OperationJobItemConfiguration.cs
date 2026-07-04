using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class OperationJobItemConfiguration : IEntityTypeConfiguration<OperationJobItem>
{
    public void Configure(EntityTypeBuilder<OperationJobItem> builder)
    {
        builder.ToTable("OperationJobItems");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceRelativePath).IsRequired();
        builder.Property(x => x.TargetRelativePath).IsRequired();
        builder.Property(x => x.State).HasConversion<string>();

        // Job FK relationship configured on the OperationJob side (Cascade).
        builder.HasIndex(x => x.JobId);

        // Every enqueue's per-file guard filters OperationJobItems by FileId on a table that grows
        // without bound; without this index that is a full scan (C4).
        builder.HasIndex(x => x.FileId);
    }
}
