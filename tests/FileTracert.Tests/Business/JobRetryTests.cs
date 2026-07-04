using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Platform;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FileTracert.Tests.Business;

/// <summary>
/// FEATURE Riprova — manual retry of Blocked/Failed jobs via <see cref="QueueService.RetryAsync"/>.
/// Real <see cref="SpaceLedger"/> (reservation coherence is under test) and real
/// <see cref="Win32FileMover"/> on temp dirs (partial cleanup is under test).
/// </summary>
[Trait("Category", "Platform")]
public sealed class JobRetryTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;
    private readonly Win32FileMover _mover;
    private readonly string _volumeGuid;
    private readonly string _mountPoint;
    private readonly string _relRoot;
    private readonly string _absRoot;

    public JobRetryTests()
    {
        _harness = new SqliteInMemoryContext();
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        var services = new ServiceCollection();
        var harness = _harness;
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _ledger = new SpaceLedger(scopeFactory, NullLogger<SpaceLedger>.Instance);

        _mover = new Win32FileMover(NullLogger<Win32FileMover>.Instance);

        var probe = new Win32VolumeProbe(
            new WmiPhysicalDiskResolver(NullLogger<WmiPhysicalDiskResolver>.Instance),
            NullLogger<Win32VolumeProbe>.Instance);

        var tempPath = Path.GetTempPath();
        var vol = probe.EnumerateVolumes()
            .Where(v => v.MountPoints.Count > 0)
            .OrderByDescending(v => v.MountPoints[0].Length)
            .First(v => tempPath.StartsWith(v.MountPoints[0], StringComparison.OrdinalIgnoreCase));

        _volumeGuid = vol.VolumeGuid;
        _mountPoint = vol.MountPoints[0];
        _absRoot = Path.Combine(tempPath, $"ft-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_absRoot);
        _relRoot = Path.GetRelativePath(_mountPoint, _absRoot);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_absRoot))
            Directory.Delete(_absRoot, recursive: true);
    }

    private string R(params string[] parts) => Path.Combine([_relRoot, .. parts]);
    private string Abs(string rel) => Path.GetFullPath(Path.Combine(_mountPoint, rel));

    private QueueService MakeQueue() =>
        new(_harness.CreateContext(), _ledger, new JobCancellationRegistry(), _mover,
            new QueueSignal(), NullLogger<QueueService>.Instance);

    private JobExecutionEngine MakeEngine()
    {
        var db = _harness.CreateContext();
        var indexUpdater = new IndexUpdater(db, new FakeFileSearchIndex(), NullLogger<IndexUpdater>.Instance);
        var notifications = new FileTracert.Business.Notifications.NotificationService(db);
        return new JobExecutionEngine(db, _mover, _ledger, indexUpdater, notifications,
            TimeProvider.System, NullLogger<JobExecutionEngine>.Instance);
    }

    private void SeedVolume(long freeBytes = 1_000_000)
    {
        using var db = _harness.CreateContext();
        db.Volumes.Add(new Volume
        {
            Id = 1, VolumeGuid = _volumeGuid, FileSystem = "NTFS",
            FreeBytesLastKnown = freeBytes, IsOnline = true,
        });
        db.SaveChanges();
    }

    private int SeedJob(JobState state, JobBlockReason reason, long requiredBytes,
        params OperationJobItem[] items)
    {
        using var db = _harness.CreateContext();
        var job = new OperationJob
        {
            Id = 1, Type = JobType.MoveFolder, State = state, BlockReason = reason,
            IsIntraVolume = false, SourceVolumeId = 1, TargetVolumeId = 1,
            TargetRelativePath = R("dst"),
            TotalBytes = requiredBytes, RequiredBytesTarget = requiredBytes,
            FreedBytesSource = requiredBytes,
            ErrorMessage = state == JobState.Failed ? "boom" : null,
            CompletedUtc = state == JobState.Failed ? DateTime.UtcNow : null,
            SequenceOrder = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
        };
        foreach (var item in items)
            job.Items.Add(item);
        db.OperationJobs.Add(job);
        db.SaveChanges();
        return job.Id;
    }

    private static OperationJobItem Item(string srcRel, string dstRel, long size,
        JobItemState state, string? tempPath = null) => new()
    {
        SourceRelativePath = srcRel, TargetRelativePath = dstRel, SizeBytes = size,
        State = state, TempPath = tempPath, ErrorMessage = state == JobItemState.Failed ? "boom" : null,
        CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
    };

    private async Task<List<SpaceLedgerEntry>> ActiveLedgerRows(int jobId)
    {
        await using var db = _harness.CreateContext();
        return await db.SpaceLedgerEntries.AsNoTracking()
            .Where(e => e.JobId == jobId && e.IsActive)
            .ToListAsync();
    }

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Retry_of_Failed_job_resets_to_Pending_cleans_partials_and_restores_reservation()
    {
        const string content = "retry me";
        var srcRel = R("src", "doc.txt");
        var dstRel = R("dst", "doc.txt");
        var partialRel = dstRel + ".fadit-partial";

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(srcRel), content);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(partialRel), "half"); // leftover of the failed run

        SeedVolume();
        int jobId = SeedJob(JobState.Failed, JobBlockReason.None, content.Length,
            Item(srcRel, dstRel, content.Length, JobItemState.Failed, partialRel));
        // A Failed job has no active ledger entries (released on failure).

        var dto = await MakeQueue().RetryAsync(jobId, CancellationToken.None);

        dto.State.Should().Be("Pending");

        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.State.Should().Be(JobState.Pending);
        job.BlockReason.Should().Be(JobBlockReason.None);
        job.ErrorMessage.Should().BeNull();
        job.CompletedUtc.Should().BeNull();
        job.RetryCount.Should().Be(1);

        var item = job.Items.Single();
        item.State.Should().Be(JobItemState.Pending);
        item.BytesCopied.Should().Be(0);
        item.TempPath.Should().BeNull();
        File.Exists(Abs(partialRel)).Should().BeFalse("retry re-copies from scratch");

        // Ledger coherence: exactly one reservation set (target +, source −).
        var rows = await ActiveLedgerRows(jobId);
        rows.Should().HaveCount(2);
        rows.Sum(r => r.DeltaBytes).Should().Be(0);
        rows.Should().ContainSingle(r => r.DeltaBytes == content.Length);
    }

    [Fact]
    public async Task Retry_of_Blocked_job_with_kept_reservation_does_not_duplicate_ledger_entries()
    {
        SeedVolume();
        int jobId = SeedJob(JobState.Blocked, JobBlockReason.InsufficientSpace, 500,
            Item(R("src", "a.bin"), R("dst", "a.bin"), 500, JobItemState.Pending));

        // Engine-blocked jobs keep the reservation written at enqueue.
        await _ledger.ReserveAsync(jobId, 1, targetVolumeId: 1, requiredBytes: 500,
            sourceVolumeId: 1, freedBytes: 500, CancellationToken.None);

        await MakeQueue().RetryAsync(jobId, CancellationToken.None);

        await using var db = _harness.CreateContext();
        (await db.OperationJobs.Where(j => j.Id == jobId).Select(j => j.State).SingleAsync())
            .Should().Be(JobState.Pending);

        var rows = await ActiveLedgerRows(jobId);
        rows.Should().HaveCount(2, "release-then-reserve must normalize, not stack, reservations");
    }

    [Theory]
    [InlineData(JobState.Completed)]
    [InlineData(JobState.Cancelled)]
    [InlineData(JobState.Pending)]
    [InlineData(JobState.Copying)]
    public async Task Retry_of_non_retryable_state_is_rejected(JobState state)
    {
        SeedVolume();
        int jobId = SeedJob(state, JobBlockReason.None, 100,
            Item(R("src", "x.bin"), R("dst", "x.bin"), 100, JobItemState.Pending));

        var act = () => MakeQueue().RetryAsync(jobId, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Retried_job_with_an_already_Verified_item_completes_without_stalling()
    {
        // Failure happened during Verifying: item1 was already finalized (Verified), item2
        // failed. After retry, the engine must re-copy item2 and still advance past the
        // Copying gate even though item1 sits in Verified (neither Copied nor Done).
        const string content1 = "already finalized";
        const string content2 = "needs re-copy";
        var src1 = R("src", "one.txt");
        var src2 = R("src", "two.txt");
        var dst1 = R("dst", "one.txt");
        var dst2 = R("dst", "two.txt");

        Directory.CreateDirectory(Abs(R("src")));
        File.WriteAllText(Abs(src1), content1);
        File.WriteAllText(Abs(src2), content2);
        Directory.CreateDirectory(Abs(R("dst")));
        File.WriteAllText(Abs(dst1), content1); // item1 already finalized on target

        SeedVolume();
        int jobId = SeedJob(JobState.Failed, JobBlockReason.None, content1.Length + content2.Length,
            Item(src1, dst1, content1.Length, JobItemState.Verified),
            Item(src2, dst2, content2.Length, JobItemState.Failed));

        await MakeQueue().RetryAsync(jobId, CancellationToken.None);
        await MakeEngine().ExecuteJobAsync(jobId, CancellationToken.None);

        await using var db = _harness.CreateContext();
        var job = await db.OperationJobs.Include(j => j.Items).AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.State.Should().Be(JobState.Completed);
        job.Items.Should().OnlyContain(i => i.State == JobItemState.Done);

        File.ReadAllText(Abs(dst1)).Should().Be(content1);
        File.ReadAllText(Abs(dst2)).Should().Be(content2);
        // Sources moved away (MoveFolder deletes them after verify).
        File.Exists(Abs(src1)).Should().BeFalse();
        File.Exists(Abs(src2)).Should().BeFalse();
    }
}
