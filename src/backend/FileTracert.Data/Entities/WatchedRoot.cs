namespace FileTracert.Data.Entities;

public class WatchedRoot : IAuditable
{
    public int Id { get; set; }
    public int VolumeId { get; set; }
    public string RelativePath { get; set; } = null!;
    public bool IsActive { get; set; }
    public string? FilterOverrideJson { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }

    public Volume Volume { get; set; } = null!;
}
