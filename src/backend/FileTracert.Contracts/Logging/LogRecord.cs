namespace FileTracert.Contracts.Logging;

/// <summary>
/// One log line on its way to the dedicated log store. <paramref name="Level"/> is
/// the integer log level (Trace=0 … Critical=5). <paramref name="Exception"/> holds
/// the full formatted exception (message + stack + inner) when present.
/// </summary>
public sealed record LogRecord(
    DateTime TimestampUtc,
    int Level,
    string Category,
    string Message,
    string? Exception,
    int? EventId,
    string? Scope);
