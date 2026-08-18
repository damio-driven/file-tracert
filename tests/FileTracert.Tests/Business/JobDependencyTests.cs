using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Platform;
using FileTracert.Data;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 9c — dependencies between jobs: how they are detected at enqueue, and how they resolve.
///
/// The contract: an operation that touches something another queued job is already touching is
/// never rejected (§4 «non rifiutare mai un job all'enqueue»). It goes into the queue
/// <c>Blocked(DependencyPending)</c>, pointing at the job that holds the entity, and it does NOT
/// take the projection overlay — that still belongs to the first job.
///
/// Real services throughout: real SQLite, the real ledger, the real guard.
/// </summary>
public sealed class JobDependencyTests : IDisposable
{
    private const int Vol1Id = 1;
    private const int Vol2Id = 2;
    private const int DocsId = 1;       // "Docs"
    private const int SubId = 2;        // "Docs\Sub"
    private const int OtherCaseId = 3;  // "DOCS\Other"  — same tree, different casing
    private const int MediaId = 4;      // "Media"       — unrelated sibling
    private const int ReportId = 1;     // "Docs\report.txt"
    private const int DataId = 2;       // "Docs\Sub\data.csv"
    private const int PhotoId = 3;      // "Media\photo.jpg"

    private readonly SqliteInMemoryContext _harness = new();
    private readonly SpaceLedger _ledger;
    private readonly JobCancellationRegistry _cancellation = new();

    public JobDependencyTests()
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

    private JobExecutionEngine Engine() => EngineWith(NSubstitute.Substitute.For<IFileMover>());

    private JobExecutionEngine EngineWith(IFileMover mover)
    {
        var db = _harness.CreateContext();
        return new JobExecutionEngine(db, mover, _ledger,
            TestProjection.Index(db), TestProjection.Overlay(db), new FakeNotificationPublisher(),
            TimeProvider.System, TestProjection.Realtime(), NullLogger<JobExecutionEngine>.Instance);
    }

    private BlockedJobRevaluator Revaluator()
    {
        var db = _harness.CreateContext();
        return new BlockedJobRevaluator(db, _ledger, TestProjection.Unblocker(db),
            TestProjection.Realtime(), NullLogger<BlockedJobRevaluator>.Instance);
    }

    private QueueService Svc(params IInterceptor[] interceptors)
    {
        var db = _harness.CreateContext(interceptors);
        return new QueueService(db, _ledger, _cancellation,
            NSubstitute.Substitute.For<IFileMover>(), new QueueSignal(),
            TestProjection.Index(db), TestProjection.Overlay(db), TestProjection.Unblocker(db),
            TestProjection.Revaluator(db, _ledger),
            TestProjection.Realtime(), NullLogger<QueueService>.Instance);
    }

    private void Seed()
    {
        using var db = _harness.CreateContext();

        db.Volumes.AddRange(
            new Volume
            {
                Id = Vol1Id, VolumeGuid = @"\\?\Volume{aaa-1}\", FileSystem = "NTFS",
                FreeBytesLastKnown = 1_000_000, IsOnline = true
            },
            new Volume
            {
                Id = Vol2Id, VolumeGuid = @"\\?\Volume{bbb-2}\", FileSystem = "NTFS",
                FreeBytesLastKnown = 1_000_000, IsOnline = true
            });

        db.Directories.AddRange(
            NewDir(DocsId, null, "Docs", "Docs"),
            NewDir(SubId, DocsId, "Sub", @"Docs\Sub"),
            NewDir(OtherCaseId, DocsId, "Other", @"DOCS\Other"),
            NewDir(MediaId, null, "Media", "Media"));

        db.Files.AddRange(
            NewFile(ReportId, DocsId, "report.txt"),
            NewFile(DataId, SubId, "data.csv"),
            NewFile(PhotoId, MediaId, "photo.jpg"));

        db.SaveChanges();
    }

    private static DirectoryNode NewDir(int id, int? parentId, string name, string path) => new()
    {
        Id = id, VolumeId = Vol1Id, ParentId = parentId, Name = name, MaterializedPath = path,
        IsMaterialized = true, IsPresent = true
    };

