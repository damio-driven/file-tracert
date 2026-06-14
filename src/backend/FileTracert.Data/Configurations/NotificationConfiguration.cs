using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Severity).HasConversion<string>();
        builder.Property(x => x.Source).IsRequired();
        builder.Property(x => x.Title).IsRequired();
        builder.Property(x => x.Message).IsRequired();

        // The bell shows newest-first, unread/undismissed first → index that access path.
        builder.HasIndex(x => new { x.IsDismissed, x.IsRead, x.TimestampUtc });
    }
}
