using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

/// <summary>Static lookup: extension (without dot, lower-cased) → category. Not auditable.</summary>
public class ExtensionCategory
{
    public string Extension { get; set; } = null!;
    public FileCategory Category { get; set; }
}
