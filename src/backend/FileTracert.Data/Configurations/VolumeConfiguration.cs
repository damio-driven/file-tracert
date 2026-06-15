using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class VolumeConfiguration : IEntityTypeConfiguration<Volume>
{
    public void Configure(EntityTypeBuilder<Volume> builder)
    {
        builder.ToTable("Volumes");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.VolumeGuid).IsRequired();
        builder.Property(x => x.FileSystem).IsRequired();
        builder.Property(x => x.ScanEngine).HasConversion<string>();
        builder.Property(x => x.Kind).HasConversion<string>();
        builder.Property(x => x.IsCatalogable).HasDefaultValue(true);

        builder.HasIndex(x => x.VolumeGuid).IsUnique();
        builder.HasIndex(x => x.IsCatalogable);

        builder.HasMany(x => x.WatchedRoots)
            .WithOne(x => x.Volume)
            .HasForeignKey(x => x.VolumeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Directories)
            .WithOne(x => x.Volume)
            .HasForeignKey(x => x.VolumeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(x => x.Files)
            .WithOne(x => x.Volume)
            .HasForeignKey(x => x.VolumeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
