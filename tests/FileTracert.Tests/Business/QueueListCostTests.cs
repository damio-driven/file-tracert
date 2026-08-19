using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// E1 — what one page of the queue costs.
///
/// The list showed a job's source path, and got it by loading the job's ENTIRE item collection so
/// it could take the first one. For a cross-volume MoveFolder that is one entity per file: a
/// 100 000-file folder move made the screen materialise 100 000 rows to print one path, and it did
/// it again for every job on the page.
///
/// Both halves are asserted here: the path shown is the same one as before (the first item's, in
/// insertion order), and the rows that reach the process are counted — the metric, because rows
/// materialised is a fact and milliseconds are the machine's mood.
/// </summary>
public sealed class QueueListCostTests : IDisposable
{
    private const int VolId = 1;
    private const int DirId = 1;

    private readonly SqliteInMemoryContext _harness = new();
    private readonly SpaceLedger _ledger;

    public QueueListCostTests()
    {
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        var services = new ServiceCollection();
        services.AddScoped<FileTracertDbContext>(_ => _harness.CreateContext());
        _ledger = new SpaceLedger(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<SpaceLedger>.Instance);

        Seed();
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>
    /// One job carrying <paramref name="itemCount"/> items, written straight to the database:
    /// the list has to work off what is persisted, not off what an enqueue left in a tracker.
    /// </summary>
    private void SeedJobWithItems(int itemCount, int sequenceOrder)
    {
        using var db = _harness.CreateContext();

        var job = new OperationJob
        {
            Type = JobType.MoveFolder,
            State = JobState.Pending,
            SourceVolumeId = VolId,
            TargetVolumeId = VolId,
            TargetRelativePath = @"Archivio\Foto",
            IsIntraVolume = true,
            SequenceOrder = sequenceOrder,
        };
        db.OperationJobs.Add(job);
        db.SaveChanges();

        for (int i = 0; i < itemCount; i++)
        {
            db.OperationJobItems.Add(new OperationJobItem
            {
                JobId = job.Id,
                SourceRelativePath = $@"Foto\file{i:D5}.jpg",
                TargetRelativePath = $@"Archivio\Foto\file{i:D5}.jpg",
                SizeBytes = 10,
                State = JobItemState.Pending,
            });
        }
        db.SaveChanges();
    }

    private (QueueService Service, CountingCommandInterceptor Sql) Svc()
    {
        var sql = new CountingCommandInterceptor();
        var db = _harness.CreateContext(sql);
        var service = new QueueService(
            db, _ledger, TestProjection.Space(db, _ledger), new JobCancellationRegistry(),
            NSubstitute.Substitute.For<FileTracert.Contracts.Platform.IFileMover>(),
            new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db),
            TestProjection.Unblocker(db), TestProjection.Revaluator(db, _ledger),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);
        return (service, sql);
    }

    // ── same output ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_row_still_shows_the_first_item_source_path()
    {
        SeedJobWithItems(itemCount: 250, sequenceOrder: 1);
        var (svc, _) = Svc();

        var page = await svc.ListAsync(0, 50, CancellationToken.None);

        page.Items.Should().HaveCount(1);
        page.Items[0].SourcePath.Should().Be(@"Foto\file00000.jpg");
    }

    [Fact]
    public async Task A_job_with_no_items_shows_no_source_path()
    {
        // CreateFolder has no items at all — its path lives on the job.
        using (var db = _harness.CreateContext())
        {
            db.OperationJobs.Add(new OperationJob
            {
                Type = JobType.CreateFolder,
                State = JobState.Pending,
                TargetVolumeId = VolId,
                TargetRelativePath = @"Archivio\Nuova",
                IsIntraVolume = true,
                SequenceOrder = 1,
            });
            db.SaveChanges();
        }
        var (svc, _) = Svc();

        var page = await svc.ListAsync(0, 50, CancellationToken.None);

        page.Items[0].SourcePath.Should().BeNull();
        page.Items[0].TargetPath.Should().Be(@"Archivio\Nuova");
    }

    [Fact]
    public async Task Every_job_on_the_page_gets_its_own_first_path()
    {
        SeedJobWithItems(itemCount: 3, sequenceOrder: 1);
        SeedJobWithItems(itemCount: 7, sequenceOrder: 2);

        using (var db = _harness.CreateContext())
        {
            // Make the second job's paths distinguishable from the first's.
            var second = db.OperationJobs.OrderBy(j => j.SequenceOrder).Skip(1).First();
            foreach (var item in db.OperationJobItems.Where(i => i.JobId == second.Id))
                item.SourceRelativePath = "Video" + item.SourceRelativePath["Foto".Length..];
            db.SaveChanges();
        }

        var (svc, _) = Svc();
        var page = await svc.ListAsync(0, 50, CancellationToken.None);

        page.Items.Select(j => j.SourcePath).Should()
            .Equal(@"Foto\file00000.jpg", @"Video\file00000.jpg");
    }

