using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Logging;

/// <summary>
/// Persistence port for the dedicated log database. SQLite-specific (its own file,
/// raw connection — never the main <c>DbContext</c>) so the implementation lives in
/// the Data layer behind this interface. Every write must be safe to call before
/// the main data layer is ready.
/// </summary>
public interface ILogStore
{
    /// <summary>Creates the log table/indexes if missing. Idempotent; safe at startup.</summary>
    void EnsureSchema();

    /// <summary>Appends a batch of records. Throws on failure (the caller swallows).</summary>
    Task WriteBatchAsync(IReadOnlyList<LogRecord> records, CancellationToken ct);

    /// <summary>One page of log lines (newest first) matching the query.</summary>
    Task<PagedResult<LogEntryDto>> QueryAsync(LogQuery query, CancellationToken ct);

    /// <summary>
    /// Retention pass: deletes rows older than <paramref name="olderThanUtc"/>, then
    /// trims the oldest beyond <paramref name="maxRows"/>. Returns rows removed.
    /// </summary>
    Task<int> TrimAsync(DateTime olderThanUtc, int maxRows, bool vacuum, CancellationToken ct);

    /// <summary>
    /// Merges the write-ahead log back into the main file and truncates it, keeping the
    /// WAL bounded during long runs. Best-effort under concurrent writers.
    /// </summary>
    Task CheckpointAsync(CancellationToken ct);
}
