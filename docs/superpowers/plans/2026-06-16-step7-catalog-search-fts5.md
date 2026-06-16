# Step 7 — Catalog (Lazy Tree) + Search (FTS5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add FTS5 full-text search + lazy-tree catalog browsing: FTS5 `FileSearchIndex` virtual table, sync hooked into scan pipeline, `POST /api/search` and `GET /api/catalog/{volume}/children` APIs, plus the Ricerca and Catalogo Angular screens.

**Architecture:** FTS5 regular virtual table (no `content=`) keyed on `rowid = Files.Id`; managed explicitly (no triggers) via `IFileSearchIndex` (Contracts) / `FileSearchIndex` (Data). `ScanService` clears FTS before Files deletion and syncs FTS after bulk insert, within the same transaction. Search controller uses `IFileSearchIndex.SearchAsync` to get file IDs, then fetches DTOs from DbContext. Catalog controller queries `Directories` + `Files` directly from the index.

**Tech Stack:** .NET 10, EF Core 10, SQLite FTS5 (`unicode61` tokenizer), Microsoft.Data.Sqlite raw commands (for search query + FTS management), Angular 21 standalone zoneless, @ngrx/signals, CDK Virtual Scroll, impeccable skill for UI.

---

## File Map

**New backend files:**
- `src/backend/FileTracert.Contracts/Search/IFileSearchIndex.cs`
- `src/backend/FileTracert.Contracts/Search/FileSearchQuery.cs`
- `src/backend/FileTracert.Contracts/Dtos/SearchRequest.cs`
- `src/backend/FileTracert.Contracts/Dtos/SearchResultDto.cs`
- `src/backend/FileTracert.Contracts/Dtos/CatalogChildrenDto.cs`
- `src/backend/FileTracert.Contracts/Dtos/CatalogDirDto.cs`
- `src/backend/FileTracert.Contracts/Dtos/CatalogFileDto.cs`
- `src/backend/FileTracert.Data/Search/FileSearchIndex.cs`
- `src/backend/FileTracert.Data/Migrations/<timestamp>_AddFts5FileSearchIndex.cs` (generated then edited)
- `src/backend/FileTracert.Host/Controllers/SearchController.cs`
- `src/backend/FileTracert.Host/Controllers/CatalogController.cs`
- `tests/FileTracert.Tests/Data/FileSearchIndexTests.cs`
- `tests/FileTracert.Tests/Host/SearchApiTests.cs`
- `tests/FileTracert.Tests/Host/CatalogApiTests.cs`

**New frontend files:**
- `src/frontend/src/app/core/api/search-api.service.ts`
- `src/frontend/src/app/core/api/catalog-api.service.ts`
- `src/frontend/src/app/features/search/search.store.ts`
- `src/frontend/src/app/features/search/search.ts`
- `src/frontend/src/app/features/search/search.html`
- `src/frontend/src/app/features/search/search.scss`
- `src/frontend/src/app/features/search/search.store.spec.ts`
- `src/frontend/src/app/features/catalog/catalog.store.ts`
- `src/frontend/src/app/features/catalog/catalog.ts`
- `src/frontend/src/app/features/catalog/catalog.html`
- `src/frontend/src/app/features/catalog/catalog.scss`
- `src/frontend/src/app/features/catalog/catalog.store.spec.ts`

**Modified backend files:**
- `src/backend/FileTracert.Data/DataServiceCollectionExtensions.cs` — register `IFileSearchIndex`
- `src/backend/FileTracert.Business/Scanning/ScanService.cs` — inject + call FTS sync
- `src/backend/FileTracert.Host/Infrastructure/DatabaseInitializer.cs` — backfill on startup

**Modified frontend files:**
- `src/frontend/src/app/core/models/catalog.models.ts` — add Search/Catalog DTOs
- `src/frontend/src/app/app.routes.ts` — add catalog + search routes
- `src/frontend/src/app/app.html` — activate nav links

---

## Task 1: FTS5 contracts + interface + migration

**Files:**
- Create: `src/backend/FileTracert.Contracts/Search/IFileSearchIndex.cs`
- Create: `src/backend/FileTracert.Contracts/Search/FileSearchQuery.cs`
- Create (via `dotnet ef migrations add` then edit): `src/backend/FileTracert.Data/Migrations/<ts>_AddFts5FileSearchIndex.cs`

- [ ] **Step 1.1: Create `FileSearchQuery.cs`**

```csharp
// src/backend/FileTracert.Contracts/Search/FileSearchQuery.cs
using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Search;

public sealed record FileSearchQuery(
    string Text,
    SearchScope Scope,
    FileCategory? Category,
    string[]? Extensions,
    long? SizeBytesMin,
    long? SizeBytesMax,
    DateTime? ModifiedFrom,
    DateTime? ModifiedTo,
    int? VolumeId,
    bool OnlineOnly,
    SearchSort Sort,
    bool Desc,
    int Skip,
    int Take);

public enum SearchScope { Name, FullPath }
public enum SearchSort { Relevance, Name, Date, Size }
```

- [ ] **Step 1.2: Create `IFileSearchIndex.cs`**

```csharp
// src/backend/FileTracert.Contracts/Search/IFileSearchIndex.cs
using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Search;

/// <summary>
/// Port interface for the FTS5 full-text search index. Implemented in Data (SQLite-specific).
/// Sync is explicit — no triggers. Called by ScanService within the scan transaction.
/// </summary>
public interface IFileSearchIndex
{
    /// <summary>Delete FTS entries for a volume's files. Call BEFORE deleting Files rows.</summary>
    Task ClearVolumeAsync(int volumeId, CancellationToken ct);

    /// <summary>Insert FTS entries from the Files table for a volume. Call AFTER bulk insert.</summary>
    Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct);

    /// <summary>Full rebuild of the FTS index from all Files in the DB. Used for one-time backfill.</summary>
    Task RebuildAsync(CancellationToken ct);

    /// <summary>Single-file upsert — for incremental USN updates (step 10).</summary>
    Task UpsertAsync(int fileId, string name, string path, CancellationToken ct);

    /// <summary>Single-file remove — for incremental USN deletes (step 10).</summary>
    Task RemoveAsync(int fileId, CancellationToken ct);

    /// <summary>FTS5 MATCH + structural filters + paging. Returns file IDs and total count.</summary>
    Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct);
}
```

- [ ] **Step 1.3: Generate migration skeleton**

Run from repo root:
```
dotnet ef migrations add AddFts5FileSearchIndex --project src/backend/FileTracert.Data --startup-project src/backend/FileTracert.Host
```

Expected: new migration file in `src/backend/FileTracert.Data/Migrations/`.

- [ ] **Step 1.4: Edit migration to add FTS5 virtual table**

Replace the generated (empty) `Up` and `Down` bodies:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("""
        CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
            name,
            path,
            tokenize="unicode61 remove_diacritics 2 separators '\._-'"
        );
        """);
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.Sql("DROP TABLE IF EXISTS FileSearchIndex;");
}
```

> Note: The tokenizer uses `unicode61` with `remove_diacritics 2` (accent-insensitive) and `separators '\._-'` so that paths/names split on backslash, dot, underscore, hyphen.
> The snapshot file is auto-updated by EF (it does not model virtual tables, so the snapshot change is minimal).

- [ ] **Step 1.5: Build — verify zero errors**

```
dotnet build src/backend/FileTracert.sln
```

Expected: Build succeeded, 0 errors.

---

## Task 2: `FileSearchIndex` implementation + DI + FTS sync in ScanService + startup backfill

**Files:**
- Create: `src/backend/FileTracert.Data/Search/FileSearchIndex.cs`
- Modify: `src/backend/FileTracert.Data/DataServiceCollectionExtensions.cs`
- Modify: `src/backend/FileTracert.Business/Scanning/ScanService.cs`
- Modify: `src/backend/FileTracert.Host/Infrastructure/DatabaseInitializer.cs`

- [ ] **Step 2.1: Write `FileSearchIndex.cs`**

```csharp
// src/backend/FileTracert.Data/Search/FileSearchIndex.cs
using System.Text;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Data.Search;

/// <summary>
/// SQLite FTS5 implementation of <see cref="IFileSearchIndex"/>.
/// Uses the DbContext connection so operations participate in the caller's transaction.
/// </summary>
public sealed class FileSearchIndex : IFileSearchIndex
{
    private readonly FileTracertDbContext _db;

    public FileSearchIndex(FileTracertDbContext db) => _db = db;

