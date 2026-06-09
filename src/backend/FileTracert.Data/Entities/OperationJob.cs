using FileTracert.Contracts.Enums;

namespace FileTracert.Data.Entities;

public class OperationJob : IAuditable
{
    public int Id { get; set; }
    public JobType Type { get; set; }
    public JobState State { get; set; }
    public JobBlockReason BlockReason { get; set; }

    public int? SourceVolumeId { get; set; }
    public int? TargetVolumeId { get; set; }
    public string? TargetRelativePath { get; set; }
    public bool IsIntraVolume { get; set; }

    public long TotalBytes { get; set; }
    public long BytesProcessed { get; set; }
    public long RequiredBytesTarget { get; set; }
    public long FreedBytesSource { get; set; }
    public bool EstimateIsLive { get; set; }

    public int SequenceOrder { get; set; }
    public int? DependsOnJobId { get; set; }
    public int RetryCount { get; set; }
    public string? ErrorMessage { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public Volume? SourceVolume { get; set; }
    public Volume? TargetVolume { get; set; }
    public OperationJob? DependsOnJob { get; set; }
    public ICollection<OperationJobItem> Items { get; set; } = new List<OperationJobItem>();
    public ICollection<SpaceLedgerEntry> LedgerEntries { get; set; } = new List<SpaceLedgerEntry>();
}
