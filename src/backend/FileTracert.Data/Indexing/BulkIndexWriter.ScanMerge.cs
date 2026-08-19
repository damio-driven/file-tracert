using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FileTracert.Data.Indexing;

/// <summary>
/// The scan merge, SQLite side. A re-scan reconciles instead of replacing: matched rows
/// are updated in place, unseen ones inserted, and the rows left over are split by WHY the scan
/// did not touch them — deliberately skipped (excluded) or looked for and not found (absent).
/// Everything is set-based through per-connection staging tables — no volume-wide dictionary in
/// memory (a system drive holds millions of rows) and no per-row round-trip from Business.
/// </summary>
/// <remarks>
/// <para><b>Staging is TEMP and per batch.</b> A <c>TEMP</c> table lives in the connection's
/// private schema, so it can never collide with another writer and leaves nothing behind if
/// the process dies mid-scan. Its lifetime is a single merge call, which always runs inside
/// the caller's transaction (the connection is therefore open throughout); the
/// <c>CREATE … IF NOT EXISTS</c> + <c>DELETE FROM</c> pair makes the call correct whether or
/// not the connection survived the previous batch, so nothing is assumed about pooling.</para>
/// <para><b>Matching order</b> (§2 of the step 9a task): the USN file reference first — it is
/// the file's real identity and survives a rename done outside the app — then the physical
/// location <c>(DirectoryId, Name)</c>, compared <c>COLLATE NOCASE</c> because Windows does
/// not distinguish case while SQLite's default BINARY collation does (review item P2).
/// <c>NOCASE</c> folds ASCII only; a non-ASCII file whose case changed on disk is matched as
/// a new row instead of an update. That path only applies to the enumeration engine (on NTFS
/// the FRN answers first) and it degrades to an extra row, never to a lost overlay.</para>
/// <para><b>No duplicate-FRN hazard.</b> The unique index on <c>(VolumeId, UsnFileRef)</c>
/// could be violated only if a row matched by path carried an FRN already held by a different
/// row — impossible, because that staged row would have been matched by the FRN pass first.</para>
/// </remarks>
public sealed partial class BulkIndexWriter
{
    public async Task<ScanMergeBatchResult> MergeScannedFilesAsync(
        int volumeId, IReadOnlyCollection<FileEntry> batch, DateTime indexedUtc, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return new ScanMergeBatchResult(0, 0, []);
        }

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var tx = _db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;

            await PrepareStagingAsync(conn, tx, ct);
            await FillStagingAsync(conn, tx, batch, ct);
            await MatchStagedRowsAsync(conn, tx, volumeId, ct);

            var updated = await UpdateMatchedAsync(conn, tx, indexedUtc, ct);
            var inserted = await InsertUnmatchedAsync(conn, tx, volumeId, indexedUtc, ct);

            if (inserted > 0)
            {
                // The rows just inserted have identities now: resolve them so the caller can
                // refresh the search index for exactly this batch.
                await MatchStagedByPathAsync(conn, tx, volumeId, excludeClaimed: false, ct);
            }

