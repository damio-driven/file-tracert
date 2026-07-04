using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// C4: the queue's growth tables must carry the indexes the hot queries rely on —
/// <c>OperationJobItems.FileId</c> (per-file enqueue guard) and
/// <c>Directories.MaterializedPath</c> (subtree prefix queries). The schema is built from the
/// EF model here (EnsureCreated), which is the same model the migration materializes.
/// </summary>
public sealed class QueuePerfIndexTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness = new();

    public void Dispose() => _harness.Dispose();

    private List<string> IndexNames()
    {
        using var db = _harness.CreateContext();
        return db.Database
            .SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index'")
            .ToList();
    }

    [Fact]
    public void OperationJobItems_is_indexed_on_FileId()
    {
        IndexNames().Should().Contain("IX_OperationJobItems_FileId");
    }

    [Fact]
    public void Directories_is_indexed_on_MaterializedPath()
    {
        IndexNames().Should().Contain("IX_Directories_MaterializedPath");
    }
}
