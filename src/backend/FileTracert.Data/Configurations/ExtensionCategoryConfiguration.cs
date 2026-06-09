using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileTracert.Data.Configurations;

public sealed class ExtensionCategoryConfiguration : IEntityTypeConfiguration<ExtensionCategory>
{
    public void Configure(EntityTypeBuilder<ExtensionCategory> builder)
    {
        builder.ToTable("ExtensionCategories");
        builder.HasKey(x => x.Extension);

        builder.Property(x => x.Extension).IsRequired();
        builder.Property(x => x.Category).HasConversion<string>();

        builder.HasData(BuildSeed());
    }

    private static IEnumerable<ExtensionCategory> BuildSeed()
    {
        (FileCategory Category, string[] Extensions)[] groups =
        {
            (FileCategory.Image, new[]
            {
                "jpg", "jpeg", "png", "gif", "bmp", "tiff", "tif", "webp",
                "heic", "heif", "raw", "cr2", "cr3", "nef", "arw", "dng",
                "orf", "raf", "svg"
            }),
            (FileCategory.Video, new[]
            {
                "mp4", "mov", "avi", "mkv", "wmv", "flv", "webm", "m4v",
                "mpg", "mpeg", "3gp", "mts", "m2ts"
            }),
            (FileCategory.Audio, new[]
            {
                "mp3", "wav", "flac", "aac", "ogg", "wma", "m4a", "aiff",
                "alac", "opus"
            }),
            (FileCategory.Document, new[]
            {
                "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt",
                "rtf", "odt", "ods", "odp", "md", "csv", "epub"
            }),
            (FileCategory.Archive, new[]
            {
                "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso"
            }),
        };

        foreach (var (category, extensions) in groups)
        {
            foreach (var ext in extensions)
            {
                yield return new ExtensionCategory { Extension = ext, Category = category };
            }
        }
    }
}
