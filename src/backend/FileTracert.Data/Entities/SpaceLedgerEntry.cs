namespace FileTracert.Data.Entities;

public class SpaceLedgerEntry : IAuditable
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public int VolumeId { get; set; }

    /// <summary>Positive = reservation, negative = liberation.</summary>
    public long DeltaBytes { get; set; }
    public bool IsActive { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public OperationJob Job { get; set; } = null!;
    public Volume Volume { get; set; } = null!;
}
