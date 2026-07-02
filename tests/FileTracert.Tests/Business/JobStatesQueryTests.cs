using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

/// <summary>
/// Guards the queue processor's "pick next runnable job" query against real SQLite.
/// It regressed with <c>IReadOnlySet&lt;JobState&gt;.Contains</c>, which EF Core cannot
/// translate — the worker threw on every loop and never processed the queue.
/// </summary>
public sealed class JobStatesQueryTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;

    public JobStatesQueryTests()
    {
        _harness = new SqliteInMemoryContext();
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        foreach (var state in Enum.GetValues<JobState>())
        {
            setup.OperationJobs.Add(new OperationJob
            {
                Type = JobType.CreateFolder, State = state,
                SequenceOrder = (int)state, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            });
        }
        setup.SaveChanges();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Runnable_filter_translates_to_sql_and_returns_only_runnable_jobs()
    {
        await using var db = _harness.CreateContext();

        // Must translate to SQL (no InvalidOperationException) and return exactly the runnable set.
        var states = await db.OperationJobs
            .Where(j => JobStates.Runnable.Contains(j.State))
            .OrderBy(j => j.SequenceOrder)
            .Select(j => j.State)
            .ToListAsync();

        states.Should().BeEquivalentTo(new[]
        {
            JobState.Pending, JobState.SpaceReserved, JobState.Copying,
            JobState.Verifying, JobState.DeletingSource,
        });
    }
}
