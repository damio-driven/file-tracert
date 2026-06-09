using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

public class OperationJobItem : IAuditable
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int? FileId { get; set; }

    /// <summary>Snapshot of the source path at enqueue time.</summary>
    public string SourceRelativePath { get; set; } = null!;
    public string TargetRelativePath { get; set; } = null!;
    public long SizeBytes { get; set; }
    public JobItemState State { get; set; }

    /// <summary>Temp path for the in-flight copy (.fadit-partial).</summary>
    public string? TempPath { get; set; }
    public long BytesCopied { get; set; }
    public string? Hash { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public OperationJob Job { get; set; } = null!;
}