            var affected = await ReadAffectedIdsAsync(conn, tx, batch.Count, ct);
            return new ScanMergeBatchResult(inserted, updated, affected);
        }
        finally
        {
            _db.Database.CloseConnection();
        }
    }

    public async Task<ScanClosureResult> ReconcileUnseenFilesAsync(
        int volumeId,
        DateTime scanStartedUtc,
        IReadOnlyCollection<SkippedScanArea> skipped,
        CancellationToken ct)
    {
        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);
        try
        {
            var tx = _db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
            var now = DateTime.UtcNow;

            // Exclusion runs FIRST, and that ordering is the whole trick: the rows it flags
            // IsIncluded = 0 drop out of the absence pass by themselves, so that pass keeps the
            // one statement and the exact predicate it has always had — no negated subquery on
            // the hot path, and nothing to keep in step between the two.
            var excluded = skipped.Count == 0
                ? 0
                : await ExcludeSkippedAsync(conn, tx, volumeId, scanStartedUtc, now, skipped, ct);

            var absent = await MarkAbsentAsync(conn, tx, volumeId, scanStartedUtc, now, ct);
            return new ScanClosureResult(excluded, absent);
        }
        finally
        {
            _db.Database.CloseConnection();
        }
    }

    /// <summary>
    /// Flags what the scan deliberately skipped as excluded, leaving <c>IsPresent</c> untouched:
    /// the file is on disk, it is simply outside the perimeter the user asked for (§4). The areas
    /// are staged the same way the merge stages its batch — a per-connection TEMP table — so the
    /// pass stays one UPDATE PER CAUSE whatever the number of rows behind those areas.
    ///
    /// <para>Two statements rather than one because the two causes are undone by different people
    /// (step 11h) and a row must carry the one that is true of it. A single UPDATE could set both
    /// flags with correlated CASE subqueries, at the price of asking the staging table three times
    /// per row instead of once; a cause with no areas costs nothing here, and the common shape of a
    /// scan closure is one cause or none at all.</para>
    /// </summary>
    private static async Task<int> ExcludeSkippedAsync(
        SqliteConnection conn, SqliteTransaction? tx, int volumeId,
        DateTime scanStartedUtc, DateTime now, IReadOnlyCollection<SkippedScanArea> skipped, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx,
            """
            CREATE TEMP TABLE IF NOT EXISTS ScanSkipAreas (
                DirectoryId INTEGER NOT NULL,
                Name        TEXT    NULL,
                Cause       TEXT    NOT NULL
            );
            """, ct);
        await ExecuteAsync(conn, tx,
            "CREATE INDEX IF NOT EXISTS IX_ScanSkipAreas_DirectoryId ON ScanSkipAreas(DirectoryId);", ct);
        await ExecuteAsync(conn, tx, "DELETE FROM ScanSkipAreas;", ct);

        var causes = new HashSet<ScanSkipCause>();
        await using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText =
                "INSERT INTO ScanSkipAreas (DirectoryId, Name, Cause) VALUES ($dir, $name, $cause);";
            var dir = insert.Parameters.Add("$dir", SqliteType.Integer);
            var name = insert.Parameters.Add("$name", SqliteType.Text);
            var cause = insert.Parameters.Add("$cause", SqliteType.Text);

            foreach (var area in skipped)
            {
                dir.Value = area.DirectoryId;
                name.Value = area.FileName is null ? DBNull.Value : area.FileName;
                cause.Value = area.Cause.ToString();
                causes.Add(area.Cause);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        var excluded = 0;
        foreach (var cause in causes)
        {
            excluded += await ExcludeForCauseAsync(conn, tx, volumeId, scanStartedUtc, now, cause, ct);
        }

        return excluded;
    }

    /// <summary>
    /// One cause, one statement. The guard is the cause's OWN flag, not <c>IsIncluded</c>: a row
    /// already excluded for a different reason still has to learn this one, or undoing that other
    /// reason would let it back in. (A <c>.tmp</c> inside a hidden folder is the case: excluded by
    /// type, and it must not return when the type filter widens.)
    /// </summary>
    private static Task<int> ExcludeForCauseAsync(
        SqliteConnection conn, SqliteTransaction? tx, int volumeId,
        DateTime scanStartedUtc, DateTime now, ScanSkipCause cause, CancellationToken ct)
    {
        var column = ColumnFor(cause);

        // A NULL Name means the whole directory. Name comparison is COLLATE NOCASE for the same
        // reason the merge matches that way: Windows does not distinguish case, SQLite's default
        // BINARY collation does.
        return ExecuteAsync(conn, tx,
            $"""
            UPDATE Files
               SET {column} = 1, IsIncluded = 0, RowUpdatedUtc = $now
             WHERE VolumeId = $vol AND {column} = 0
               AND LastIndexedUtc < $scanStart
               AND EXISTS (SELECT 1 FROM ScanSkipAreas s
                            WHERE s.DirectoryId = Files.DirectoryId
                              AND s.Cause = $cause
                              AND (s.Name IS NULL OR s.Name = Files.Name COLLATE NOCASE));
            """, ct,
            ("$vol", volumeId), ("$scanStart", scanStartedUtc), ("$now", now), ("$cause", cause.ToString()));
    }

    /// <summary>
    /// The one place a scan-skip cause becomes a column name. Interpolated into the statement
    /// rather than parameterised because a column name cannot be a parameter — which is safe here
    /// precisely because it comes from this switch and never from a caller's string.
    /// </summary>
    private static string ColumnFor(ScanSkipCause cause) => cause switch
    {
        ScanSkipCause.InactiveRoot => "ExcludedByRoot",
        ScanSkipCause.FilteredOut => "ExcludedByScan",
        _ => throw new ArgumentOutOfRangeException(nameof(cause), cause, "Unknown scan skip cause."),
    };

    /// <summary>
    /// Flags what the scan looked for and did not find. Only rows the filter includes: an excluded
    /// file is never handed to the merge, so "not touched by this scan" says nothing about whether
    /// it is still on disk.
    /// </summary>
    private static Task<int> MarkAbsentAsync(
        SqliteConnection conn, SqliteTransaction? tx, int volumeId,
        DateTime scanStartedUtc, DateTime now, CancellationToken ct) =>
        // The bound is passed as a DateTime, not as an ISO string — the provider then writes it in
        // the same TEXT layout as the column, which is what makes the "<" comparison mean what it
        // reads (review finding #11).
        ExecuteAsync(conn, tx,
            """
            UPDATE Files
               SET IsPresent = 0, RowUpdatedUtc = $now
             WHERE VolumeId = $vol AND IsIncluded = 1 AND IsPresent = 1
               AND LastIndexedUtc < $scanStart
            """, ct, ("$vol", volumeId), ("$scanStart", scanStartedUtc), ("$now", now));

    // ── staging ───────────────────────────────────────────────────────────────

    private static async Task PrepareStagingAsync(SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        await ExecuteAsync(conn, tx,
            """
            CREATE TEMP TABLE IF NOT EXISTS ScanStageFiles (
                DirectoryId INTEGER NOT NULL,
                Name        TEXT    NOT NULL,
                Extension   TEXT    NOT NULL,
                Category    TEXT    NOT NULL,
                SizeBytes   INTEGER NOT NULL,
                CreatedUtc  TEXT    NOT NULL,
                ModifiedUtc TEXT    NOT NULL,
                Attributes  INTEGER NOT NULL,
                UsnFileRef  INTEGER NULL,
                MatchedId   INTEGER NULL
            );
            """, ct);

        // Serves the "already claimed" anti-join and the final id read-back; the batch is
        // small by construction, but without it both degenerate into nested scans.
        await ExecuteAsync(conn, tx,
            "CREATE INDEX IF NOT EXISTS IX_ScanStageFiles_MatchedId ON ScanStageFiles(MatchedId);", ct);
        await ExecuteAsync(conn, tx,
            "CREATE INDEX IF NOT EXISTS IX_ScanStageFiles_Location ON ScanStageFiles(DirectoryId, Name);", ct);

        await ExecuteAsync(conn, tx, "DELETE FROM ScanStageFiles;", ct);
    }

    private static async Task FillStagingAsync(
        SqliteConnection conn, SqliteTransaction? tx, IReadOnlyCollection<FileEntry> batch, CancellationToken ct)
    {
        // One prepared statement reused for the whole batch: multi-row VALUES would have to
        // guess SQLite's parameter ceiling, and the whole loop runs inside one transaction.
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            INSERT INTO ScanStageFiles
                (DirectoryId, Name, Extension, Category, SizeBytes, CreatedUtc, ModifiedUtc, Attributes, UsnFileRef)
            VALUES ($dir, $name, $ext, $cat, $size, $created, $modified, $attrs, $frn);
            """;

        var dir = cmd.Parameters.Add("$dir", SqliteType.Integer);
        var name = cmd.Parameters.Add("$name", SqliteType.Text);
        var ext = cmd.Parameters.Add("$ext", SqliteType.Text);
        var cat = cmd.Parameters.Add("$cat", SqliteType.Text);
        var size = cmd.Parameters.Add("$size", SqliteType.Integer);
        var created = cmd.Parameters.Add("$created", SqliteType.Text);
        var modified = cmd.Parameters.Add("$modified", SqliteType.Text);
        var attrs = cmd.Parameters.Add("$attrs", SqliteType.Integer);
        var frn = cmd.Parameters.Add("$frn", SqliteType.Integer);

        foreach (var file in batch)
        {
            dir.Value = file.DirectoryId;
            name.Value = file.Name;
            ext.Value = file.Extension;
            cat.Value = file.Category.ToString();
            size.Value = file.SizeBytes;
            created.Value = file.FileCreatedUtc;
            modified.Value = file.FileModifiedUtc;
            attrs.Value = (int)file.Attributes;
            frn.Value = file.UsnFileRef.HasValue ? file.UsnFileRef.Value : DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ── matching ──────────────────────────────────────────────────────────────

    private static async Task MatchStagedRowsAsync(
        SqliteConnection conn, SqliteTransaction? tx, int volumeId, CancellationToken ct)
    {
        // 1. By USN file reference — the file's identity, unique per volume by index.
        await ExecuteAsync(conn, tx,
            """
            UPDATE ScanStageFiles
               SET MatchedId = (SELECT f.Id FROM Files f
                                 WHERE f.VolumeId = $vol AND f.UsnFileRef = ScanStageFiles.UsnFileRef)
             WHERE UsnFileRef IS NOT NULL;
            """, ct, ("$vol", volumeId));

        // 2. By physical location, skipping rows another staged file already claimed by FRN.
        //    Without this, a recreated file (new FRN, same name) and the renamed original
        //    (old FRN, new name) would both land on the same catalog row and one would vanish.
        //    The claimed ids go through their own TEMP table: reading the table an UPDATE is
        //    writing has no defined snapshot semantics in SQLite.
        await ExecuteAsync(conn, tx, "DROP TABLE IF EXISTS temp.ScanStageClaimed;", ct);
        await ExecuteAsync(conn, tx,
            """
            CREATE TEMP TABLE ScanStageClaimed AS
            SELECT DISTINCT MatchedId AS Id FROM ScanStageFiles WHERE MatchedId IS NOT NULL;
            """, ct);
        await ExecuteAsync(conn, tx,
            "CREATE INDEX IX_ScanStageClaimed_Id ON ScanStageClaimed(Id);", ct);

        await MatchStagedByPathAsync(conn, tx, volumeId, excludeClaimed: true, ct);
    }

    private static Task MatchStagedByPathAsync(
        SqliteConnection conn, SqliteTransaction? tx, int volumeId, bool excludeClaimed, CancellationToken ct)
    {
        var claimedFilter = excludeClaimed
            ? "AND NOT EXISTS (SELECT 1 FROM ScanStageClaimed c WHERE c.Id = f.Id)"
            : string.Empty;

        return ExecuteAsync(conn, tx,
            $"""
            UPDATE ScanStageFiles
               SET MatchedId = (SELECT f.Id FROM Files f
                                 WHERE f.VolumeId = $vol
                                   AND f.DirectoryId = ScanStageFiles.DirectoryId
                                   AND f.Name = ScanStageFiles.Name COLLATE NOCASE
                                   {claimedFilter}
                                 ORDER BY f.Id LIMIT 1)
             WHERE MatchedId IS NULL;
            """, ct, ("$vol", volumeId));
    }

    // ── write-back ────────────────────────────────────────────────────────────

    private static async Task<int> UpdateMatchedAsync(
        SqliteConnection conn, SqliteTransaction? tx, DateTime indexedUtc, CancellationToken ct)
    {
        // Only the physical facts a scan can observe. Id, QuickHash/Hash (never re-derived by a
        // scan) and every Pending* field — the queue's projection (§5) — are deliberately absent
        // from the SET list.
        //
        // IsIncluded IS set, because a row in this batch is a row the pipeline's filter let
        // through: the scan IS the filter's decision (§4), and it is the only one that can undo an
        // exclusion nobody asked for through Setup — a folder that stops being hidden raises no
        // event, so without this its content would stay invisible for ever. The three cause flags
        // go with it: the row is inside the perimeter, of an allowed type, under an active root —
        // every reason it could have carried is gone, and leaving one behind would keep the row
        // out of the next reconciliation for a cause that is no longer true.
        return await ExecuteAsync(conn, tx,
            """
            UPDATE Files
               SET DirectoryId    = s.DirectoryId,
                   Name           = s.Name,
                   Extension      = s.Extension,
                   Category       = s.Category,
                   SizeBytes      = s.SizeBytes,
                   CreatedUtc     = s.CreatedUtc,
                   ModifiedUtc    = s.ModifiedUtc,
                   Attributes     = s.Attributes,
                   UsnFileRef     = COALESCE(s.UsnFileRef, Files.UsnFileRef),
                   IsIncluded     = 1,
                   ExcludedByType = 0,
                   ExcludedByRoot = 0,
                   ExcludedByScan = 0,
                   IsPresent      = 1,
                   LastIndexedUtc = $now,
                   RowUpdatedUtc  = $now
              FROM ScanStageFiles s
             WHERE s.MatchedId = Files.Id;
            """, ct, ("$now", indexedUtc));
    }

    private static async Task<int> InsertUnmatchedAsync(
        SqliteConnection conn, SqliteTransaction? tx, int volumeId, DateTime indexedUtc, CancellationToken ct)
    {
        return await ExecuteAsync(conn, tx,
            """
            INSERT INTO Files
                (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes, CreatedUtc, ModifiedUtc,
                 Attributes, UsnFileRef, IsIncluded, ExcludedByType, ExcludedByRoot, ExcludedByScan,
                 IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT $vol, s.DirectoryId, s.Name, s.Extension, s.Category, s.SizeBytes, s.CreatedUtc,
                   s.ModifiedUtc, s.Attributes, s.UsnFileRef, 1, 0, 0, 0, 1, $now, 'None', $now, $now
              FROM ScanStageFiles s
             WHERE s.MatchedId IS NULL;
            """, ct, ("$vol", volumeId), ("$now", indexedUtc));
    }

    private static async Task<IReadOnlyList<int>> ReadAffectedIdsAsync(
        SqliteConnection conn, SqliteTransaction? tx, int capacity, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT MatchedId FROM ScanStageFiles WHERE MatchedId IS NOT NULL;";

        var ids = new List<int>(capacity);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection conn, SqliteTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