    public async Task ClearVolumeAsync(int volumeId, CancellationToken ct)
    {
        // Delete FTS rows whose rowid corresponds to Files on this volume.
        // Must be called BEFORE deleting the Files rows, otherwise the subquery returns nothing.
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM FileSearchIndex WHERE rowid IN (SELECT Id FROM Files WHERE VolumeId = {volumeId})",
            ct);
    }

    public async Task SyncVolumeFromDbAsync(int volumeId, CancellationToken ct)
    {
        // Bulk-populate FTS from the Files table after bulk insert.
        // Path = dirPath + '\' + fileName; root files have MaterializedPath = '' so just fileName.
        await _db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO FileSearchIndex(rowid, name, path)
            SELECT f.Id,
                   f.Name,
                   CASE WHEN d.MaterializedPath = '' THEN f.Name
                        ELSE d.MaterializedPath || '\' || f.Name END
            FROM Files f
            JOIN Directories d ON d.Id = f.DirectoryId
            WHERE f.VolumeId = {volumeId} AND f.IsIncluded = 1 AND f.IsPresent = 1
            """,
            ct);
    }

    public async Task RebuildAsync(CancellationToken ct)
    {
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM FileSearchIndex;", ct);
        await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO FileSearchIndex(rowid, name, path)
            SELECT f.Id,
                   f.Name,
                   CASE WHEN d.MaterializedPath = '' THEN f.Name
                        ELSE d.MaterializedPath || '\' || f.Name END
            FROM Files f
            JOIN Directories d ON d.Id = f.DirectoryId
            WHERE f.IsIncluded = 1 AND f.IsPresent = 1
            """,
            ct);
    }

    public async Task UpsertAsync(int fileId, string name, string path, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM FileSearchIndex WHERE rowid = {fileId}", ct);
        await _db.Database.ExecuteSqlAsync(
            $"INSERT INTO FileSearchIndex(rowid, name, path) VALUES ({fileId}, {name}, {path})", ct);
    }

    public async Task RemoveAsync(int fileId, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlAsync(
            $"DELETE FROM FileSearchIndex WHERE rowid = {fileId}", ct);
    }

    public async Task<PagedResult<int>> SearchAsync(FileSearchQuery query, CancellationToken ct)
    {
        var paged = new PagedRequest(query.Skip, query.Take).Normalized();
        var matchTerm = BuildMatchTerm(query.Text, query.Scope);

        // FTS5 MATCH cannot be expressed in LINQ; use raw SqliteCommand.
        // Reuse the DbContext's connection so the call participates in any open transaction.
        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct);

        var (filterSql, filterParams) = BuildFilterClause(query);

        // COUNT query (FTS5 count over large result sets can be slow; capped at 10 000 in total).
        var countSql =
            $"""
            SELECT MIN(COUNT(*), 10000)
            FROM FileSearchIndex fts
            JOIN Files f ON f.Id = fts.rowid
            JOIN Volumes v ON v.Id = f.VolumeId
            WHERE fts MATCH $match
              AND f.IsIncluded = 1 AND f.IsPresent = 1
            {filterSql}
            """;

        int total;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = countSql;
            cmd.Parameters.AddWithValue("$match", matchTerm);
            foreach (var (n, v) in filterParams) cmd.Parameters.AddWithValue(n, v);
            total = Convert.ToInt32((long)(await cmd.ExecuteScalarAsync(ct))!);
        }

        var sortExpr = query.Sort switch
        {
            SearchSort.Name => "f.Name",
            SearchSort.Date => "f.ModifiedUtc",
            SearchSort.Size => "f.SizeBytes",
            _ => "bm25(fts)", // bm25: lower = more relevant → ASC
        };
        var sortDir = query.Sort == SearchSort.Relevance
            ? "ASC"
            : (query.Desc ? "DESC" : "ASC");

        var pageSql =
            $"""
            SELECT fts.rowid
            FROM FileSearchIndex fts
            JOIN Files f ON f.Id = fts.rowid
            JOIN Volumes v ON v.Id = f.VolumeId
            WHERE fts MATCH $match
              AND f.IsIncluded = 1 AND f.IsPresent = 1
            {filterSql}
            ORDER BY {sortExpr} {sortDir}
            LIMIT $take OFFSET $skip
            """;

        var ids = new List<int>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = pageSql;
            cmd.Parameters.AddWithValue("$match", matchTerm);
            foreach (var (n, v) in filterParams) cmd.Parameters.AddWithValue(n, v);
            cmd.Parameters.AddWithValue("$take", paged.Take);
            cmd.Parameters.AddWithValue("$skip", paged.Skip);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                ids.Add(reader.GetInt32(0));
        }

        return new PagedResult<int>(ids, total, paged.Skip, paged.Take);
    }

    private static string BuildMatchTerm(string text, SearchScope scope)
    {
        // Sanitize: double-quote FTS5 special chars.
        var sanitized = text.Replace("\"", "\"\"").Trim();
        if (string.IsNullOrEmpty(sanitized))
            return "*"; // shouldn't hit the endpoint empty, but guard

        // Scope: 'name: term*' restricts to the name column; plain 'term*' searches both.
        return scope == SearchScope.Name
            ? $"name : \"{sanitized}*\""
            : $"\"{sanitized}*\"";
    }

    private static (string Sql, List<(string Name, object Value)> Params) BuildFilterClause(FileSearchQuery q)
    {
        var sb = new StringBuilder();
        var p = new List<(string, object)>();

        if (q.Category.HasValue)
        {
            sb.AppendLine("  AND f.Category = $category");
            p.Add(("$category", q.Category.Value.ToString()));
        }
        if (q.Extensions is { Length: > 0 })
        {
            // SQLite doesn't support array params; inline the safe lower-cased values.
            var list = string.Join(", ", q.Extensions.Select(e => $"'{e.Replace("'", "''").ToLowerInvariant()}'"));
            sb.AppendLine($"  AND f.Extension IN ({list})");
        }
        if (q.SizeBytesMin.HasValue)
        {
            sb.AppendLine("  AND f.SizeBytes >= $szMin");
            p.Add(("$szMin", q.SizeBytesMin.Value));
        }
        if (q.SizeBytesMax.HasValue)
        {
            sb.AppendLine("  AND f.SizeBytes <= $szMax");
            p.Add(("$szMax", q.SizeBytesMax.Value));
        }
        if (q.ModifiedFrom.HasValue)
        {
            sb.AppendLine("  AND f.ModifiedUtc >= $modFrom");
            p.Add(("$modFrom", q.ModifiedFrom.Value.ToString("o")));
        }
        if (q.ModifiedTo.HasValue)
        {
            sb.AppendLine("  AND f.ModifiedUtc <= $modTo");
            p.Add(("$modTo", q.ModifiedTo.Value.ToString("o")));
        }
        if (q.VolumeId.HasValue)
        {
            sb.AppendLine("  AND f.VolumeId = $volId");
            p.Add(("$volId", q.VolumeId.Value));
        }
        if (q.OnlineOnly)
        {
            sb.AppendLine("  AND v.IsOnline = 1");
        }

        return (sb.ToString(), p);
    }
}
```

> Count cap: `MIN(COUNT(*), 10000)` — acceptable for MVP. Document: total > 10 000 returns exactly 10 000; UI shows "10 000+" if `totalCount == 10000 && items.Count == take`.

- [ ] **Step 2.2: Register `IFileSearchIndex` in DI**

In `src/backend/FileTracert.Data/DataServiceCollectionExtensions.cs`, add after `AddScoped<IBulkIndexWriter, BulkIndexWriter>()`:

```csharp
services.AddScoped<IFileSearchIndex, FileSearchIndex>();
```

Add using at top: `using FileTracert.Contracts.Search;` and `using FileTracert.Data.Search;`.

- [ ] **Step 2.3: Wire FTS sync into `ScanService.PersistAsync`**

In `src/backend/FileTracert.Business/Scanning/ScanService.cs`:

1. Add `IFileSearchIndex _ftsIndex;` field and constructor parameter (inject after `_bulkWriter`):

```csharp
// Add to constructor params:
IFileSearchIndex ftsIndex,

// Add to fields:
private readonly IFileSearchIndex _ftsIndex;

// Add in body:
_ftsIndex = ftsIndex;
```

2. Add using: `using FileTracert.Contracts.Search;`

3. In `PersistAsync`, modify the body:

```csharp
private async Task PersistAsync(
    Volume volume,
    List<ScanItem> dirItems,
    List<ResolvedFile> files,
    long? checkpointUsn,
    CancellationToken ct)
{
    await using var tx = await _db.Database.BeginTransactionAsync(ct);
    await _db.Database.ExecuteSqlRawAsync("PRAGMA defer_foreign_keys=ON;", ct);

    // Clear FTS entries BEFORE deleting Files (subquery needs the rows).
    await _ftsIndex.ClearVolumeAsync(volume.Id, ct);

    // Idempotent re-scan: replace this volume's index.
    await _db.Files.Where(f => f.VolumeId == volume.Id).ExecuteDeleteAsync(ct);
    await _db.Directories.Where(d => d.VolumeId == volume.Id).ExecuteDeleteAsync(ct);

    var nodeByPath = BuildDirectoryTree(volume.Id, dirItems, files);

    _db.Directories.AddRange(nodeByPath.Values);
    await _db.SaveChangesAsync(ct);

    var now = DateTime.UtcNow;
    var fileEntities = files.Select(f => new FileEntry
    {
        VolumeId = volume.Id,
        DirectoryId = nodeByPath[ScanPath.Parent(f.Item.RelativePath)].Id,
        Name = f.Item.Name,
        Extension = f.Extension,
        Category = f.Category,
        SizeBytes = f.SizeBytes,
        FileCreatedUtc = f.CreatedUtc,
        FileModifiedUtc = f.ModifiedUtc,
        Attributes = f.Item.Attributes,
        UsnFileRef = f.Item.Frn is { } frn ? unchecked((long)frn) : null,
        IsIncluded = true,
        IsPresent = true,
        LastIndexedUtc = now,
    }).ToList();

    await _bulkWriter.BulkInsertFilesAsync(fileEntities, ct);

    // Sync FTS from the Files table (raw SQL so we use the auto-assigned IDs).
    await _ftsIndex.SyncVolumeFromDbAsync(volume.Id, ct);

    volume.LastFullScanUtc = now;
    if (checkpointUsn is { } usn)
        volume.LastUsn = usn;

    await _db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
}
```

- [ ] **Step 2.4: Add FTS backfill to `DatabaseInitializer.InitializeAsync`**

In `DatabaseInitializer`, inject `IServiceProvider _services` (already injected). After `await ApplyLogLevelAsync(...)` add:

```csharp
// Backfill FTS5 index if Files exist but FTS is empty (happens on upgrade from pre-step-7).
await BackfillFtsIfNeededAsync(scope, ct);
```

Add the method:

```csharp
private static async Task BackfillFtsIfNeededAsync(IServiceScope scope, CancellationToken ct)
{
    var db = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
    var fts = scope.ServiceProvider.GetRequiredService<IFileSearchIndex>();

    var hasFiles = await db.Files.AnyAsync(f => f.IsIncluded && f.IsPresent, ct);
    if (!hasFiles) return;

    // Check FTS count via raw command (virtual table, not modeled in EF).
    var conn = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
    await db.Database.OpenConnectionAsync(ct);
    long ftsCount;
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM FileSearchIndex LIMIT 1";
        ftsCount = (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    if (ftsCount == 0)
    {
        await fts.RebuildAsync(ct);
    }
}
```

Add using: `using FileTracert.Contracts.Search;` and `using Microsoft.EntityFrameworkCore;`.

- [ ] **Step 2.5: Build**

```
dotnet build src/backend/FileTracert.sln
```

Expected: 0 errors.

- [ ] **Step 2.6: Commit**

```
git add src/backend/FileTracert.Contracts/Search/ src/backend/FileTracert.Data/Search/ src/backend/FileTracert.Data/Migrations/ src/backend/FileTracert.Data/DataServiceCollectionExtensions.cs src/backend/FileTracert.Business/Scanning/ScanService.cs src/backend/FileTracert.Host/Infrastructure/DatabaseInitializer.cs
git commit -m "feat(data): FTS5 FileSearchIndex migration + IFileSearchIndex impl + sync in scan pipeline"
```

---

## Task 3: Search API (`POST /api/search`)

**Files:**
- Create: `src/backend/FileTracert.Contracts/Dtos/SearchRequest.cs`
- Create: `src/backend/FileTracert.Contracts/Dtos/SearchResultDto.cs`
- Create: `src/backend/FileTracert.Host/Controllers/SearchController.cs`

- [ ] **Step 3.1: Create `SearchRequest.cs`**

```csharp
// src/backend/FileTracert.Contracts/Dtos/SearchRequest.cs
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;

namespace FileTracert.Contracts.Dtos;

/// <summary>Request body for POST /api/search.</summary>
public sealed record SearchRequest(
    string Text,
    SearchScope Scope,
    FileCategory? Category,
    string[]? Extensions,
    long? SizeBytesMin,
    long? SizeBytesMax,
    DateTime? ModifiedFrom,
    DateTime? ModifiedTo,
    int? VolumeId,
    bool OnlineOnly,
    SearchSort Sort,
    bool Desc,
    int Skip,
    int Take);
```

- [ ] **Step 3.2: Create `SearchResultDto.cs`**

```csharp
// src/backend/FileTracert.Contracts/Dtos/SearchResultDto.cs
using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

public sealed record SearchResultDto(
    int FileId,
    string Name,
    string RelativePath,
    int VolumeId,
    string? VolumeLabel,
    string? VolumeLetter,
    bool VolumeIsOnline,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileCategory Category,
    string ProjectedState);   // placeholder "None" until step 9
```

- [ ] **Step 3.3: Create `SearchController.cs`**

```csharp
// src/backend/FileTracert.Host/Controllers/SearchController.cs
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>FTS5-powered file search with scope, filters, paging, and sort.</summary>
[ApiController]
[Route("api/search")]
public sealed class SearchController : ControllerBase
{
    private readonly IFileSearchIndex _fts;
    private readonly FileTracertDbContext _db;

    public SearchController(IFileSearchIndex fts, FileTracertDbContext db)
    {
        _fts = fts;
        _db = db;
    }

    /// <summary>
    /// POST body carries the full query (text, scope, filters, paging).
    /// Returns a paged list of matching files with volume context.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PagedResult<SearchResultDto>>> Search(
        [FromBody] SearchRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("text is required");

        var query = new FileSearchQuery(
            req.Text, req.Scope, req.Category, req.Extensions,
            req.SizeBytesMin, req.SizeBytesMax,
            req.ModifiedFrom, req.ModifiedTo,
            req.VolumeId, req.OnlineOnly,
            req.Sort, req.Desc, req.Skip, req.Take);

        var pagedIds = await _fts.SearchAsync(query, ct);

        if (pagedIds.Items.Count == 0)
            return Ok(new PagedResult<SearchResultDto>([], pagedIds.TotalCount, pagedIds.Skip, pagedIds.Take));

        // Fetch full file + volume data for the returned IDs.
        var ids = pagedIds.Items.ToHashSet();
        var rows = await _db.Files
            .AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Include(f => f.Directory)
            .Include(f => f.Volume)
            .ToListAsync(ct);

        // Preserve the FTS relevance/sort order.
        var byId = rows.ToDictionary(f => f.Id);
        var dtos = pagedIds.Items
            .Where(id => byId.ContainsKey(id))
            .Select(id =>
            {
                var f = byId[id];
                var dirPath = f.Directory.MaterializedPath;
                var relativePath = dirPath.Length == 0 ? f.Name : $"{dirPath}\\{f.Name}";
                return new SearchResultDto(
                    f.Id,
                    f.Name,
                    relativePath,
                    f.VolumeId,
                    f.Volume.Label,
                    f.Volume.LastDriveLetter,
                    f.Volume.IsOnline,
                    f.SizeBytes,
                    f.FileModifiedUtc,
                    f.Category,
                    "None");
            })
            .ToList();

        return Ok(new PagedResult<SearchResultDto>(dtos, pagedIds.TotalCount, pagedIds.Skip, pagedIds.Take));
    }
}
```

> The `Directory` and `Volume` navigations are loaded via `Include`; EF generates a JOIN, not N+1.

- [ ] **Step 3.4: Build**

```
dotnet build src/backend/FileTracert.sln
```

Expected: 0 errors.

- [ ] **Step 3.5: Commit**

```
git add src/backend/FileTracert.Contracts/Dtos/SearchRequest.cs src/backend/FileTracert.Contracts/Dtos/SearchResultDto.cs src/backend/FileTracert.Host/Controllers/SearchController.cs
git commit -m "feat(host): POST /api/search — FTS5 scope/filters/paging/sort"
```

---

## Task 4: Catalog API (`GET /api/catalog/{volume}/children`)

**Files:**
- Create: `src/backend/FileTracert.Contracts/Dtos/CatalogDirDto.cs`
- Create: `src/backend/FileTracert.Contracts/Dtos/CatalogFileDto.cs`
- Create: `src/backend/FileTracert.Contracts/Dtos/CatalogChildrenDto.cs`
- Create: `src/backend/FileTracert.Host/Controllers/CatalogController.cs`

- [ ] **Step 4.1: Create catalog DTOs**

```csharp
// src/backend/FileTracert.Contracts/Dtos/CatalogDirDto.cs
namespace FileTracert.Contracts.Dtos;

public sealed record CatalogDirDto(
    int Id,
    string Name,
    string MaterializedPath,
    int ChildDirectoryCount,
    int FileCount);
```

```csharp
// src/backend/FileTracert.Contracts/Dtos/CatalogFileDto.cs
using FileTracert.Contracts.Enums;

namespace FileTracert.Contracts.Dtos;

public sealed record CatalogFileDto(
    int Id,
    string Name,
    long SizeBytes,
    DateTime ModifiedUtc,
    FileCategory Category,
    string ProjectedState);   // placeholder "None" until step 9
```

```csharp
// src/backend/FileTracert.Contracts/Dtos/CatalogChildrenDto.cs
using FileTracert.Contracts.Paging;

namespace FileTracert.Contracts.Dtos;

public sealed record CatalogChildrenDto(
    IReadOnlyList<CatalogDirDto> Directories,
    PagedResult<CatalogFileDto> Files,
    bool VolumeIsOnline,
    string? VolumeLabel,
    string? VolumeLetter,
    int? CurrentDirectoryId,
    string? CurrentDirectoryPath);
```

- [ ] **Step 4.2: Create `CatalogController.cs`**

```csharp
// src/backend/FileTracert.Host/Controllers/CatalogController.cs
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Paging;
using FileTracert.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Host.Controllers;

/// <summary>
/// Catalog browser: lazy directory tree over the index.
/// directoryId=null → children of the volume root (MaterializedPath="").
/// Works offline (reads index, not disk).
/// </summary>
[ApiController]
[Route("api/catalog")]
public sealed class CatalogController : ControllerBase
{
    private readonly FileTracertDbContext _db;

    public CatalogController(FileTracertDbContext db) => _db = db;

    [HttpGet("{volumeId:int}/children")]
    public async Task<ActionResult<CatalogChildrenDto>> GetChildren(
        int volumeId,
        [FromQuery] int? directoryId,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 50,
        CancellationToken ct = default)
    {
        var volume = await _db.Volumes
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == volumeId, ct);

        if (volume is null)
            return NotFound();

        var paged = new PagedRequest(skip, take).Normalized();

        int parentId;
        string? parentPath;

        if (directoryId is null)
        {
            // Volume root: find the synthetic root node (MaterializedPath == "").
            var root = await _db.Directories
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.VolumeId == volumeId && d.MaterializedPath == string.Empty, ct);

            if (root is null)
            {
                // No index yet for this volume.
                return Ok(new CatalogChildrenDto([], EmptyPage(paged), volume.IsOnline, volume.Label, volume.LastDriveLetter, null, null));
            }

            parentId = root.Id;
            parentPath = null;
        }
        else
        {
            var dir = await _db.Directories
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == directoryId && d.VolumeId == volumeId, ct);

            if (dir is null)
                return NotFound();

            parentId = dir.Id;
            parentPath = dir.MaterializedPath;
        }

        // Sub-directories of the current node.
        var subDirs = await _db.Directories
            .AsNoTracking()
            .Where(d => d.VolumeId == volumeId && d.ParentId == parentId && d.IsMaterialized)
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.MaterializedPath,
                ChildCount = _db.Directories.Count(c => c.ParentId == d.Id && c.IsMaterialized),
                FileCount = _db.Files.Count(f => f.DirectoryId == d.Id && f.IsIncluded && f.IsPresent),
            })
            .ToListAsync(ct);

        var dirDtos = subDirs
            .Select(d => new CatalogDirDto(d.Id, d.Name, d.MaterializedPath, d.ChildCount, d.FileCount))
            .ToList();

        // Files in the current directory, paged.
        var filesQuery = _db.Files
            .AsNoTracking()
            .Where(f => f.DirectoryId == parentId && f.IsIncluded && f.IsPresent);

        var totalFiles = await filesQuery.CountAsync(ct);

        var filePage = await filesQuery
            .OrderBy(f => f.Name)
            .Skip(paged.Skip)
            .Take(paged.Take)
            .Select(f => new CatalogFileDto(f.Id, f.Name, f.SizeBytes, f.FileModifiedUtc, f.Category, "None"))
            .ToListAsync(ct);

        var pagedFiles = new PagedResult<CatalogFileDto>(filePage, totalFiles, paged.Skip, paged.Take);

        return Ok(new CatalogChildrenDto(dirDtos, pagedFiles, volume.IsOnline, volume.Label, volume.LastDriveLetter, directoryId, parentPath));
    }

    private static PagedResult<CatalogFileDto> EmptyPage(PagedRequest paged) =>
        new([], 0, paged.Skip, paged.Take);
}
```

> The sub-directory `ChildCount` and `FileCount` are scalar subqueries; EF Core 10 translates these to correlated SQL. With a shallow tree this is fine; if it becomes a hotspot in production, replace with a single grouped query.

- [ ] **Step 4.3: Build**

```
dotnet build src/backend/FileTracert.sln
```

Expected: 0 errors.

- [ ] **Step 4.4: Commit**

```
git add src/backend/FileTracert.Contracts/Dtos/CatalogDirDto.cs src/backend/FileTracert.Contracts/Dtos/CatalogFileDto.cs src/backend/FileTracert.Contracts/Dtos/CatalogChildrenDto.cs src/backend/FileTracert.Host/Controllers/CatalogController.cs
git commit -m "feat(host): GET /api/catalog/{volume}/children — lazy index tree, file pagination"
```

---

## Task 5: Backend tests

**Files:**
- Create: `tests/FileTracert.Tests/Data/FileSearchIndexTests.cs`
- Create: `tests/FileTracert.Tests/Host/SearchApiTests.cs`
- Create: `tests/FileTracert.Tests/Host/CatalogApiTests.cs`

- [ ] **Step 5.1: Write `FileSearchIndexTests.cs`**

This uses `SqliteInMemoryContext` + manual FTS5 table creation. Tests the pure Data layer without HTTP.

```csharp
// tests/FileTracert.Tests/Data/FileSearchIndexTests.cs
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

