namespace FileTracert.Data.Entities;

public interface IAuditable
{
    DateTime CreatedUtc { get; set; }
    DateTime UpdatedUtc { get; set; }
}
