using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class WatchedRootConfiguration : IEntityTypeConfiguration<WatchedRoot>
{
    public void Configure(EntityTypeBuilder<WatchedRoot> builder)
    {
        builder.ToTable("WatchedRoots");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.RelativePath).IsRequired();

        // FK relationship configured on the Volume side (Restrict).
    }
}