public sealed class FileSearchIndexTests
{
    // -----------------------------------------------------------------------
    // Test helpers
    // -----------------------------------------------------------------------

    private static async Task<(SqliteInMemoryContext Harness, FileTracertDbContext Ctx, FileSearchIndex Fts)> SetupAsync()
    {
        var harness = new SqliteInMemoryContext();
        var ctx = harness.CreateContext();

        // EnsureCreated builds EF tables but not virtual tables — create FTS5 manually.
        await ctx.Database.ExecuteSqlRawAsync("""
            CREATE VIRTUAL TABLE IF NOT EXISTS FileSearchIndex USING fts5(
                name,
                path,
                tokenize="unicode61 remove_diacritics 2 separators '\._-'"
            );
            """);

        return (harness, ctx, new FileSearchIndex(ctx));
    }

    private static async Task<(int VolumeId, int DirId)> SeedVolumeAndDirAsync(FileTracertDbContext ctx)
    {
        var volume = new Volume
        {
            VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync();

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        ctx.Directories.Add(root);
        await ctx.SaveChangesAsync();

        return (volume.Id, root.Id);
    }

    private static async Task<FileEntry> AddFileAsync(
        FileTracertDbContext ctx, int volumeId, int dirId, string name, string ext, FileCategory cat = FileCategory.Image)
    {
        var f = new FileEntry
        {
            VolumeId = volumeId,
            DirectoryId = dirId,
            Name = name,
            Extension = ext,
            Category = cat,
            SizeBytes = 1024,
            FileCreatedUtc = DateTime.UtcNow,
            FileModifiedUtc = DateTime.UtcNow,
            IsIncluded = true,
            IsPresent = true,
            LastIndexedUtc = DateTime.UtcNow,
        };
        ctx.Files.Add(f);
        await ctx.SaveChangesAsync();
        return f;
    }

    // -----------------------------------------------------------------------
    // SyncVolumeFromDbAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task SyncVolume_populates_fts_from_files()
    {
        using var (harness, ctx, fts) = await SetupAsync();
        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        var file = await AddFileAsync(ctx, volId, dirId, "vacation.jpg", "jpg");

        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var result = await fts.SearchAsync(
            new FileSearchQuery("vacation", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task SyncVolume_path_includes_directory()
    {
        using var (harness, ctx, fts) = await SetupAsync();
        var (volId, rootId) = await SeedVolumeAndDirAsync(ctx);

        var photos = new DirectoryNode
        {
            VolumeId = volId, ParentId = rootId, Name = "Photos",
            MaterializedPath = "Photos", IsMaterialized = true,
        };
        ctx.Directories.Add(photos);
        await ctx.SaveChangesAsync();

        var file = await AddFileAsync(ctx, volId, photos.Id, "city.jpg", "jpg");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        // Scope FullPath: searching for 'Photos' should find the file via path column.
        var result = await fts.SearchAsync(
            new FileSearchQuery("Photos", SearchScope.FullPath, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task SyncVolume_name_scope_does_not_match_directory_only()
    {
        using var (harness, ctx, fts) = await SetupAsync();
        var (volId, rootId) = await SeedVolumeAndDirAsync(ctx);

        var subDir = new DirectoryNode
        {
            VolumeId = volId, ParentId = rootId, Name = "UniqueFolder",
            MaterializedPath = "UniqueFolder", IsMaterialized = true,
        };
        ctx.Directories.Add(subDir);
        await ctx.SaveChangesAsync();

        var file = await AddFileAsync(ctx, volId, subDir.Id, "report.pdf", "pdf");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        // Scope Name: 'UniqueFolder' is only in the path, not the name → no match.
        var nameResult = await fts.SearchAsync(
            new FileSearchQuery("UniqueFolder", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        nameResult.TotalCount.Should().Be(0);

        // Scope FullPath: 'UniqueFolder' is in the path → match.
        var pathResult = await fts.SearchAsync(
            new FileSearchQuery("UniqueFolder", SearchScope.FullPath, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        pathResult.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task Search_accent_insensitive_prefix()
    {
        using var (harness, ctx, fts) = await SetupAsync();
        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        var file = await AddFileAsync(ctx, volId, dirId, "città.jpg", "jpg");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        // Search without accent should find accented filename.
        var result = await fts.SearchAsync(
            new FileSearchQuery("citta", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.Items.Should().Contain(file.Id);
    }

    [Fact]
    public async Task ClearVolume_removes_fts_entries()
    {
        using var (harness, ctx, fts) = await SetupAsync();
        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        await AddFileAsync(ctx, volId, dirId, "removeme.jpg", "jpg");
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        // Verify it's there first.
        var before = await fts.SearchAsync(
            new FileSearchQuery("removeme", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        before.TotalCount.Should().Be(1);

        await fts.ClearVolumeAsync(volId, CancellationToken.None);

        var after = await fts.SearchAsync(
            new FileSearchQuery("removeme", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        after.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task RebuildAsync_populates_all_volumes()
    {
        using var (harness, ctx, fts) = await SetupAsync();

        var (volId1, dirId1) = await SeedVolumeAndDirAsync(ctx);
        var (volId2, dirId2) = await SeedVolumeAndDirAsync(ctx);
        await AddFileAsync(ctx, volId1, dirId1, "alpha.jpg", "jpg");
        await AddFileAsync(ctx, volId2, dirId2, "beta.mp4", "mp4");

        await fts.RebuildAsync(CancellationToken.None);

        var alphaResult = await fts.SearchAsync(
            new FileSearchQuery("alpha", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);
        var betaResult = await fts.SearchAsync(
            new FileSearchQuery("beta", SearchScope.Name, null, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        alphaResult.TotalCount.Should().Be(1);
        betaResult.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Search_category_filter_narrows_results()
    {
        using var (harness, ctx, fts) = await SetupAsync();
        var (volId, dirId) = await SeedVolumeAndDirAsync(ctx);
        await AddFileAsync(ctx, volId, dirId, "photo.jpg", "jpg", FileCategory.Image);
        await AddFileAsync(ctx, volId, dirId, "photo.mp4", "mp4", FileCategory.Video);
        await fts.SyncVolumeFromDbAsync(volId, CancellationToken.None);

        var result = await fts.SearchAsync(
            new FileSearchQuery("photo", SearchScope.Name, FileCategory.Image, null, null, null, null, null, null, false, SearchSort.Relevance, false, 0, 10),
            CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
    }
}
```

> Note: `using var (harness, ctx, fts) = await SetupAsync()` uses C# value-tuple deconstruct. `SqliteInMemoryContext` implements `IDisposable`. The `FileTracertDbContext` is disposed when the harness is disposed.

- [ ] **Step 5.2: Write `SearchApiTests.cs`**

Uses `FileTracertAppFactory` which runs migrations (creates FTS5 table).

```csharp
// tests/FileTracert.Tests/Host/SearchApiTests.cs
using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

public sealed class SearchApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    private static FileTracertAppFactory SeedFactory(
        string volumeGuid,
        IEnumerable<(string Name, string Ext, FileCategory Cat, string DirPath)> files)
    {
        return new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var volume = new Volume
                {
                    VolumeGuid = volumeGuid,
                    Label = "Test",
                    FileSystem = "NTFS",
                    Kind = VolumeKind.Fixed,
                    IsCatalogable = true,
                    IsOnline = true,
                };
                db.Volumes.Add(volume);
                await db.SaveChangesAsync(ct);

                var rootDir = new DirectoryNode
                {
                    VolumeId = volume.Id,
                    Name = string.Empty,
                    MaterializedPath = string.Empty,
                    IsMaterialized = true,
                };
                db.Directories.Add(rootDir);
                await db.SaveChangesAsync(ct);

                var dirCache = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase)
                {
                    [string.Empty] = rootDir,
                };

                DirectoryNode EnsureDir(string path)
                {
                    if (dirCache.TryGetValue(path, out var existing)) return existing;
                    var parentPath = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : string.Empty;
                    var parent = EnsureDir(parentPath);
                    var node = new DirectoryNode
                    {
                        VolumeId = volume.Id,
                        Name = path.Contains('\\') ? path[(path.LastIndexOf('\\') + 1)..] : path,
                        MaterializedPath = path,
                        ParentId = parent.Id,
                        IsMaterialized = true,
                    };
                    db.Directories.Add(node);
                    db.SaveChanges();
                    dirCache[path] = node;
                    return node;
                }

                foreach (var (name, ext, cat, dirPath) in files)
                {
                    var dir = EnsureDir(dirPath);
                    db.Files.Add(new FileEntry
                    {
                        VolumeId = volume.Id,
                        DirectoryId = dir.Id,
                        Name = name,
                        Extension = ext,
                        Category = cat,
                        SizeBytes = 1024,
                        FileCreatedUtc = DateTime.UtcNow,
                        FileModifiedUtc = DateTime.UtcNow,
                        IsIncluded = true,
                        IsPresent = true,
                        LastIndexedUtc = DateTime.UtcNow,
                    });
                }
                await db.SaveChangesAsync(ct);

                // Sync FTS (backfill runs at startup but we want it after seed).
                using var scope = db.GetService<IServiceProvider>()
                    .GetService<IServiceProvider>()!
                    .CreateScope();
                // Use the factory's scope to get the FTS index.
            },
        };
    }

    [Fact]
    public async Task Search_finds_file_by_name()
    {
        var guid = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = guid, Label = "Disk", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                db.Files.Add(new FileEntry
                {
                    VolumeId = vol.Id, DirectoryId = root.Id,
                    Name = "vacation2024.jpg", Extension = "jpg", Category = FileCategory.Image,
                    SizeBytes = 2048, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                    IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);

                // Manually trigger FTS sync via the scoped service.
                using var scope = factory2.Services.CreateScope();
                // ↑ Can't reference factory2 here; use the IServiceProvider from the seed param.
                // Actually, `db` has a connection to the real DB; get IFileSearchIndex from it.
                var fts = new FileSearchIndex(db);
                await fts.SyncVolumeFromDbAsync(vol.Id, ct);
            },
        };
```

> **IMPORTANT NOTE:** The `Seed` delegate receives `FileTracertDbContext db`. To call `IFileSearchIndex.SyncVolumeFromDbAsync`, instantiate `FileSearchIndex` directly with `db`:
> ```csharp
> var fts = new FileSearchIndex(db);
> await fts.SyncVolumeFromDbAsync(vol.Id, ct);
> ```

Let me rewrite the test more cleanly:

```csharp
// tests/FileTracert.Tests/Host/SearchApiTests.cs
using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.Tests.Host;

public sealed class SearchApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    private static (FileTracertAppFactory Factory, string VolumeGuid) NewFactory(
        IEnumerable<(string Name, string Ext, FileCategory Cat)> files)
    {
        var guid = $@"\\?\Volume{{{Guid.NewGuid()}}}\";
        var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume
                {
                    VolumeGuid = guid, Label = "TestDisk", FileSystem = "NTFS",
                    Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true,
                };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);

                var root = new DirectoryNode
                {
                    VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true,
                };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                foreach (var (name, ext, cat) in files)
                {
                    db.Files.Add(new FileEntry
                    {
                        VolumeId = vol.Id, DirectoryId = root.Id,
                        Name = name, Extension = ext, Category = cat,
                        SizeBytes = 1024, FileCreatedUtc = DateTime.UtcNow,
                        FileModifiedUtc = DateTime.UtcNow,
                        IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                    });
                }
                await db.SaveChangesAsync(ct);

                // Populate FTS (the DatabaseInitializer backfill runs after seed, but direct call is cleaner).
                await new FileSearchIndex(db).SyncVolumeFromDbAsync(vol.Id, ct);
            },
        };
        return (factory, guid);
    }

    [Fact]
    public async Task Post_search_finds_file_by_name()
    {
        var (factory, _) = NewFactory([("holiday.jpg", "jpg", FileCategory.Image)]);
        using var _ = factory;
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "holiday", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 10));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await resp.Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>();
        page!.TotalCount.Should().Be(1);
        page.Items[0].Name.Should().Be("holiday.jpg");
    }

    [Fact]
    public async Task Post_search_category_filter_excludes_non_matching()
    {
        var (factory, _) = NewFactory([
            ("photo.jpg", "jpg", FileCategory.Image),
            ("movie.mp4", "mp4", FileCategory.Video),
        ]);
        using var _ = factory;
        var client = Authed(factory);

        // Search all with category=Image
        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "photo", SearchScope.Name, FileCategory.Image, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 10));

        var page = await resp.Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>();
        page!.TotalCount.Should().Be(1);
        page.Items.Should().OnlyContain(r => r.Category == FileCategory.Image);
    }

    [Fact]
    public async Task Post_search_paging_works()
    {
        var files = Enumerable.Range(1, 25)
            .Select(i => ($"file{i:D2}.jpg", "jpg", FileCategory.Image))
            .ToArray();
        var (factory, _) = NewFactory(files);
        using var _ = factory;
        var client = Authed(factory);

        // Search prefix 'file' → all 25 results
        var firstPage = await (await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "file", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Name, false, 0, 10))).Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>();

        firstPage!.TotalCount.Should().Be(25);
        firstPage.Items.Should().HaveCount(10);

        var secondPage = await (await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "file", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Name, false, 10, 10))).Content.ReadFromJsonAsync<PagedResult<SearchResultDto>>();

        secondPage!.Items.Should().HaveCount(10);
        // Pages should not overlap.
        firstPage.Items.Select(r => r.FileId)
            .Intersect(secondPage.Items.Select(r => r.FileId))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Post_search_returns_bad_request_for_empty_text()
    {
        using var factory = new FileTracertAppFactory { DisableVolumeSync = true, DisableScan = true };
        var client = Authed(factory);

        var resp = await client.PostAsJsonAsync("/api/search", new SearchRequest(
            "", SearchScope.Name, null, null, null, null, null, null, null, false,
            SearchSort.Relevance, false, 0, 10));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 5.3: Write `CatalogApiTests.cs`**

```csharp
// tests/FileTracert.Tests/Host/CatalogApiTests.cs
using System.Net;
using System.Net.Http.Json;
using FileTracert.Contracts.Dtos;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Host;

public sealed class CatalogApiTests
{
    private const string Header = "X-FileTracert-Token";

    private static HttpClient Authed(FileTracertAppFactory f)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add(Header, f.Token);
        return c;
    }

    [Fact]
    public async Task GetChildren_root_returns_subdirectories_and_files()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume
                {
                    VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
                    Label = "Cat Disk", FileSystem = "NTFS",
                    Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true,
                };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                var photos = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Photos", MaterializedPath = "Photos", IsMaterialized = true };
                var docs   = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Docs",   MaterializedPath = "Docs",   IsMaterialized = true };
                db.Directories.AddRange(photos, docs);
                await db.SaveChangesAsync(ct);

                db.Files.Add(new FileEntry
                {
                    VolumeId = vol.Id, DirectoryId = root.Id,
                    Name = "readme.txt", Extension = "txt", Category = FileCategory.Document,
                    SizeBytes = 100, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                    IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>();
        dto!.Directories.Should().HaveCount(2);
        dto.Directories.Select(d => d.Name).Should().BeEquivalentTo(["Docs", "Photos"]);
        dto.Files.TotalCount.Should().Be(1);
        dto.Files.Items[0].Name.Should().Be("readme.txt");
        dto.VolumeIsOnline.Should().BeTrue();
        dto.VolumeLabel.Should().Be("Cat Disk");
    }

    [Fact]
    public async Task GetChildren_subdirectory_returns_files_in_that_directory()
    {
        int volumeId = 0;
        int photoDirId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Disk2", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root   = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                var photos = new DirectoryNode { VolumeId = vol.Id, ParentId = root.Id, Name = "Photos", MaterializedPath = "Photos", IsMaterialized = true };
                db.Directories.Add(photos);
                await db.SaveChangesAsync(ct);
                photoDirId = photos.Id;

                db.Files.Add(new FileEntry
                {
                    VolumeId = vol.Id, DirectoryId = photos.Id,
                    Name = "beach.jpg", Extension = "jpg", Category = FileCategory.Image,
                    SizeBytes = 2048, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
                    IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children?directoryId={photoDirId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>();
        dto!.Files.TotalCount.Should().Be(1);
        dto.Files.Items[0].Name.Should().Be("beach.jpg");
        dto.CurrentDirectoryId.Should().Be(photoDirId);
        dto.CurrentDirectoryPath.Should().Be("Photos");
    }

    [Fact]
    public async Task GetChildren_excludes_non_present_files()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "Disk3", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = "present.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = 1, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow });
                db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = "deleted.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = 1, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = true, IsPresent = false, LastIndexedUtc = DateTime.UtcNow });
                db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = "excluded.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = 1, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = false, IsPresent = true, LastIndexedUtc = DateTime.UtcNow });
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var resp = await client.GetAsync($"/api/catalog/{volumeId}/children");
        var dto = await resp.Content.ReadFromJsonAsync<CatalogChildrenDto>();

        dto!.Files.TotalCount.Should().Be(1);
        dto.Files.Items[0].Name.Should().Be("present.jpg");
    }

    [Fact]
    public async Task GetChildren_unknown_volume_returns_404()
    {
        using var factory = new FileTracertAppFactory { DisableVolumeSync = true, DisableScan = true };
        var client = Authed(factory);

        var resp = await client.GetAsync("/api/catalog/9999/children");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetChildren_paging_files_works()
    {
        int volumeId = 0;
        using var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            Seed = async (db, ct) =>
            {
                var vol = new Volume { VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\", Label = "PagDisk", FileSystem = "NTFS", Kind = VolumeKind.Fixed, IsCatalogable = true, IsOnline = true };
                db.Volumes.Add(vol);
                await db.SaveChangesAsync(ct);
                volumeId = vol.Id;

                var root = new DirectoryNode { VolumeId = vol.Id, Name = "", MaterializedPath = "", IsMaterialized = true };
                db.Directories.Add(root);
                await db.SaveChangesAsync(ct);

                for (int i = 1; i <= 30; i++)
                {
                    db.Files.Add(new FileEntry { VolumeId = vol.Id, DirectoryId = root.Id, Name = $"img{i:D3}.jpg", Extension = "jpg", Category = FileCategory.Image, SizeBytes = i * 100, FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow, IsIncluded = true, IsPresent = true, LastIndexedUtc = DateTime.UtcNow });
                }
                await db.SaveChangesAsync(ct);
            },
        };
        var client = Authed(factory);

        var firstPage = await (await client.GetAsync($"/api/catalog/{volumeId}/children?skip=0&take=10")).Content.ReadFromJsonAsync<CatalogChildrenDto>();
        var secondPage = await (await client.GetAsync($"/api/catalog/{volumeId}/children?skip=10&take=10")).Content.ReadFromJsonAsync<CatalogChildrenDto>();

        firstPage!.Files.TotalCount.Should().Be(30);
        firstPage.Files.Items.Should().HaveCount(10);
        secondPage!.Files.Items.Should().HaveCount(10);

        firstPage.Files.Items.Select(f => f.Id)
            .Intersect(secondPage.Files.Items.Select(f => f.Id))
            .Should().BeEmpty();
    }
}
```

- [ ] **Step 5.4: Run all backend tests**

```
dotnet test tests/FileTracert.Tests/FileTracert.Tests.csproj --verbosity normal
```

Expected: All tests pass (green). Fix any failures before proceeding.

- [ ] **Step 5.5: Commit**

```
git add tests/FileTracert.Tests/Data/FileSearchIndexTests.cs tests/FileTracert.Tests/Host/SearchApiTests.cs tests/FileTracert.Tests/Host/CatalogApiTests.cs
git commit -m "test: FTS5 index + search API + catalog API integration tests"
```

---

## Task 6: Frontend — DTOs + API services + routes + nav

**Files:**
- Modify: `src/frontend/src/app/core/models/catalog.models.ts`
- Create: `src/frontend/src/app/core/api/search-api.service.ts`
- Create: `src/frontend/src/app/core/api/catalog-api.service.ts`
- Modify: `src/frontend/src/app/app.routes.ts`
- Modify: `src/frontend/src/app/app.html`

- [ ] **Step 6.1: Add DTOs to `catalog.models.ts`**

Append at end of `src/frontend/src/app/core/models/catalog.models.ts`:

```typescript
// ---- Step 7: Search + Catalog ----

export type SearchScope = 'Name' | 'FullPath';
export type SearchSort = 'Relevance' | 'Name' | 'Date' | 'Size';
export type FileCategory = 'Image' | 'Video' | 'Audio' | 'Document' | 'Archive' | 'Other';

export interface SearchRequest {
  text: string;
  scope: SearchScope;
  category: FileCategory | null;
  extensions: string[] | null;
  sizeBytesMin: number | null;
  sizeBytesMax: number | null;
  modifiedFrom: string | null;
  modifiedTo: string | null;
  volumeId: number | null;
  onlineOnly: boolean;
  sort: SearchSort;
  desc: boolean;
  skip: number;
  take: number;
}

export interface SearchResultDto {
  fileId: number;
  name: string;
  relativePath: string;
  volumeId: number;
  volumeLabel: string | null;
  volumeLetter: string | null;
  volumeIsOnline: boolean;
  sizeBytes: number;
  modifiedUtc: string;
  category: FileCategory;
  projectedState: string;
}

export interface CatalogDirDto {
  id: number;
  name: string;
  materializedPath: string;
  childDirectoryCount: number;
  fileCount: number;
}

export interface CatalogFileDto {
  id: number;
  name: string;
  sizeBytes: number;
  modifiedUtc: string;
  category: FileCategory;
  projectedState: string;
}

export interface CatalogChildrenDto {
  directories: CatalogDirDto[];
  files: PagedResult<CatalogFileDto>;
  volumeIsOnline: boolean;
  volumeLabel: string | null;
  volumeLetter: string | null;
  currentDirectoryId: number | null;
  currentDirectoryPath: string | null;
}
```

- [ ] **Step 6.2: Create `search-api.service.ts`**

```typescript
// src/frontend/src/app/core/api/search-api.service.ts
import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResult, SearchRequest, SearchResultDto } from '../models/catalog.models';

@Injectable({ providedIn: 'root' })
export class SearchApi {
  private readonly http = inject(HttpClient);

  search(req: SearchRequest): Observable<PagedResult<SearchResultDto>> {
    return this.http.post<PagedResult<SearchResultDto>>('/api/search', req);
  }
}
```

- [ ] **Step 6.3: Create `catalog-api.service.ts`**

```typescript
// src/frontend/src/app/core/api/catalog-api.service.ts
import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { CatalogChildrenDto } from '../models/catalog.models';

@Injectable({ providedIn: 'root' })
export class CatalogApi {
  private readonly http = inject(HttpClient);

  children(
    volumeId: number,
    directoryId: number | null,
    skip = 0,
    take = 50,
  ): Observable<CatalogChildrenDto> {
    let params = new HttpParams().set('skip', skip).set('take', take);
    if (directoryId !== null) params = params.set('directoryId', directoryId);
    return this.http.get<CatalogChildrenDto>(`/api/catalog/${volumeId}/children`, { params });
  }
}
```

- [ ] **Step 6.4: Add routes**

In `src/frontend/src/app/app.routes.ts`, add before the wildcard route:

```typescript
{
  path: 'catalog',
  title: 'Catalogo · FileTracert',
  loadComponent: () => import('./features/catalog/catalog').then((m) => m.Catalog),
},
{
  path: 'search',
  title: 'Ricerca · FileTracert',
  loadComponent: () => import('./features/search/search').then((m) => m.Search),
},
```

- [ ] **Step 6.5: Activate nav links in `app.html`**

Replace the two "presto" placeholder items with real links:

```html
<!-- Replace: -->
<div class="navitem soon"><span class="ic">▦</span> Catalogo <span class="qbadge mut">presto</span></div>
<div class="navitem soon"><span class="ic">⌕</span> Ricerca <span class="qbadge mut">presto</span></div>

<!-- With: -->
<a class="navitem" routerLink="/catalog" routerLinkActive="active">
  <span class="ic">▦</span> Catalogo
</a>
<a class="navitem" routerLink="/search" routerLinkActive="active">
  <span class="ic">⌕</span> Ricerca
</a>
```

- [ ] **Step 6.6: Build frontend**

```
cd src/frontend && npx ng build --configuration development
```

Expected: 0 errors. (Routes will 404 until components exist — that's OK at this step.)

---

## Task 7: Frontend — SearchStore + Search component

> ⚠️ **UI SKILL REQUIRED:** Invoke the `impeccable` skill before writing the Search component HTML/SCSS. The component must match `DESIGN.md` / `filetracert-mockup.html`.

**Files:**
- Create: `src/frontend/src/app/features/search/search.store.ts`
- Create: `src/frontend/src/app/features/search/search.ts`
- Create: `src/frontend/src/app/features/search/search.html`
- Create: `src/frontend/src/app/features/search/search.scss`
- Create: `src/frontend/src/app/features/search/search.store.spec.ts`

- [ ] **Step 7.1: Write `search.store.ts`**

```typescript
// src/frontend/src/app/features/search/search.store.ts
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { SearchApi } from '../../core/api/search-api.service';
import {
  FileCategory, PagedResult, SearchRequest,
  SearchResultDto, SearchScope, SearchSort,
} from '../../core/models/catalog.models';

interface SearchFilters {
  category: FileCategory | null;
  extensions: string[] | null;
  sizeBytesMin: number | null;
  sizeBytesMax: number | null;
  modifiedFrom: string | null;
  modifiedTo: string | null;
  volumeId: number | null;
  onlineOnly: boolean;
}

interface SearchState {
  text: string;
  scope: SearchScope;
  sort: SearchSort;
  desc: boolean;
  filters: SearchFilters;
  results: PagedResult<SearchResultDto> | null;
  loading: boolean;
  error: string | null;
  currentSkip: number;
  take: number;
}

const defaultFilters: SearchFilters = {
  category: null,
  extensions: null,
  sizeBytesMin: null,
  sizeBytesMax: null,
  modifiedFrom: null,
  modifiedTo: null,
  volumeId: null,
  onlineOnly: false,
};

const initial: SearchState = {
  text: '',
  scope: 'Name',
  sort: 'Relevance',
  desc: false,
  filters: defaultFilters,
  results: null,
  loading: false,
  error: null,
  currentSkip: 0,
  take: 50,
};

export const SearchStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    hasResults: computed(() => (store.results()?.totalCount ?? 0) > 0),
    totalCount: computed(() => store.results()?.totalCount ?? 0),
    isCapped: computed(() => store.results()?.totalCount === 10000),
  })),
  withMethods((store, api = inject(SearchApi)) => {
    function buildRequest(skip: number): SearchRequest {
      const s = store;
      return {
        text: s.text(),
        scope: s.scope(),
        sort: s.sort(),
        desc: s.desc(),
        skip,
        take: s.take(),
        ...s.filters(),
      };
    }

    async function doSearch(skip: number): Promise<void> {
      const text = store.text().trim();
      if (!text) return;
      patchState(store, { loading: true, error: null, currentSkip: skip });
      try {
        const results = await firstValueFrom(api.search(buildRequest(skip)));
        patchState(store, { results, loading: false });
      } catch (e) {
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      setQuery(text: string): void {
        patchState(store, { text });
      },
      setScope(scope: SearchScope): void {
        patchState(store, { scope });
      },
      setSort(sort: SearchSort, desc = false): void {
        patchState(store, { sort, desc });
      },
      setFilters(filters: Partial<SearchFilters>): void {
        patchState(store, { filters: { ...store.filters(), ...filters } });
      },
      clearFilters(): void {
        patchState(store, { filters: defaultFilters });
      },
      async search(): Promise<void> {
        await doSearch(0);
      },
      async loadPage(skip: number): Promise<void> {
        await doSearch(skip);
      },
      clear(): void {
        patchState(store, { text: '', results: null, error: null, currentSkip: 0 });
      },
    };
  }),
);
```

- [ ] **Step 7.2: Invoke `impeccable` skill, then write `search.ts` / `search.html` / `search.scss`**

**Invoke the `impeccable` skill before writing the component.** Brief for impeccable:
- Screen: Ricerca (File Search)
- Design system: `DESIGN.md`, `filetracert-mockup.html`, dark theme, IBM Plex Sans, teal `#2ec4b6` / lime `#a8e063`
- Layout: top bar with search input + scope toggle (Nome / Path completo) + chip filters, results table below with CDK Virtual Scroll
- Chip filters: Categoria (Image/Video/Audio/Document/Archive), Solo online, by Volume
- Sort selector: Rilevanza / Nome / Data / Dimensione
- Results table columns: Nome, Percorso, Volume (pill: online/offline), Dimensione, Data
- Status states: empty, loading spinner, zero results, results (total count, capped note "10 000+" if total == 10000)
- Reuse shared: `ft-panel`, `ft-pill`, `BytesPipe`, `RelativeTimePipe`

Component shell (`search.ts`):

```typescript
// src/frontend/src/app/features/search/search.ts
import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { SearchStore } from './search.store';
import { VolumesStore } from '../volumes/volumes.store';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FileCategory, SearchScope, SearchSort } from '../../core/models/catalog.models';

@Component({
  selector: 'ft-search',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, ScrollingModule, BytesPipe, RelativeTimePipe, FtPill, FtPanel],
  templateUrl: './search.html',
  styleUrl: './search.scss',
})
export class Search implements OnInit {
  protected readonly store = inject(SearchStore);
  protected readonly volumes = inject(VolumesStore);

  // Two-way bound to the text input
  protected queryText = '';

  protected readonly CATEGORIES: FileCategory[] = ['Image', 'Video', 'Audio', 'Document', 'Archive'];
  protected readonly SORTS: { value: SearchSort; label: string }[] = [
    { value: 'Relevance', label: 'Rilevanza' },
    { value: 'Name', label: 'Nome' },
    { value: 'Date', label: 'Data' },
    { value: 'Size', label: 'Dimensione' },
  ];

  ngOnInit(): void {
    this.volumes.loadList();
  }

  protected onSubmit(): void {
    this.store.setQuery(this.queryText);
    void this.store.search();
  }

  protected setScope(scope: SearchScope): void {
    this.store.setScope(scope);
  }

  protected setSort(sort: SearchSort): void {
    this.store.setSort(sort, sort === this.store.sort() && !this.store.desc());
  }

  protected toggleCategory(cat: FileCategory): void {
    const current = this.store.filters().category;
    this.store.setFilters({ category: current === cat ? null : cat });
    if (this.store.text()) void this.store.search();
  }

  protected toggleOnlineOnly(): void {
    this.store.setFilters({ onlineOnly: !this.store.filters().onlineOnly });
    if (this.store.text()) void this.store.search();
  }

  protected goToPage(skip: number): void {
    void this.store.loadPage(skip);
  }

  protected catIcon(cat: FileCategory): string {
    const icons: Record<FileCategory, string> = {
      Image: '🖼', Video: '🎬', Audio: '🎵', Document: '📄', Archive: '🗜', Other: '📁',
    };
    return icons[cat] ?? '📁';
  }

  protected catLabel(cat: FileCategory): string {
    const labels: Record<FileCategory, string> = {
      Image: 'Immagini', Video: 'Video', Audio: 'Audio',
      Document: 'Documenti', Archive: 'Archivi', Other: 'Altro',
    };
    return labels[cat] ?? cat;
  }
}
```

> The actual `search.html` and `search.scss` must be produced by the `impeccable` skill to ensure design quality. Invoke it with the brief above before writing those two files.

- [ ] **Step 7.3: Write `search.store.spec.ts`**

```typescript
// src/frontend/src/app/features/search/search.store.spec.ts
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { SearchApi } from '../../core/api/search-api.service';
import { PagedResult, SearchResultDto } from '../../core/models/catalog.models';
import { SearchStore } from './search.store';

const mockResult: PagedResult<SearchResultDto> = {
  items: [
    {
      fileId: 1, name: 'photo.jpg', relativePath: 'Photos\\photo.jpg',
      volumeId: 1, volumeLabel: 'Disk', volumeLetter: 'D:', volumeIsOnline: true,
      sizeBytes: 1024, modifiedUtc: '2026-01-01T00:00:00Z',
      category: 'Image', projectedState: 'None',
    },
  ],
  totalCount: 1,
  skip: 0,
  take: 50,
};

function setup(apiMock: Partial<SearchApi> = {}) {
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: SearchApi, useValue: { search: vi.fn(() => of(mockResult)), ...apiMock } },
    ],
  });
  return TestBed.inject(SearchStore);
}

describe('SearchStore', () => {
  it('initialises with empty state', () => {
    const store = setup();
    expect(store.text()).toBe('');
    expect(store.results()).toBeNull();
    expect(store.loading()).toBe(false);
  });

  it('setQuery updates text signal', () => {
    const store = setup();
    store.setQuery('holiday');
    expect(store.text()).toBe('holiday');
  });

  it('search calls API and populates results', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    expect(store.results()?.totalCount).toBe(1);
    expect(store.results()?.items[0].name).toBe('photo.jpg');
  });

  it('search with empty text is a no-op', async () => {
    const searchSpy = vi.fn(() => of(mockResult));
    const store = setup({ search: searchSpy });
    await store.search(); // text is ''
    expect(searchSpy).not.toHaveBeenCalled();
  });

  it('setFilters merges partial filter state', () => {
    const store = setup();
    store.setFilters({ category: 'Image' });
    expect(store.filters().category).toBe('Image');
    expect(store.filters().onlineOnly).toBe(false); // other fields untouched
  });

  it('setScope changes scope signal', () => {
    const store = setup();
    store.setScope('FullPath');
    expect(store.scope()).toBe('FullPath');
  });

  it('isCapped is true when totalCount equals 10000', async () => {
    const cappedResult: PagedResult<SearchResultDto> = { ...mockResult, totalCount: 10000 };
    const store = setup({ search: vi.fn(() => of(cappedResult)) });
    store.setQuery('x');
    await store.search();
    expect(store.isCapped()).toBe(true);
  });

  it('error state populated on API failure', async () => {
    const store = setup({ search: vi.fn(() => throwError(() => new Error('Network error'))) });
    store.setQuery('x');
    await store.search();
    expect(store.error()).toBe('Network error');
    expect(store.loading()).toBe(false);
  });

  it('clear resets text and results', async () => {
    const store = setup();
    store.setQuery('photo');
    await store.search();
    store.clear();
    expect(store.text()).toBe('');
    expect(store.results()).toBeNull();
  });
});
```

- [ ] **Step 7.4: Run frontend tests**

```
cd src/frontend && npx vitest run src/app/features/search/search.store.spec.ts
```

Expected: All 9 tests pass.

---

## Task 8: Frontend — CatalogStore + Catalog component

> ⚠️ **UI SKILL REQUIRED:** Invoke the `impeccable` skill before writing the Catalog component HTML/SCSS.

**Files:**
- Create: `src/frontend/src/app/features/catalog/catalog.store.ts`
- Create: `src/frontend/src/app/features/catalog/catalog.ts`
- Create: `src/frontend/src/app/features/catalog/catalog.html`
- Create: `src/frontend/src/app/features/catalog/catalog.scss`
- Create: `src/frontend/src/app/features/catalog/catalog.store.spec.ts`

- [ ] **Step 8.1: Write `catalog.store.ts`**

```typescript
// src/frontend/src/app/features/catalog/catalog.store.ts
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';

import { CatalogApi } from '../../core/api/catalog-api.service';
import { CatalogChildrenDto, VolumeDto } from '../../core/models/catalog.models';

interface Breadcrumb {
  id: number | null;
  name: string;
  path: string | null;
}

interface CatalogState {
  selectedVolume: VolumeDto | null;
  breadcrumbs: Breadcrumb[];
  children: CatalogChildrenDto | null;
  loading: boolean;
  error: string | null;
  fileSkip: number;
  fileTake: number;
}

const initial: CatalogState = {
  selectedVolume: null,
  breadcrumbs: [],
  children: null,
  loading: false,
  error: null,
  fileSkip: 0,
  fileTake: 50,
};

export const CatalogStore = signalStore(
  { providedIn: 'root' },
  withState(initial),
  withComputed((store) => ({
    currentDirId: computed(() => {
      const crumbs = store.breadcrumbs();
      return crumbs.length > 0 ? crumbs[crumbs.length - 1].id : null;
    }),
    volumeIsOnline: computed(() => store.children()?.volumeIsOnline ?? false),
    totalFiles: computed(() => store.children()?.files.totalCount ?? 0),
    canGoUp: computed(() => store.breadcrumbs().length > 0),
  })),
  withMethods((store, api = inject(CatalogApi)) => {
    async function loadChildren(dirId: number | null, fileSkip: number): Promise<void> {
      const vol = store.selectedVolume();
      if (!vol) return;
      patchState(store, { loading: true, error: null });
      try {
        const children = await firstValueFrom(api.children(vol.id, dirId, fileSkip, store.fileTake()));
        patchState(store, { children, fileSkip, loading: false });
      } catch (e) {
        patchState(store, { error: (e as Error).message, loading: false });
      }
    }

    return {
      async selectVolume(volume: VolumeDto): Promise<void> {
        patchState(store, {
          selectedVolume: volume,
          breadcrumbs: [],
          children: null,
          fileSkip: 0,
        });
        await loadChildren(null, 0);
      },

      async openDirectory(dirId: number, name: string, path: string): Promise<void> {
        const crumbs = [...store.breadcrumbs(), { id: dirId, name, path }];
        patchState(store, { breadcrumbs: crumbs, fileSkip: 0 });
        await loadChildren(dirId, 0);
      },

      async navigateTo(index: number): Promise<void> {
        // index -1 = volume root; 0..n = breadcrumb index
        if (index < 0) {
          patchState(store, { breadcrumbs: [], fileSkip: 0 });
          await loadChildren(null, 0);
        } else {
          const crumbs = store.breadcrumbs().slice(0, index + 1);
          const dirId = crumbs[crumbs.length - 1]?.id ?? null;
          patchState(store, { breadcrumbs: crumbs, fileSkip: 0 });
          await loadChildren(dirId, 0);
        }
      },

      async loadFilePage(skip: number): Promise<void> {
        await loadChildren(store.currentDirId(), skip);
      },

      clear(): void {
        patchState(store, { ...initial });
      },
    };
  }),
);
```

- [ ] **Step 8.2: Invoke `impeccable` skill, then write `catalog.ts` / `catalog.html` / `catalog.scss`**

**Invoke the `impeccable` skill before writing the component.** Brief for impeccable:
- Screen: Catalogo (Catalog Browser)
- Design system: dark theme, IBM Plex Sans, `filetracert-mockup.html` reference
- Layout: left column = volume selector list (from VolumesStore); right = breadcrumb + dir grid + file list
- Breadcrumb: Volume name → Dir1 → Dir2 (each clickable to navigate up)
- Dir grid: cards with folder icon, name, child count, file count; click → openDirectory
- File list: CDK Virtual Scroll, columns: Nome, Categoria (pill), Dimensione, Data modifica
- Status pill on volume: online (teal) / offline (amber)
- Stale indicator if volume is offline
- "Sola lettura" note (no actions yet)
- Reuse: `ft-panel`, `ft-pill`, `BytesPipe`, `RelativeTimePipe`

Component shell (`catalog.ts`):

```typescript
// src/frontend/src/app/features/catalog/catalog.ts
import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { ScrollingModule } from '@angular/cdk/scrolling';

import { CatalogStore } from './catalog.store';
import { VolumesStore } from '../volumes/volumes.store';
import { BytesPipe } from '../../shared/pipes/bytes.pipe';
import { RelativeTimePipe } from '../../shared/pipes/relative-time.pipe';
import { FtPill } from '../../shared/components/ft-pill/ft-pill';
import { FtPanel } from '../../shared/components/ft-panel/ft-panel';
import { FileCategory } from '../../core/models/catalog.models';

const CATEGORY_LABELS: Record<FileCategory, string> = {
  Image: 'Immagine', Video: 'Video', Audio: 'Audio',
  Document: 'Documento', Archive: 'Archivio', Other: 'Altro',
};

@Component({
  selector: 'ft-catalog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ScrollingModule, BytesPipe, RelativeTimePipe, FtPill, FtPanel],
  templateUrl: './catalog.html',
  styleUrl: './catalog.scss',
})
export class Catalog implements OnInit {
  protected readonly store = inject(CatalogStore);
  protected readonly volumes = inject(VolumesStore);

  ngOnInit(): void {
    this.volumes.loadList();
  }

  protected selectVolume(volumeId: number): void {
    const vol = this.volumes.volumes().find((v) => v.id === volumeId);
    if (vol) void this.store.selectVolume(vol);
  }

  protected openDir(id: number, name: string, path: string): void {
    void this.store.openDirectory(id, name, path);
  }

  protected navigateTo(index: number): void {
    void this.store.navigateTo(index);
  }

  protected loadFilePage(skip: number): void {
    void this.store.loadFilePage(skip);
  }

  protected catLabel(cat: FileCategory): string {
    return CATEGORY_LABELS[cat] ?? cat;
  }
}
```

> The actual `catalog.html` and `catalog.scss` must be produced by the `impeccable` skill. Invoke it with the brief above before writing those two files.

- [ ] **Step 8.3: Write `catalog.store.spec.ts`**

```typescript
// src/frontend/src/app/features/catalog/catalog.store.spec.ts
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
import { vi } from 'vitest';

import { CatalogApi } from '../../core/api/catalog-api.service';
import { VolumesApi } from '../../core/api/volumes-api.service';
import { CatalogChildrenDto, VolumeDto } from '../../core/models/catalog.models';
import { CatalogStore } from './catalog.store';

const mockVolume: VolumeDto = {
  id: 1, volumeGuid: '\\\\?\\Volume{x}\\', label: 'SSD', currentLetter: 'D:',
  fileSystem: 'NTFS', isRemovable: false, isOnline: true, lastSeenUtc: '2026-01-01T00:00:00Z',
  capacityBytes: 500_000_000_000, freeBytes: 200_000_000_000, fileCount: 1000,
  lastFullScanUtc: '2026-01-01T00:00:00Z', dataIsLive: true, isStale: false,
  kind: 'Fixed', isCatalogable: true,
};

const rootChildren: CatalogChildrenDto = {
  directories: [
    { id: 10, name: 'Photos', materializedPath: 'Photos', childDirectoryCount: 2, fileCount: 50 },
  ],
  files: { items: [], totalCount: 0, skip: 0, take: 50 },
  volumeIsOnline: true,
  volumeLabel: 'SSD',
  volumeLetter: 'D:',
  currentDirectoryId: null,
  currentDirectoryPath: null,
};

const photoChildren: CatalogChildrenDto = {
  directories: [],
  files: {
    items: [{ id: 1, name: 'beach.jpg', sizeBytes: 2048, modifiedUtc: '2026-01-01T00:00:00Z', category: 'Image', projectedState: 'None' }],
    totalCount: 1,
    skip: 0,
    take: 50,
  },
  volumeIsOnline: true,
  volumeLabel: 'SSD',
  volumeLetter: 'D:',
  currentDirectoryId: 10,
  currentDirectoryPath: 'Photos',
};

function setup(childrenFn: (volId: number, dirId: number | null) => CatalogChildrenDto = () => rootChildren) {
  const childrenSpy = vi.fn((volId: number, dirId: number | null) => of(childrenFn(volId, dirId)));
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: CatalogApi, useValue: { children: childrenSpy } },
      { provide: VolumesApi, useValue: { list: () => of([mockVolume]), detail: () => of(null), rescan: () => of(null), setCatalogable: () => of(null) } },
    ],
  });
  return { store: TestBed.inject(CatalogStore), childrenSpy };
}

describe('CatalogStore', () => {
  it('initial state is empty', () => {
    const { store } = setup();
    expect(store.selectedVolume()).toBeNull();
    expect(store.breadcrumbs()).toHaveLength(0);
    expect(store.children()).toBeNull();
  });

  it('selectVolume loads root children', async () => {
    const { store, childrenSpy } = setup();
    await store.selectVolume(mockVolume);

    expect(store.selectedVolume()?.id).toBe(1);
    expect(store.children()?.directories).toHaveLength(1);
    expect(store.breadcrumbs()).toHaveLength(0);
    expect(childrenSpy).toHaveBeenCalledWith(1, null, 0, 50);
  });

  it('openDirectory pushes breadcrumb and loads children', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);

    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');

    expect(store.breadcrumbs()).toHaveLength(1);
    expect(store.breadcrumbs()[0].name).toBe('Photos');
    expect(store.children()?.files.totalCount).toBe(1);
  });

  it('navigateTo(-1) returns to root', async () => {
    const { store, childrenSpy } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);

    await store.selectVolume(mockVolume);
    await store.openDirectory(10, 'Photos', 'Photos');
    await store.navigateTo(-1);

    expect(store.breadcrumbs()).toHaveLength(0);
    expect(store.currentDirId()).toBeNull();
  });

  it('canGoUp is false at root, true in subdirectory', async () => {
    const { store } = setup((_v, dirId) => dirId === 10 ? photoChildren : rootChildren);

    await store.selectVolume(mockVolume);
    expect(store.canGoUp()).toBe(false);

    await store.openDirectory(10, 'Photos', 'Photos');
    expect(store.canGoUp()).toBe(true);
  });

  it('error state populated on API failure', async () => {
    const errSpy = vi.fn(() => throwError(() => new Error('offline')));
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        { provide: CatalogApi, useValue: { children: errSpy } },
        { provide: VolumesApi, useValue: { list: () => of([mockVolume]), detail: () => of(null), rescan: () => of(null), setCatalogable: () => of(null) } },
      ],
    });
    const store = TestBed.inject(CatalogStore);
    await store.selectVolume(mockVolume);
    expect(store.error()).toBe('offline');
    expect(store.loading()).toBe(false);
  });
});
```

- [ ] **Step 8.4: Run frontend tests**

```
cd src/frontend && npx vitest run src/app/features/catalog/catalog.store.spec.ts
```

Expected: All 6 tests pass.

- [ ] **Step 8.5: Run all frontend tests**

```
cd src/frontend && npx vitest run
```

Expected: All tests pass (including pre-existing ones).

- [ ] **Step 8.6: Build frontend production**

```
cd src/frontend && npx ng build --configuration development
```

Expected: 0 errors.

- [ ] **Step 8.7: Commit**

```
git add src/frontend/src/app/core/ src/frontend/src/app/features/catalog/ src/frontend/src/app/features/search/ src/frontend/src/app/app.routes.ts src/frontend/src/app/app.html src/frontend/src/app/core/models/catalog.models.ts
git commit -m "feat(frontend): SearchStore + CatalogStore + Ricerca + Catalogo screens"
```

---

## Task 9: Final verification

- [ ] **Step 9.1: Run all backend tests**

```
dotnet test tests/FileTracert.Tests/FileTracert.Tests.csproj --verbosity normal
```

Expected: All tests pass.

- [ ] **Step 9.2: Check for warnings-as-errors**

```
dotnet build src/backend/FileTracert.sln -warnaserror
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 9.3: Start app and smoke-test** (optional but recommended)

```
dotnet run --project src/backend/FileTracert.Host
```

Navigate to `http://localhost:5174/catalog` and `http://localhost:5174/search`. Verify:
- Volume list appears in Catalog left panel
- Selecting a catalogued volume shows directories and files from the index
- Search returns results for a known file name (e.g., search for a file extension like `jpg`)
- Scope toggle works (Name vs FullPath returns different result sets)

- [ ] **Step 9.4: Commit if anything was fixed in this step**

---

## Acceptance Criteria Verification

After all tasks complete, verify each criterion from the spec:

- [ ] FTS5 (`name`+`path`, rowid=Files.Id) created in migration raw; tokenizer unicode61 accent-insensitive; prefix queries work.
- [ ] `IFileSearchIndex` in Contracts, impl in Data; sync explicit (no triggers); `RebuildAsync` backfills existing files.
- [ ] Search with scope toggle (Name/FullPath); combined filters; server-side paging; sort (Relevance default); traverses offline volumes.
- [ ] Catalog: lazy tree on the index, paginated files, works offline, virtual scroll; read-only with `projectedState="None"`.
- [ ] Nav links Catalogo/Ricerca active; design follows `DESIGN.md` (impeccable skill used for UI).
- [ ] COUNT capped at 10 000 (documented in comments; UI shows "10 000+" when `isCapped()`).
- [ ] All tests green; build with warnings-as-errors clean.

---

## Notes for Subsequent Steps

- **Step 8 (Coda):** The `projectedState: "None"` placeholder in both search results and catalog files will be replaced with real state when the queue overlay is implemented.
- **Step 9 (Projection):** `SyncVolumeFromDbAsync` currently syncs `f.Name` (actual name). Step 9 must update FTS sync to use `PendingName ?? Name` (projected name). Also, the truncate-per-volume re-scan (`PersistAsync`) clears `Pending*` fields — this is the documented `ScanService` vs projection debt (§11 CLAUDE.md); fix in step 9 before completing the projection model.
- **FTS5 content limitation:** The `unicode61` tokenizer does not support infix (substring) search. Only prefix + full-token matches work. If "contains" search is needed later, evaluate the `trigram` tokenizer as a replacement. Noted in task A3 of the spec.
