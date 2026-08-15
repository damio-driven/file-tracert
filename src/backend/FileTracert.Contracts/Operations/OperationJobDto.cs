namespace FileTracert.Contracts.Operations;

public sealed class OperationJobDto
{
    public int Id { get; set; }
    public string Type { get; set; } = null!;
    public string State { get; set; } = null!;
    public string BlockReason { get; set; } = null!;

    public int? SourceVolumeId { get; set; }
    public string? SourceVolumeLabel { get; set; }
    public int? TargetVolumeId { get; set; }
    public string? TargetVolumeLabel { get; set; }

    /// <summary>Snapshot source path (from first OperationJobItem).</summary>
    public string? SourcePath { get; set; }

    /// <summary>Destination path stored on the job (directory for Move, new name for Rename, full path for CreateFolder).</summary>
    public string? TargetPath { get; set; }

    public bool IsIntraVolume { get; set; }
    public long TotalBytes { get; set; }
    public long BytesProcessed { get; set; }
    public long RequiredBytesTarget { get; set; }
    public long FreedBytesSource { get; set; }
    public bool EstimateIsLive { get; set; }

    public int SequenceOrder { get; set; }

    /// <summary>
    /// The job this one is waiting for, when it is Blocked on a dependency (§5). The UI links
    /// straight to that row so "in attesa dell'operazione #N" is one click, not a search.
    /// </summary>
    public int? DependsOnJobId { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>Current feasibility; populated for Blocked jobs in the list endpoint.</summary>
    public FeasibilityResult? Feasibility { get; set; }
}
