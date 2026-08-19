using FileTracert.Business.Dashboard;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Business;

public sealed class DashboardStatsAssemblerTests
{
    [Fact]
    public void Passes_the_index_totals_and_the_queue_totals_through()
    {
        var dto = DashboardStatsAssembler.From(
            totalFiles: 1_214_882, totalBytes: 3_400_000_000_000, volumesOnline: 3, volumesTotal: 4,
            new QueueTotals(QueuedJobs: 9, BlockedJobs: 4, RunningJobs: 1, PendingBytes: 700));

        dto.TotalFiles.Should().Be(1_214_882);
        dto.TotalBytes.Should().Be(3_400_000_000_000);
        dto.VolumesOnline.Should().Be(3);
        dto.VolumesTotal.Should().Be(4);
        dto.QueuedJobs.Should().Be(9);
        dto.BlockedJobs.Should().Be(4);
        dto.RunningJobs.Should().Be(1);
        dto.PendingBytes.Should().Be(700);
    }
}

/// <summary>
/// C30 — the Dashboard used to hard-code four zeros ("placeholder step 8") while the Coda
/// screen listed real jobs. Against a real SQLite database, because the whole point is that
/// the numbers come from the table.
/// </summary>
public sealed class QueueTotalsTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness = new();

    public QueueTotalsTests()
    {
        using var setup = _harness.CreateContext();
        setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
    }

    public void Dispose() => _harness.Dispose();

    private void Seed(params OperationJob[] jobs)
    {
        using var db = _harness.CreateContext();
        db.OperationJobs.AddRange(jobs);
        db.SaveChanges();
    }

    private static OperationJob Job(
        int sequence, JobState state,
        JobBlockReason reason = JobBlockReason.None,
        long total = 0, long processed = 0) => new()
        {
            Type = JobType.MoveFile,
            State = state,
            BlockReason = reason,
            SequenceOrder = sequence,
            TotalBytes = total,
            BytesProcessed = processed,
        };

    private async Task<QueueTotals> ComputeAsync()
    {
        using var db = _harness.CreateContext();
        return await QueueTotals.ComputeAsync(db.OperationJobs.AsNoTracking(), CancellationToken.None);
    }

    [Fact]
    public async Task An_empty_queue_is_four_zeros_and_not_a_missing_row()
    {
        (await ComputeAsync()).Should().Be(QueueTotals.Empty);
    }

    [Fact]
    public async Task Counts_what_is_in_the_queue_and_ignores_what_is_finished()
    {
        Seed(
            Job(1, JobState.Pending),
            Job(2, JobState.SpaceReserved),
            Job(3, JobState.Copying),
            Job(4, JobState.Verifying),
            Job(5, JobState.DeletingSource),
            Job(6, JobState.Blocked, JobBlockReason.InsufficientSpace),
            Job(7, JobState.Completed),
            Job(8, JobState.Failed),
            Job(9, JobState.Cancelled));

        var totals = await ComputeAsync();

        // Six non-terminal jobs; the three terminal ones are history, not queue.
        totals.QueuedJobs.Should().Be(6);
        // Only the three states where bytes are actually moving.
        totals.RunningJobs.Should().Be(3);
        totals.BlockedJobs.Should().Be(1);
    }

    [Fact]
    public async Task Pending_bytes_are_the_bytes_waiting_on_space_or_a_volume()
    {
        Seed(
            Job(1, JobState.Blocked, JobBlockReason.InsufficientSpace, total: 1_000),
            Job(2, JobState.Blocked, JobBlockReason.TargetVolumeOffline, total: 500, processed: 200),
            Job(3, JobState.Blocked, JobBlockReason.SourceVolumeOffline, total: 40),
            // Waiting for another JOB, not for a resource: it is blocked, but nothing about it
            // is waiting for room on a disk, and the card says "spazio/volume".
            Job(4, JobState.Blocked, JobBlockReason.DependencyPending, total: 9_000),
            // Not blocked at all.
            Job(5, JobState.Pending, total: 8_000));

        var totals = await ComputeAsync();

        totals.PendingBytes.Should().Be(1_000 + 300 + 40);
        totals.BlockedJobs.Should().Be(4);
    }

    [Fact]
    public async Task A_checkpoint_past_the_total_never_subtracts_from_the_figure()
    {
        Seed(
            Job(1, JobState.Blocked, JobBlockReason.InsufficientSpace, total: 100, processed: 900),
            Job(2, JobState.Blocked, JobBlockReason.InsufficientSpace, total: 700));

        (await ComputeAsync()).PendingBytes.Should().Be(700);
    }
}
