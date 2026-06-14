using System.Text.Json;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class AppSettingsConfiguration : IEntityTypeConfiguration<AppSettings>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<AppSettings> builder)
    {
        builder.ToTable("AppSettings");
        builder.HasKey(x => x.Id);

        // Singleton: never auto-generate the key (always Id = 1).
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.ApiToken).IsRequired();

        // Defaults so the columns backfill cleanly on an existing singleton row.
        builder.Property(x => x.MinimumLogLevel).IsRequired().HasDefaultValue("Information");
        builder.Property(x => x.LogRetentionDays).HasDefaultValue(14);
        builder.Property(x => x.LogMaxRows).HasDefaultValue(500_000);

        ConfigureStringList(builder, x => x.DefaultExtensionFilter, nameof(AppSettings.DefaultExtensionFilter));
        ConfigureStringList(builder, x => x.ExcludedPaths, nameof(AppSettings.ExcludedPaths));
    }

    private static void ConfigureStringList(
        EntityTypeBuilder<AppSettings> builder,
        System.Linq.Expressions.Expression<Func<AppSettings, List<string>>> selector,
        string columnName)
    {
        var comparer = new ValueComparer<List<string>>(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
            v => v.ToList());

        builder.Property(selector)
            .HasColumnName(columnName)
            .HasColumnType("TEXT")
            .HasConversion(
                v => JsonSerializer.Serialize(v, SerializerOptions),
                v => JsonSerializer.Deserialize<List<string>>(v, SerializerOptions) ?? new List<string>())
            .Metadata.SetValueComparer(comparer);
    }
}