    private static FileEntry NewFile(int id, int dirId, string name) => new()
    {
        Id = id, VolumeId = Vol1Id, DirectoryId = dirId, Name = name,
        Extension = name[(name.LastIndexOf('.') + 1)..], Category = FileCategory.Document,
        SizeBytes = 1_000, IsPresent = true, IsIncluded = true,
        FileCreatedUtc = DateTime.UtcNow, FileModifiedUtc = DateTime.UtcNow,
        LastIndexedUtc = DateTime.UtcNow
    };

    private static CancellationToken None => CancellationToken.None;

    private Task<OperationJobDto> RenameFileAsync(int fileId, string newName) =>
        Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = fileId, NewName = newName
        }, None);

    private Task<OperationJobDto> RenameFolderAsync(int dirId, string newName) =>
        Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFolder, SourceDirectoryId = dirId, NewName = newName
        }, None);

    private Task<OperationJobDto> MoveFileAsync(int fileId, string targetPath, int volumeId = Vol1Id) =>
        Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFile, SourceFileId = fileId,
            TargetVolumeId = volumeId, TargetRelativePath = targetPath
        }, None);

    private Task<OperationJobDto> CreateFolderAsync(string path, int volumeId = Vol1Id) =>
        Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.CreateFolder, TargetVolumeId = volumeId, TargetRelativePath = path
        }, None);

    private async Task<OperationJob> ReloadJobAsync(int jobId)
    {
        using var db = _harness.CreateContext();
        return await db.OperationJobs.AsNoTracking().FirstAsync(j => j.Id == jobId, None);
    }

    private static void ShouldDependOn(OperationJobDto dto, OperationJobDto prerequisite)
    {
        dto.State.Should().Be(nameof(JobState.Blocked));
        dto.BlockReason.Should().Be(nameof(JobBlockReason.DependencyPending));
        dto.DependsOnJobId.Should().Be(prerequisite.Id);
        dto.ErrorMessage.Should().Contain($"#{prerequisite.Id}");
    }

    // ── the second operation is queued, never refused ─────────────────────────

    [Fact]
    public async Task Second_op_on_the_same_file_is_queued_blocked_not_rejected()
    {
        var first = await RenameFileAsync(ReportId, "v2.txt");
        var second = await RenameFileAsync(ReportId, "v3.txt");

        first.State.Should().Be(nameof(JobState.Pending));
        ShouldDependOn(second, first);
    }

    [Fact]
    public async Task A_blocked_dependent_does_not_take_the_overlay()
    {
        var first = await RenameFileAsync(ReportId, "v2.txt");
        var second = await RenameFileAsync(ReportId, "v3.txt");

        using var db = _harness.CreateContext();
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == ReportId, None);

        file.PendingJobId.Should().Be(first.Id, "the entity still belongs to the first job");
        file.PendingName.Should().Be("v2.txt");
        file.PendingState.Should().Be(EntityPendingState.PendingRename);
        second.Id.Should().NotBe(first.Id);
    }

    // ── subtree overlap, both directions ──────────────────────────────────────

    [Fact]
    public async Task An_op_on_a_descendant_of_a_pending_folder_is_blocked()
    {
        var folder = await RenameFolderAsync(DocsId, "Documenti");
        var descendant = await MoveFileAsync(DataId, "Media");

        ShouldDependOn(descendant, folder);
    }

    [Fact]
    public async Task An_op_on_an_ancestor_of_a_pending_file_is_blocked()
    {
        // The reverse direction, and the one the old FileId-only guard could not see at all.
        var file = await RenameFileAsync(DataId, "v2.csv");
        var folder = await RenameFolderAsync(DocsId, "Documenti");

        ShouldDependOn(folder, file);
    }

    [Fact]
    public async Task An_unrelated_sibling_is_left_alone()
    {
        await RenameFolderAsync(DocsId, "Documenti");
        var sibling = await RenameFileAsync(PhotoId, "foto.jpg");

        sibling.State.Should().Be(nameof(JobState.Pending));
        sibling.DependsOnJobId.Should().BeNull();
    }

    [Fact]
    public async Task The_same_relative_path_on_another_volume_is_another_place()
    {
        await RenameFolderAsync(DocsId, "Documenti");

        // Same relative destination string, different volume: no overlap.
        var crossVolume = await MoveFileAsync(PhotoId, "Docs", Vol2Id);

        crossVolume.State.Should().Be(nameof(JobState.Pending));
        crossVolume.DependsOnJobId.Should().BeNull();
    }

    [Fact]
    public async Task Overlap_is_case_insensitive()
    {
        // "DOCS\Other" and "Docs" are the same tree as far as Windows is concerned.
        var folder = await RenameFolderAsync(DocsId, "Documenti");
        var caseVariant = await RenameFolderAsync(OtherCaseId, "Altro");

        ShouldDependOn(caseVariant, folder);
    }

    // ── the blind spots of the old guard ──────────────────────────────────────

    [Fact]
    public async Task A_rename_of_the_destination_of_a_pending_move_is_blocked()
    {
        // Finding 8b: nothing used to inspect the TARGET of a queued job, so this rename went
        // through and the move then resurrected the old folder name via EnsureTargetDirectory.
        var move = await MoveFileAsync(ReportId, "Media");
        var renameTarget = await RenameFolderAsync(MediaId, "Immagini");

        ShouldDependOn(renameTarget, move);
    }

    [Fact]
    public async Task A_pending_CreateFolder_is_visible_to_the_guard()
    {
        // Finding 8c: a CreateFolder owns no item, so the item-based guard never saw it.
        var create = await CreateFolderAsync(@"Media\2026");

        using (var db = _harness.CreateContext())
        {
            var projected = await db.Directories.AsNoTracking()
                .FirstAsync(d => d.MaterializedPath == @"Media\2026", None);
            projected.PendingState.Should().Be(EntityPendingState.PendingCreate);
        }

        var rename = await RenameFolderAsync(
            await IdOfDirectoryAsync(@"Media\2026"), "Duemilaventisei");

        ShouldDependOn(rename, create);
    }

    [Fact]
    public async Task Two_CreateFolders_on_the_same_path_are_serialized()
    {
        var first = await CreateFolderAsync(@"Media\2026");
        var second = await CreateFolderAsync(@"Media\2026");

        ShouldDependOn(second, first);
    }

    // ── §5: a queued folder is a legal destination ────────────────────────────

    [Fact]
    public async Task Moving_a_file_into_a_queued_folder_is_not_a_conflict()
    {
        // The projection promise of §5: "I queue folder X, then I move files into it". Two
        // targets that merely NEST must stay legal — only equal targets collide.
        var create = await CreateFolderAsync(@"Media\2026");
        var move = await MoveFileAsync(ReportId, @"Media\2026");

        create.State.Should().Be(nameof(JobState.Pending));
        move.State.Should().Be(nameof(JobState.Pending));
        move.DependsOnJobId.Should().BeNull();
    }

    [Fact]
    public async Task Two_files_moved_into_the_same_folder_do_not_conflict()
    {
        var first = await MoveFileAsync(ReportId, "Media");
        var second = await MoveFileAsync(DataId, "Media");

        first.State.Should().Be(nameof(JobState.Pending));
        second.State.Should().Be(nameof(JobState.Pending));
    }

    // ── the dependency points at the LAST conflicting job ─────────────────────

    [Fact]
    public async Task The_dependency_points_at_the_last_conflicting_job_in_queue_order()
    {
        var onSub = await RenameFolderAsync(SubId, "SubRinominata");      // Docs\Sub
        var onData = await RenameFileAsync(DataId, "v2.csv");             // Docs\Sub\data.csv

        // "Docs" overlaps both. The dependency must name the LAST of them in queue order:
        // everything ahead of it resolves first anyway, and the revaluation re-asks the guard
        // before actually unblocking anyone.
        var onDocs = await RenameFolderAsync(DocsId, "Documenti");

        onSub.State.Should().Be(nameof(JobState.Pending));
        ShouldDependOn(onData, onSub);
        ShouldDependOn(onDocs, onData);
    }

    private async Task<int> IdOfDirectoryAsync(string path)
    {
        using var db = _harness.CreateContext();
        return await db.Directories.AsNoTracking()
            .Where(d => d.MaterializedPath == path).Select(d => d.Id).FirstAsync(None);
    }

    // ── release: what happens when the prerequisite is done ───────────────────

    [Fact]
    public async Task Completing_the_prerequisite_releases_the_dependent_and_hands_it_the_overlay()
    {
        var first = await RenameFileAsync(ReportId, "v2.txt");
        var second = await RenameFileAsync(ReportId, "v3.txt");

        await Engine().ExecuteJobAsync(first.Id, None);
        (await ReloadJobAsync(first.Id)).State.Should().Be(JobState.Completed);

        (await Revaluator().RevaluateAsync(None)).Should().Be(1);

        var released = await ReloadJobAsync(second.Id);
        released.State.Should().Be(JobState.Pending);
        released.BlockReason.Should().Be(JobBlockReason.None);
        released.DependsOnJobId.Should().BeNull();

        using var db = _harness.CreateContext();
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == ReportId, None);
        file.PendingJobId.Should().Be(second.Id, "the released job is now the queue's promise");
        file.PendingName.Should().Be("v3.txt");
    }

    [Fact]
    public async Task A_dependent_does_not_move_while_its_prerequisite_is_still_queued()
    {
        var first = await RenameFileAsync(ReportId, "v2.txt");
        var second = await RenameFileAsync(ReportId, "v3.txt");

        (await Revaluator().RevaluateAsync(None)).Should().Be(0);

        var still = await ReloadJobAsync(second.Id);
        still.State.Should().Be(JobState.Blocked);
        still.BlockReason.Should().Be(JobBlockReason.DependencyPending);
        still.DependsOnJobId.Should().Be(first.Id);
    }

    [Fact]
    public async Task A_released_dependent_runs_on_fresh_snapshots_not_dead_paths()
    {
        // Finding 8a, the whole point: the folder job runs first (FIFO) and moves the ground
        // under the file job's snapshot. Before 9c the file job then died on a
        // FileNotFoundException — permanently, because the retry re-used the same dead path.
        var folderJob = await RenameFolderAsync(DocsId, "Documenti");
        var fileJob = await MoveFileAsync(DataId, "Media");

        ShouldDependOn(fileJob, folderJob);

        await Engine().ExecuteJobAsync(folderJob.Id, None);
        (await ReloadJobAsync(folderJob.Id)).State.Should().Be(JobState.Completed);

        (await Revaluator().RevaluateAsync(None)).Should().Be(1);

        using var db = _harness.CreateContext();
        var item = await db.OperationJobItems.AsNoTracking().FirstAsync(i => i.JobId == fileJob.Id, None);
        item.SourceRelativePath.Should().Be(@"Documenti\Sub\data.csv",
            "the file is where the completed rename left it, not where it was queued from");
        item.TargetRelativePath.Should().Be(@"Media\data.csv");
    }

    [Fact]
    public async Task A_released_folder_job_follows_the_rename_that_moved_it()
    {
        // Same, for the item that has no FileId to resolve: the folder marker of a MoveFolder.
        var renameParent = await RenameFolderAsync(DocsId, "Documenti");
        var moveChild = await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.MoveFolder, SourceDirectoryId = SubId,
            TargetVolumeId = Vol1Id, TargetRelativePath = "Media"
        }, None);

        ShouldDependOn(moveChild, renameParent);

        await Engine().ExecuteJobAsync(renameParent.Id, None);
        (await Revaluator().RevaluateAsync(None)).Should().Be(1);

        using var db = _harness.CreateContext();
        var item = await db.OperationJobItems.AsNoTracking()
            .FirstAsync(i => i.JobId == moveChild.Id, None);
        item.SourceRelativePath.Should().Be(@"Documenti\Sub");
        item.TargetRelativePath.Should().Be(@"Media\Sub");
    }

    [Fact]
    public async Task The_dependency_is_repointed_when_someone_else_is_still_in_the_way()
    {
        var onSub = await RenameFolderAsync(SubId, "SubRinominata");     // Docs\Sub
        var onData = await RenameFileAsync(DataId, "v2.csv");            // Docs\Sub\data.csv
        var onDocs = await RenameFolderAsync(DocsId, "Documenti");       // Docs

        ShouldDependOn(onData, onSub);
        ShouldDependOn(onDocs, onData);

        await Engine().ExecuteJobAsync(onSub.Id, None);
        await Revaluator().RevaluateAsync(None);

        // onData is free; onDocs is not — it now waits for onData instead of onSub.
        (await ReloadJobAsync(onData.Id)).State.Should().Be(JobState.Pending);

        var repointed = await ReloadJobAsync(onDocs.Id);
        repointed.State.Should().Be(JobState.Blocked);
        repointed.BlockReason.Should().Be(JobBlockReason.DependencyPending);
        repointed.DependsOnJobId.Should().Be(onData.Id);
    }

    // ── a prerequisite that ends badly: parked, never cascaded ────────────────

    [Fact]
    public async Task Cancelling_the_prerequisite_parks_the_dependent_instead_of_cancelling_it()
    {
        var first = await CreateFolderAsync(@"Media\2026");
        var second = await CreateFolderAsync(@"Media\2026");

        await Svc().CancelAsync(first.Id, None);

        (await ReloadJobAsync(first.Id)).State.Should().Be(JobState.Cancelled);

        var dependent = await ReloadJobAsync(second.Id);
        dependent.State.Should().Be(JobState.Blocked, "§5: no cascade of cancellations");
        dependent.BlockReason.Should().Be(JobBlockReason.DependencyCancelled);
        dependent.ErrorMessage.Should().Contain($"#{first.Id}");
    }

    [Fact]
    public async Task A_DependencyCancelled_job_is_not_released_on_its_own()
    {
        // The guard is clean the moment the prerequisite is cancelled — releasing on that alone
        // would silently recreate the folder the user just decided NOT to create (finding 9).
        var first = await CreateFolderAsync(@"Media\2026");
        var second = await CreateFolderAsync(@"Media\2026");

        await Svc().CancelAsync(first.Id, None);
        await Revaluator().RevaluateAsync(None);

        var dependent = await ReloadJobAsync(second.Id);
        dependent.State.Should().Be(JobState.Blocked);
        dependent.BlockReason.Should().Be(JobBlockReason.DependencyCancelled);
    }

    [Fact]
    public async Task A_DependencyCancelled_job_is_reactivated_by_a_retry()
    {
        var first = await CreateFolderAsync(@"Media\2026");
        var second = await CreateFolderAsync(@"Media\2026");

        await Svc().CancelAsync(first.Id, None);
        var retried = await Svc().RetryAsync(second.Id, None);

        retried.State.Should().Be(nameof(JobState.Pending));
        retried.DependsOnJobId.Should().BeNull("the dependency died with the cancelled job");
    }

    [Fact]
    public async Task A_failed_prerequisite_parks_its_dependents_the_same_way()
    {
        var first = await RenameFileAsync(ReportId, "v2.txt");
        var second = await RenameFileAsync(ReportId, "v3.txt");

        var mover = NSubstitute.Substitute.For<IFileMover>();
        mover.When(m => m.RenameIntraVolume(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()))
             .Do(_ => throw new IOException("boom"));
        await EngineWith(mover).ExecuteJobAsync(first.Id, None);

        (await ReloadJobAsync(first.Id)).State.Should().Be(JobState.Failed);

        var dependent = await ReloadJobAsync(second.Id);
        dependent.State.Should().Be(JobState.Blocked);
        dependent.BlockReason.Should().Be(JobBlockReason.DependencyCancelled);
    }

    // ── the barrier at execution time ─────────────────────────────────────────

    [Fact]
    public async Task A_dependent_forced_to_Pending_is_parked_again_instead_of_running()
    {
        var first = await RenameFileAsync(ReportId, "v2.txt");
        var second = await RenameFileAsync(ReportId, "v3.txt");

        // Force it runnable behind the engine's back — what a bad Retry or a broken revaluation
        // would do. The dependency column is left in place: that is the barrier's only input.
        using (var db = _harness.CreateContext())
        {
            var job = await db.OperationJobs.FirstAsync(j => j.Id == second.Id, None);
            job.State = JobState.Pending;
            job.BlockReason = JobBlockReason.None;
            await db.SaveChangesAsync(None);
        }

        var mover = NSubstitute.Substitute.For<IFileMover>();
        await EngineWith(mover).ExecuteJobAsync(second.Id, None);

        var parked = await ReloadJobAsync(second.Id);
        parked.State.Should().Be(JobState.Blocked);
        parked.BlockReason.Should().Be(JobBlockReason.DependencyPending);
        mover.ReceivedCalls().Should().BeEmpty("nothing on disk may be touched out of order");
        (await ReloadJobAsync(first.Id)).State.Should().Be(JobState.Pending);
    }

    // ── the guard is asked once the write lock is held ────────────────────────

    [Fact]
    public async Task A_job_that_commits_during_our_enqueue_is_still_seen_by_the_guard()
    {
        // The guard used to run before the transaction opened, so a competing enqueue that
        // committed in between was invisible and BOTH jobs landed Pending on one entity — two
        // owners of one overlay, which §5 forbids. Asking after the insert (this connection
        // holds the write lock from then on) closes it. Reproduced by committing the rival on a
        // separate context at the exact moment our insert happens.
        var rival = new EnqueueDuringInsertInterceptor(_harness, ReportId);
        var mine = await Svc(rival).EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = ReportId, NewName = "mio.txt"
        }, None);

        rival.RivalJobId.Should().NotBeNull("the test only proves anything if the race happened");

        mine.State.Should().Be(nameof(JobState.Blocked));
        mine.BlockReason.Should().Be(nameof(JobBlockReason.DependencyPending));
        mine.DependsOnJobId.Should().Be(rival.RivalJobId);

        // The rival is a synthetic row and never ran OverlayWriter, so what matters here is the
        // negative: OUR job did not stamp itself onto an entity it does not own.
        using var db = _harness.CreateContext();
        var file = await db.Files.AsNoTracking().FirstAsync(f => f.Id == ReportId, None);
        file.PendingJobId.Should().NotBe(mine.Id, "one entity, one owner");
    }

    /// <summary>
    /// Commits a rival job on the same entity, on its own context, exactly when our enqueue
    /// inserts its own — i.e. a second API request that got there first. Fires once.
    /// </summary>
    private sealed class EnqueueDuringInsertInterceptor : SaveChangesInterceptor
    {
        private readonly int _fileId;
        private bool _fired;

        public EnqueueDuringInsertInterceptor(SqliteInMemoryContext harness, int fileId) =>
            _fileId = fileId;

        public int? RivalJobId { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_fired) return base.SavingChangesAsync(eventData, result, cancellationToken);

            var inserting = eventData.Context!.ChangeTracker.Entries<OperationJob>()
                .FirstOrDefault(e => e.State == EntityState.Added);
            if (inserting is null) return base.SavingChangesAsync(eventData, result, cancellationToken);

            _fired = true;
            // The rival takes the queue position OUR job was about to take — which is what
            // actually happens when it commits first. Our insert then loses the unique index,
            // retries to the next number (C26), and only THEN does the guard ask: the rival is
            // now ahead of us in FIFO, so it owns the entity and we wait for it. The two
            // mechanisms have to compose, and this is where that is proven.
            var contestedOrder = inserting.Entity.SequenceOrder;

            // Raw SQL on the live connection: the in-memory harness shares ONE SqliteConnection
            // across contexts, so a second EF context cannot open its own transaction here. What
            // matters for the regression is identical either way — a rival row exists on this
            // entity by the time the guard asks, and the OLD code had already asked.
            var connection = (SqliteConnection)eventData.Context.Database.GetDbConnection();
            var tx = (SqliteTransaction?)eventData.Context.Database.CurrentTransaction?.GetDbTransaction();

            using (var job = connection.CreateCommand())
            {
                job.Transaction = tx;
                job.CommandText = """
                    INSERT INTO OperationJobs
                        (Type, State, BlockReason, SourceVolumeId, TargetVolumeId,
                         TargetRelativePath, IsIntraVolume, TotalBytes, BytesProcessed,
                         RequiredBytesTarget, FreedBytesSource, EstimateIsLive, SequenceOrder,
                         RetryCount, CreatedUtc, UpdatedUtc)
                    VALUES ('RenameFile', 'Pending', 'None', 1, 1, 'rivale.txt', 1, 0, 0, 0, 0, 1,
                            $order, 0, $now, $now);
                    SELECT last_insert_rowid();
                    """;
                job.Parameters.AddWithValue("$order", contestedOrder);
                job.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                RivalJobId = Convert.ToInt32(job.ExecuteScalar());
            }

            using (var item = connection.CreateCommand())
            {
                item.Transaction = tx;
                item.CommandText = """
                    INSERT INTO OperationJobItems
                        (JobId, FileId, SourceRelativePath, TargetRelativePath, SizeBytes, State,
                         BytesCopied, CreatedUtc, UpdatedUtc)
                    VALUES ($job, $file, 'Docs\report.txt', 'Docs\rivale.txt', 0, 'Pending', 0,
                            $now, $now);
                    """;
                item.Parameters.AddWithValue("$job", RivalJobId);
                item.Parameters.AddWithValue("$file", _fileId);
                item.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
                item.ExecuteNonQuery();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ── still refused: the requests that are wrong, not merely queued behind ──

    [Fact]
    public async Task A_malformed_request_is_still_rejected_outright()
    {
        var act = async () => await Svc().EnqueueAsync(new CreateJobRequest
        {
            Type = JobType.RenameFile, SourceFileId = ReportId, NewName = @"bad\name.txt"
        }, None);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
