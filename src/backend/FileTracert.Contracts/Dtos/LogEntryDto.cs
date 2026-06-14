namespace FileTracert.Contracts.Dtos;

/// <summary>A persisted log line as returned by the logs API (level as a name).</summary>
public sealed record LogEntryDto(
    long Id,
    DateTime TimestampUtc,
    string Level,
    string Category,
    string Message,
    string? Exception,
    int? EventId,
    string? Scope);