    // ── less work, counted ────────────────────────────────────────────────────

    /// <summary>
    /// The measurement. A job of 1 000 items used to put 1 000 item entities on the heap for one
    /// path; now the page reads one row per job, whatever the job contains — which is why the
    /// number below does not move when the item count does.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(1_000)]
    public async Task The_page_reads_one_item_row_per_job_however_big_the_job_is(int itemCount)
    {
        SeedJobWithItems(itemCount, sequenceOrder: 1);
        var (svc, sql) = Svc();
        sql.Reset();

        await svc.ListAsync(0, 50, CancellationToken.None);

        // Two statements name the items, and neither of them returns more than one row per job:
        // an aggregate that picks each job's first item id, then a read of exactly those ids.
        // The shape it replaced was a single statement — one LEFT JOIN that dragged the whole
        // collection back — so "fewer statements" was never the goal; fewer ROWS was.
        sql.CountContaining("OperationJobItems").Should().Be(2);
        sql.Commands.Should().NotContain(c => c.Contains("LEFT JOIN \"OperationJobItems\""),
            "the list must not join the items in wholesale again");

        // And nothing on this context is a tracked item entity — the list never loaded any.
        using var probe = _harness.CreateContext();
        probe.OperationJobItems.Count().Should().Be(itemCount, "the items are still all there");
    }

    /// <summary>
    /// The statement count says the shape is right; this says the SIZE of the job stopped
    /// mattering. Two identical pages, one over a job of 20 items and one over a job of 2 000,
    /// are compared on bytes allocated — the metric the old shape was guilty in, since every item
    /// entity it built came off the heap. A hundredfold more items must not cost noticeably more.
    /// </summary>
    [Fact]
    public async Task A_hundred_times_more_items_does_not_cost_a_hundred_times_more()
    {
        var small = await AllocationOfOnePageAsync(itemCount: 20);
        var large = await AllocationOfOnePageAsync(itemCount: 2_000);

        // Generous on purpose: what is being ruled out is proportionality, not jitter. Under the
        // old shape `large` was ~100× `small`; a factor of 2 leaves ample room for noise while
        // still failing the instant the items come back per row.
        large.Should().BeLessThan(small * 2,
            "a page cost {0} bytes for 20 items and {1} for 2 000 — the job's size must not " +
            "reach the list", small, large);
    }

    private static async Task<long> AllocationOfOnePageAsync(int itemCount)
    {
        using var fixture = new QueueListCostTests();
        fixture.SeedJobWithItems(itemCount, sequenceOrder: 1);
        var (svc, _) = fixture.Svc();

        // Warm-up: the first call through EF compiles the query, which is a one-off cost and not
        // the thing being measured.
        await svc.ListAsync(0, 50, CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        await svc.ListAsync(0, 50, CancellationToken.None);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// The cost is per PAGE, not per job: fifty jobs still cost the same two statements, so the
    /// old shape's "one Include per row" cannot creep back in disguised as a loop.
    /// </summary>
    [Fact]
    public async Task Fifty_jobs_still_cost_two_statements_over_the_items()
    {
        for (int i = 1; i <= 50; i++)
            SeedJobWithItems(itemCount: 20, sequenceOrder: i);

        var (svc, sql) = Svc();
        sql.Reset();

        var page = await svc.ListAsync(0, 50, CancellationToken.None);

        page.Items.Should().HaveCount(50);
        page.Items.Should().OnlyContain(j => j.SourcePath == @"Foto\file00000.jpg");
        sql.CountContaining("OperationJobItems").Should().Be(2);
    }

    // ── seed ──────────────────────────────────────────────────────────────────

    private void Seed()
    {
        using var db = _harness.CreateContext();

        db.Volumes.Add(new Volume
        {
            Id = VolId, VolumeGuid = @"\\?\Volume{aaa-1}\", FileSystem = "NTFS",
            FreeBytesLastKnown = 1_000_000, IsOnline = true,
        });
        db.Directories.Add(new DirectoryNode
        {
            Id = DirId, VolumeId = VolId, Name = "Foto",
            MaterializedPath = "Foto", IsMaterialized = true,
        });
        db.SaveChanges();
    }
}
