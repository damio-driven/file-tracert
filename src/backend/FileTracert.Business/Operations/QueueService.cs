using FileTracert.Business.Projection;
using FileTracert.Business.Realtime;
using FileTracert.Business.Scanning;
using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Errors;
using FileTracert.Contracts.Operations;
using FileTracert.Contracts.Paging;
using FileTracert.Contracts.Platform;
using FileTracert.Contracts.Scanning;
using FileTracert.Data.Entities;
using FileTracert.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileTracert.Business.Operations;

/// <summary>
/// Manages the job queue: enqueue, preview, cancel, list.
/// Scoped — one instance per request/scope, backed by a single DbContext.
/// </summary>
public sealed class QueueService : IQueueService
{
    private static readonly HashSet<JobState> TerminalStates = [.. JobStates.Terminal];

    /// <summary>
    /// Largest selection accepted by one <see cref="EnqueueBatchAsync"/> call.
    ///
    /// The batch is one exclusive SQLite write transaction, and the conflict guard re-reads the
    /// items of every job already inserted by it, so the cost of an element grows with the
    /// elements before it. Left unbounded, a "select all" over a large folder would hold the
    /// write lock long enough for the processor's own checkpoints to hit the busy timeout — on a
    /// job that is physically copying — and an abort would then throw away minutes of work.
    /// A refusal that names the limit is a worse gesture than a slow one but a far better outcome
    /// than a queue that stalls, and splitting the selection is something the user can actually do.
    /// </summary>
    public const int MaxBatchSize = 500;

    private readonly FileTracertDbContext _db;
    private readonly ISpaceLedger _ledger;
    private readonly SpaceCheck _spaceCheck;
    private readonly IJobCancellationRegistry _cancellation;
    private readonly IFileMover _mover;
    private readonly IQueueSignal _signal;
    private readonly IndexUpdater _indexUpdater;
    private readonly OverlayWriter _overlay;
    private readonly JobUnblocker _unblocker;
    private readonly BlockedJobRevaluator _revaluator;
    private readonly RealtimeEvents _realtime;
    private readonly ILogger<QueueService> _logger;

    public QueueService(
        FileTracertDbContext db,
        ISpaceLedger ledger,
        SpaceCheck spaceCheck,
        IJobCancellationRegistry cancellation,
        IFileMover mover,
        IQueueSignal signal,
        IndexUpdater indexUpdater,
        OverlayWriter overlay,
        JobUnblocker unblocker,
        BlockedJobRevaluator revaluator,
        RealtimeEvents realtime,
        ILogger<QueueService> logger)
    {
        _db = db;
        _ledger = ledger;
        _spaceCheck = spaceCheck;
        _cancellation = cancellation;
        _mover = mover;
        _signal = signal;
        _indexUpdater = indexUpdater;
        _overlay = overlay;
        _unblocker = unblocker;
        _revaluator = revaluator;
        _realtime = realtime;
        _logger = logger;
    }

    // ── IQueueService ─────────────────────────────────────────────────────────

    public async Task<OperationJobDto> EnqueueAsync(CreateJobRequest request, CancellationToken ct) =>
        (await EnqueueBatchAsync([request], ct))[0];

    /// <summary>
    /// C25 — one user gesture, one request, ONE transaction.
    ///
    /// All-or-nothing on purpose. The client used to loop a POST per selected file, so a failure
    /// at item N left 1..N−1 in the queue with nothing on screen saying so, and the obvious
    /// reaction — click again — re-enqueued the first N−1 as dependents of themselves. Either the
    /// whole selection is in the queue or none of it is: then the error is readable, the retry is
    /// the same gesture, and there is no half state to explain.
    /// The price is that one bad item stops the other forty-nine; it is paid knowingly, because
    /// the alternative (partial success) is only honest if the response enumerates exactly which
    /// items landed — and a queue the user has to reconcile item by item is the thing this fixes.
    ///
    /// Everything the single enqueue does per job still happens per job: the guard is asked for
    /// every element (a batch is not a free pass), <see cref="OperationJob.SequenceOrder"/> is
    /// still assigned inside the transaction against the unique index (C26/9c), and each job's
    /// overlay and ledger rows are staged in the same unit of work.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The batch is empty, exceeds <see cref="MaxBatchSize"/>, or one of its requests is invalid.
    /// </exception>
    public async Task<IReadOnlyList<OperationJobDto>> EnqueueBatchAsync(
        IReadOnlyList<CreateJobRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            throw new ArgumentException("Nessuna operazione da accodare: la richiesta è vuota.");

        if (requests.Count > MaxBatchSize)
            throw new ArgumentException(
                $"Troppe operazioni in una sola richiesta ({requests.Count}): il massimo è " +
                $"{MaxBatchSize}. Dividere la selezione e accodarla in più riprese.");

        var created = new List<(OperationJob Job, List<OperationJobItem> Items, bool Reserved)>(requests.Count);
        // What this batch has already promised to each target volume. The in-memory ledger only
        // learns about these jobs after the commit (see below), so without carrying the demand
        // forward by hand fifty 1 GB moves onto a 10 GB drive would every one of them be weighed
        // against the same untouched free space, and all fifty would be born Pending.
        var batchDemandByVolume = new Dictionary<int, long>();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        for (int i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            try
            {
                var (job, items, shouldReserve) = await BuildJobAsync(
                    request,
                    request.TargetVolumeId is { } tv ? batchDemandByVolume.GetValueOrDefault(tv) : 0,
                    ct);

                _db.OperationJobs.Add(job);
                foreach (var item in items)
                    _db.OperationJobItems.Add(item);
                // Assigns job.Id and job.SequenceOrder, still inside the open transaction (C26).
                await AssignSequenceOrderAndSaveAsync(job, ct);

                // Finding 8 — the guard runs AFTER the insert, deliberately. SQLite grants the write
                // lock at the first write, not at BEGIN: asking before it would read a snapshot another
                // enqueue can still change underneath, and two requests racing on the same entity would
                // both read "clear" and both land Pending on it (§5 allows one). From here on this
                // connection holds the lock, so a competitor has either already committed — and is
                // therefore visible to this read — or is queued behind us. Within a batch the same
                // property makes each element see the elements before it: they are already inserted on
                // this connection, so two requests for the same entity in one batch serialize exactly
                // as two separate calls would.
                await ApplyPendingWorkGuardAsync(job, ct);

                // §5 — queuing an operation mutates the PROJECTION immediately: the entity is shown at
                // once under its new name / in its new folder / on its new volume. Written here, inside
                // the job's own transaction, so job and overlay commit together or not at all — an
                // overlay without a job would point at a job that never existed.
                // Applied AFTER the offline gate and regardless of its verdict: a job parked
                // Blocked(volume offline) is still in the queue, therefore still in the projection.
                // The ONE exception is a job born Blocked(DependencyPending): the entity is already
                // another job's, and the projection must keep showing THAT job's promise. This dependent
                // writes its own overlay when the revaluation releases it (JobUnblocker).
                if (JobDependencies.OwnsItsEntity(job))
                    await _overlay.ApplyAsync(job, items, ct);

                // C3: stage the ledger reservation in the SAME transaction as the job, so the two commit
                // atomically. The old code reserved AFTER the commit — a throw or aborted request between
                // the two left the job Pending with no ledger entry, making every other job's feasibility
                // under-count this demand and overcommit the target.
                if (shouldReserve)
                {
                    _db.SpaceLedgerEntries.AddRange(SpaceLedger.BuildReservationEntries(
                        job.Id, job.TargetVolumeId!.Value, job.RequiredBytesTarget,
                        job.SourceVolumeId, job.FreedBytesSource));
                    await _db.SaveChangesAsync(ct);

                    batchDemandByVolume[job.TargetVolumeId!.Value] =
                        batchDemandByVolume.GetValueOrDefault(job.TargetVolumeId!.Value)
                        + job.RequiredBytesTarget;
                }

                created.Add((job, items, shouldReserve));
            }
            catch (Exception ex) when (requests.Count > 1 && ex is ArgumentException or InvalidOperationException)
            {
                // Wraps the WHOLE element, not just its build: a sequence-number exhaustion or a
                // guard failure is just as much "element i broke and nothing was queued", and a 400
                // that omits the second half sends the user straight back to the button.
                // The tracker is cleared because the rollback that follows (the transaction is
                // disposed on the way out) does not undo what EF believes: the jobs it just saved
                // would stay Unchanged with server-generated ids that no longer exist on disk.
                _db.ChangeTracker.Clear();
                throw DescribeBatchFailure(ex, i, requests.Count);
            }
        }

        await tx.CommitAsync(ct);

        // From here the durable work is DONE, so nothing below may be cancelled: an abort between
        // the commit and the mirror update would leave N reservations in the database and none in
        // memory, and every later feasibility check would under-count the demand on that volume —
        // the direction that overcommits a drive. The mirror is only rebuilt from the DB at
        // startup, so that state would persist for the whole run.
        var afterCommit = CancellationToken.None;

        // Update the in-memory mirror ONLY after the DB commit succeeds: the durable entries and
        // the job now exist together, and the mirror can never claim a reservation the DB lacks.
        foreach (var (job, _, reserved) in created)
        {
            if (!reserved) continue;
            await _ledger.RegisterReservationInMemoryAsync(
                job.Id, job.SequenceOrder, job.TargetVolumeId!.Value,
                job.RequiredBytesTarget, job.SourceVolumeId, job.FreedBytesSource, afterCommit);
        }

        // Wake the processor now instead of letting it discover the jobs on its next safety poll.
        // Once for the batch: the signal means "there is work", not "there are N pieces of work".
        _signal.Signal();

        foreach (var (job, _, _) in created)
            _logger.LogInformation("Enqueued job {Id} type={Type} state={State}.", job.Id, job.Type, job.State);

        // Published after the commit (never inside the transaction): the queue rows and the overlays
        // both exist by now, so a client that reacts by re-reading sees what the push announced.
        foreach (var (job, _, _) in created)
        {
            await _realtime.JobStateChangedAsync(job);
            await _realtime.ProjectionChangedAsync(job);
        }

        return [.. created.Select(c => MapToDto(c.Job, c.Items, null))];
    }

    /// <summary>
    /// Re-raises a per-item build failure as the same kind of error (the controller maps both to
    /// 400) with the position of the offending element and the fate of the rest spelled out.
    /// </summary>
    private static Exception DescribeBatchFailure(Exception inner, int index, int total)
    {
        var message =
            $"Elemento {index + 1} di {total}: {inner.Message} " +
            "Nessuna operazione è stata accodata.";
        return inner is ArgumentException
            ? new ArgumentException(message, inner)
            : new InvalidOperationException(message, inner);
    }

    public async Task<FeasibilityResult> PreviewAsync(CreateJobRequest request, CancellationToken ct)
    {
        var (targetVolumeId, totalBytes) = await ResolvePreviewMetaAsync(request, ct);

        if (targetVolumeId is null || totalBytes == 0)
            return new FeasibilityResult(0, 0, long.MaxValue, 0, true, null, true);

        var vol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == targetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Target volume {targetVolumeId} not found.");

        // Prospective job: it would land at the end of the queue, so all active deltas apply
        // (planning view — promised liberations count, the queue materializes them in order).
        // The free bytes come from the drive when it answers, and EstimateIsLive says which of
        // the two it was: the UI must never dress a last-known figure as a live one.
        return await _spaceCheck.PlanAsync(
            vol, totalBytes, excludeJobId: null, sequenceOrder: null, ct);
    }

    public async Task<FeasibilityResult> PreviewBatchAsync(
        IReadOnlyList<CreateJobRequest> requests, CancellationToken ct)
    {
        // Aggregate the demand per target volume — the batch weighs on the ledger as a
        // whole, so it must be evaluated as a whole (previewing one file would lie).
        var demandByVolume = new Dictionary<int, long>();
        foreach (var request in requests)
        {
            var (targetVolumeId, totalBytes) = await ResolvePreviewMetaAsync(request, ct);
            if (targetVolumeId is null || totalBytes == 0)
                continue;
            demandByVolume[targetVolumeId.Value] =
                demandByVolume.GetValueOrDefault(targetVolumeId.Value) + totalBytes;
        }

        if (demandByVolume.Count == 0)
            return new FeasibilityResult(0, 0, long.MaxValue, 0, true, null, true);

        // Evaluate each involved volume; report the tightest one (smallest available−required
        // margin — for infeasible volumes that is the largest deficit).
        FeasibilityResult? tightest = null;
        foreach (var (volumeId, requiredBytes) in demandByVolume)
        {
            var vol = await _db.Volumes.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == volumeId, ct)
                ?? throw new InvalidOperationException($"Target volume {volumeId} not found.");

            var f = await _spaceCheck.PlanAsync(
                vol, requiredBytes, excludeJobId: null, sequenceOrder: null, ct);

            if (tightest is null ||
                f.AvailableEstimateBytes - f.RequiredBytes < tightest.AvailableEstimateBytes - tightest.RequiredBytes)
            {
                tightest = f;
            }
        }

        return tightest!;
    }

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.TargetVolume)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw EntityNotFoundException.For("Job", jobId);

        // The State concurrency token (finding #2) can trip if the engine commits a transition
        // between our read and our write. A cancel must win over any non-terminal state, so
        // reload and reapply; the loop only ends when Cancelled is committed or the job turned
        // terminal on its own.
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            if (TerminalStates.Contains(job.State))
                throw new InvalidOperationException($"Job {jobId} is already terminal ({job.State}).");

            job.State = JobState.Cancelled;
            job.CompletedUtc = DateTime.UtcNow;
            try
            {
                // Cancelled + ledger release commit atomically (finding #5): a crash in
                // between must not leave a phantom IsActive reservation.
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.SaveChangesAsync(ct);
                // §5 — the overlay dies with the job, in the same transaction as the terminal
                // state: an overlay that outlives its job shows a file in a folder it will
                // never reach. Cleared AFTER the state save, so a tripped concurrency token
                // (someone else owns the state) leaves the projection untouched.
                await _overlay.ClearForJobAsync(jobId, ct);
                // §5 — never a cascade of cancellations: whoever was waiting for this job stays
                // in the queue, parked on DependencyCancelled. In the same transaction as the
                // Cancelled state, or a crash leaves a dependent waiting for a job that is gone.
                await JobDependencies.ParkDependentsAsync(_db, job, ct);
                await SpaceLedger.DeactivateEntriesAsync(_db, jobId, ct);
                await tx.CommitAsync(ct);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxAttempts)
            {
                _logger.LogInformation(
                    "Cancel of job {Id}: state moved concurrently (attempt {N}) — reloading and reapplying.",
                    jobId, attempt);
                await _db.Entry(job).ReloadAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Out of attempts: surface a meaningful error instead of a raw EF exception.
                throw new InvalidOperationException(
                    $"Impossibile annullare il job {jobId}: lo stato continua a cambiare sotto la " +
                    "richiesta. Riprovare.", ex);
            }
        }

        // Signal the running job (if any) AFTER Cancelled is committed, so the engine both
        // sees the state on re-check and has its copy interrupted via the token.
        _cancellation.Cancel(jobId);

        // DB rows were deactivated inside the commit above — mirror it in memory.
        await _ledger.ReleaseInMemoryAsync(jobId, ct);

        // FIX #10-partial: a job cancelled while NOT running (e.g. checkpointed in Copying
        // after a shutdown, or Blocked with copied items) has orphan .fadit-partial files no
        // engine pass will ever sweep. A running job's engine does its own cleanup; here a
        // locked partial just logs and stays for the engine to remove.
        await CleanupPartialsAsync(job);

        // FIX #14: items already finalized on the target (or whose source is recycled)
        // must be re-pointed in the index, or the Catalog shows ghosts and the target
        // holds untracked copies. The engine repeats this for a running job — idempotent.
        await _indexUpdater.ReconcileCancelledJobAsync(job, ct);

        // §4 "i Blocked vengono rivalutati a ogni evento" — and a cancel IS one of the events:
        // it frees the entity this job was holding and the space it had reserved. Without this
        // the jobs it was blocking wait for a completion that will never come (finding 13).
        // Same entry point as the worker's post-completion pass, not a second path.
        // Cancelled is terminal and took the overlay with it (§5) — both events, after the commit
        // and BEFORE the revaluation below, so the cancelled job is announced ahead of the jobs
        // its cancellation unblocks; a client that sees a dependent released first would show the
        // consequence before the cause.
        await _realtime.JobStateChangedAsync(job);
        await _realtime.ProjectionChangedAsync(job);

        await _revaluator.RevaluateAsync(ct);
        _signal.Signal();

        _logger.LogInformation("Cancelled job {Id}.", jobId);
    }

    public async Task<OperationJobDto> RetryAsync(int jobId, CancellationToken ct)
    {
        var job = await _db.OperationJobs
            .Include(j => j.Items)
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct)
            ?? throw EntityNotFoundException.For("Job", jobId);

        if (job.State is not (JobState.Blocked or JobState.Failed))
            throw new InvalidOperationException(
                $"Job {jobId} is not retryable in state {job.State} (only Blocked or Failed).");

        // Leftover partials are garbage: the retry re-copies from scratch (fix #10).
        await CleanupPartialsAsync(job);

        // Reset every item whose copy did not reach finalization. Verified items keep their
        // finalized target file; Done items already lost their source — both must NOT re-copy.
        foreach (var item in job.Items.Where(i =>
                     i.State is JobItemState.Pending or JobItemState.Copying
                               or JobItemState.Copied or JobItemState.Failed))
        {
            item.State = JobItemState.Pending;
            item.BytesCopied = 0;
            item.ErrorMessage = null;
        }

        // «Riprova» is also the reactivation path of a job parked behind another one — including
        // one whose prerequisite was cancelled (DependencyCancelled), where the user's retry IS
        // the decision that voids the dependency. So the guard is re-asked from scratch: if
        // something is still in the way the job goes straight back to Blocked(DependencyPending)
        // on the CURRENT obstacle instead of running out of order.
        var conflict = await _unblocker.FindConflictAsync(job, ct);
        string? problem = null;

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var before = JobPathSnapshot.Of(job);

        if (conflict is null)
        {
            // Fresh snapshots before anything is committed (finding 8a): the paths this job was
            // queued with may name places the jobs it waited for have since moved.
            problem = await _unblocker.RefreshSnapshotsAsync(job, ct);
        }

        if (problem is not null)
        {
            // The refresh rewrote item paths (and possibly the job's volume) BEFORE it hit the
            // thing it could not resolve, and this method saves on the same tracked context — so
            // without this those half-applied edits would be committed alongside the Blocked
            // state. A job whose items name a path on one volume while the job names another is
            // exactly what PendingWorkGuard reads: a phantom claim that can park an unrelated,
            // legitimate job until the next release rewrites it.
            //
            // The snapshot is put back by hand rather than by reloading the rows: this method has
            // already reset item states and cleared TempPath above (the partials are physically
            // gone), and a reload would resurrect those values along with the paths.
            before.Restore(job);
        }

        if (conflict is not null || problem is not null)
        {
            job.State = JobState.Blocked;
            // A snapshot that will not resolve has no matching JobBlockReason: keeping the old
            // one (DependencyCancelled, say) next to a message about a missing file would name
            // the wrong cause. None + the message is the honest pairing, and the Coda shows the
            // message when the reason has no label.
            job.BlockReason = conflict is not null
                ? JobBlockReason.DependencyPending
                : JobBlockReason.None;
            job.ErrorMessage = conflict is not null ? DescribeDependency(conflict) : problem;
            job.DependsOnJobId = conflict?.JobId;
        }
        else
        {
            job.State = JobState.Pending;
            job.BlockReason = JobBlockReason.None;
            job.ErrorMessage = null;
            job.DependsOnJobId = null;
        }

        job.CompletedUtc = null;
        job.RetryCount++;
        // The state change and the overlay rewrite commit together: a Failed job dropped its
        // overlay, so putting it back in the queue without putting the projection back would
        // leave a queued operation invisible in Catalogo/Ricerca.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The State token tripped: something else (a cancel, the revaluator) moved the
            // job between our read and this write. Keep the committed state and tell the
            // caller in API terms, not with a raw EF exception.
            _db.ChangeTracker.Clear();
            var current = await _db.OperationJobs.AsNoTracking()
                .Where(j => j.Id == jobId).Select(j => j.State).FirstAsync(ct);
            throw new InvalidOperationException(
                $"Job {jobId} changed state concurrently (now {current}) — retry it again if still applicable.");
        }

        // Same single entry point as the enqueue, and idempotent: a Blocked job still carries
        // its overlay, so this rewrites the same values instead of doubling anything. A job that
        // goes back to Blocked on a dependency owns no entity and therefore writes nothing.
        if (JobDependencies.OwnsItsEntity(job))
            await _unblocker.TakeOverlayAsync(job, ct);
        // Committed before the ledger calls: those run on their own scope and connection, and
        // holding this write transaction open across them would be a self-inflicted SQLITE_BUSY.
        await tx.CommitAsync(ct);

        // Ledger coherence (same principle as the atomic enqueue, fix #2): a Failed job
        // released its reservation, an engine-Blocked one kept it, an enqueue-Blocked one
        // never had it — release-then-reserve normalizes all three to exactly one set. Same
        // rule, same numbers and the same guard as the release of a parked job (K3), and
        // uncancellable by signature: the Pending state is already committed, so a request that
        // gives up here would leave a runnable job whose demand the ledger does not know about.
        if (SpaceLedger.ReservationFor(job) is { } reservation)
            await _ledger.NormalizeReservationAsync(reservation);

        // A retried job is runnable again — wake the processor.
        _signal.Signal();

        _logger.LogInformation("Job {Id} manually retried (attempt {N}).", job.Id, job.RetryCount);

        // A retry rewrote the overlay (or left the job parked on a fresh obstacle): announce both.
        await _realtime.JobStateChangedAsync(job);
        await _realtime.ProjectionChangedAsync(job);

        return MapToDto(job, [.. job.Items], null);
    }

    /// <summary>
    /// What a refresh is allowed to rewrite: the job's volumes, its destination, and every item's
    /// source/target path. Captured before <c>RefreshSnapshotsAsync</c> so a refresh that gives up
    /// halfway can be undone precisely, without disturbing the item states and <c>TempPath</c>s the
    /// retry has already reset (a blanket row reload would put those back too).
    /// </summary>
    private sealed record JobPathSnapshot(
        int? SourceVolumeId,
        int? TargetVolumeId,
        string? TargetRelativePath,
        IReadOnlyList<(OperationJobItem Item, string Source, string Target)> Items)
    {
        public static JobPathSnapshot Of(OperationJob job) => new(
            job.SourceVolumeId,
            job.TargetVolumeId,
            job.TargetRelativePath,
            [.. job.Items.Select(i => (i, i.SourceRelativePath, i.TargetRelativePath))]);

        public void Restore(OperationJob job)
        {
            job.SourceVolumeId = SourceVolumeId;
            job.TargetVolumeId = TargetVolumeId;
            job.TargetRelativePath = TargetRelativePath;
            foreach (var (item, source, target) in Items)
            {
                item.SourceRelativePath = source;
                item.TargetRelativePath = target;
            }
        }
    }

    /// <summary>
    /// Removes this job's leftover partials — the same rule the engine applies, now written once
    /// in <see cref="PartialCleanup"/> (K2). It persists the cleared pointers itself, with an
    /// uncancellable token: the local copy left that to the caller, so a request whose token
    /// tripped (or a retry transaction that rolled back) recycled the file and kept a
    /// <c>TempPath</c> naming it.
    /// </summary>
    private Task CleanupPartialsAsync(OperationJob job) =>
        PartialCleanup.RemoveAsync(_db, _mover, _logger, job);

    public async Task<PagedResult<OperationJobDto>> ListAsync(int skip, int take, CancellationToken ct)
    {
        var total = await _db.OperationJobs.CountAsync(ct);

        // E1 — the items are NOT loaded. The list needs exactly one thing out of them, the source
        // path it shows in the row, and `.Include(j => j.Items)` bought that with every item of
        // every job on the page: a cross-volume MoveFolder of 100 000 files materialised 100 000
        // entities so the screen could print one path. The volumes stay included — they are one
        // row each and the DTO shows their labels.
        var jobs = await _db.OperationJobs
            .Include(j => j.SourceVolume)
            .Include(j => j.TargetVolume)
            .OrderBy(j => j.SequenceOrder)
            .Skip(skip)
            .Take(take)
            .AsNoTracking()
            .ToListAsync(ct);

        var sourcePaths = await FirstSourcePathsAsync([.. jobs.Select(j => j.Id)], ct);

        var dtos = new List<OperationJobDto>(jobs.Count);
        foreach (var job in jobs)
        {
            FeasibilityResult? feasibility = null;
            // Only for a job that has a space question at all: an intra-volume op or one with
            // nothing to reserve would get the "feasible by construction" placeholder, and a
            // placeholder on the wire is a number someone will eventually read as real.
            if (job.State == JobState.Blocked && job.TargetVolume is not null &&
                !job.IsIntraVolume && job.RequiredBytesTarget > 0)
            {
                // Hard view: the deficit shown for a Blocked job must explain the block, i.e.
                // match the engine's execution-time re-check — same object, same live figure,
                // same margin — instead of a planning estimate that would quote a different
                // number than the one that parked the job.
                feasibility = (await _spaceCheck.EvaluateHardAsync(job, ct)).Feasibility;
            }
            dtos.Add(MapToDto(job, sourcePaths.GetValueOrDefault(job.Id), feasibility));
        }

        return new PagedResult<OperationJobDto>(dtos, total, skip, take);
    }

    /// <summary>
    /// The source path each of these jobs shows in the queue row: the one belonging to its FIRST
    /// item, which is what <c>items.FirstOrDefault()</c> returned when the whole collection was
    /// loaded. "First" is pinned to the lowest item id — the insertion order — instead of being
    /// left to whatever order the database happened to return, so a job's row cannot start
    /// showing a different one of its files after an unrelated change of plan (E1).
    ///
    /// Two aggregate round trips for the whole page rather than one per job, and at most one row
    /// materialised per job either way: the first picks the minimum id per job (an aggregate over
    /// the <c>JobId</c> index, no rows returned), the second reads the paths of exactly those ids.
    /// </summary>
    private async Task<Dictionary<int, string>> FirstSourcePathsAsync(
        IReadOnlyList<int> jobIds, CancellationToken ct)
    {
        if (jobIds.Count == 0) return [];

        var firstItemIds = await _db.OperationJobItems.AsNoTracking()
            .Where(i => jobIds.Contains(i.JobId))
            .GroupBy(i => i.JobId)
            .Select(g => g.Min(i => i.Id))
            .ToListAsync(ct);

        return await _db.OperationJobItems.AsNoTracking()
            .Where(i => firstItemIds.Contains(i.Id))
            .Select(i => new { i.JobId, i.SourceRelativePath })
            .ToDictionaryAsync(i => i.JobId, i => i.SourceRelativePath, ct);
    }

    // ── private: job building ─────────────────────────────────────────────────

    /// <summary>
    /// How many times the queue re-picks a sequence number before giving up. A collision means
    /// another enqueue committed between our <c>MAX</c> and our <c>INSERT</c>; a handful of
    /// retries covers any realistic burst, and a bound is what stops a livelock from turning an
    /// API call into an infinite loop.
    /// </summary>
    private const int MaxSequenceOrderAttempts = 5;

    /// <summary>
    /// C26 — assigns <see cref="OperationJob.SequenceOrder"/> and inserts the job, INSIDE the
    /// caller's transaction. The old code read <c>MAX(SequenceOrder) + 1</c> outside it, with no
    /// uniqueness constraint behind it, so two concurrent enqueues could share a number — and the
    /// FIFO feasibility (which only skips entries with <c>SequenceOrder &gt; mine</c>) then
    /// double-counts the two jobs against each other.
    ///
    /// The unique index is the arbiter, not the read: whoever loses the race gets a constraint
    /// violation and picks the next number. A UNIQUE violation aborts the statement, not the
    /// transaction, so the retry stays inside it — and because we hold the write lock from the
    /// first attempt on, the re-read cannot miss a competitor that committed in the meantime.
    /// </summary>
    private async Task AssignSequenceOrderAndSaveAsync(OperationJob job, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            job.SequenceOrder = (await _db.OperationJobs.MaxAsync(j => (int?)j.SequenceOrder, ct) ?? 0) + 1;
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex)
            {
                // Ask the database whether OUR index is the one that fired, instead of parsing the
                // provider's error text — that would tie Business to SQLite (§3).
                bool taken = await _db.OperationJobs
                    .AnyAsync(j => j.SequenceOrder == job.SequenceOrder, ct);
                if (!taken) throw;

                if (attempt >= MaxSequenceOrderAttempts)
                    throw new InvalidOperationException(
                        $"Impossibile accodare l'operazione: la posizione in coda " +
                        $"{job.SequenceOrder} continua a essere occupata da altri inserimenti " +
                        $"dopo {MaxSequenceOrderAttempts} tentativi. Riprovare.", ex);

                _logger.LogWarning(ex,
                    "Enqueue: sequence order {Order} was taken by a concurrent enqueue " +
                    "(attempt {N}/{Max}) — picking the next one.",
                    job.SequenceOrder, attempt, MaxSequenceOrderAttempts);
            }
        }
    }

    /// <param name="committedDemandOnTarget">
    /// Bytes the jobs built earlier in the SAME batch have already committed to this request's
    /// target volume. Zero for a lone enqueue. Only the space verdict uses it — the job's own
    /// <see cref="OperationJob.RequiredBytesTarget"/> stays the honest size of this operation.
    /// </param>
    private async Task<(OperationJob job, List<OperationJobItem> items, bool shouldReserve)>
        BuildJobAsync(CreateJobRequest request, long committedDemandOnTarget, CancellationToken ct)
    {
        var job = new OperationJob
        {
            Type = request.Type,
            State = JobState.Pending,
            BlockReason = JobBlockReason.None,
            EstimateIsLive = true
        };

        List<OperationJobItem> items = [];
        bool shouldReserve = false;

        switch (request.Type)
        {
            case JobType.CreateFolder:
                await BuildCreateFolderAsync(request, job, ct);
                break;
            case JobType.RenameFile:
                await BuildRenameFileAsync(request, job, items, ct);
                break;
            case JobType.RenameFolder:
                await BuildRenameFolderAsync(request, job, items, ct);
                break;
            case JobType.MoveFile:
                shouldReserve = await BuildMoveFileAsync(request, job, items, committedDemandOnTarget, ct);
                break;
            case JobType.MoveFolder:
                shouldReserve = await BuildMoveFolderAsync(request, job, items, committedDemandOnTarget, ct);
                break;
            default:
                throw new InvalidOperationException($"Unsupported job type: {request.Type}");
        }

        // FIX #3 — a job whose volumes are not connected is BORN Blocked(offline): never rejected
        // (§4 "non rifiutare mai un job all'enqueue"), never Pending-then-Failed. Applied after the
        // type-specific build so it sees the resolved source/target, and after the space evaluation
        // so an offline volume — the actionable blocker — wins over a deficit computed on an
        // estimate that is stale by definition.
        await ApplyOfflineGateAsync(job, ct);

        foreach (var item in items)
            item.Job = job;

        return (job, items, shouldReserve);
    }

    /// <summary>
    /// Parks the job when a volume it needs is offline. <c>shouldReserve</c> is deliberately left
    /// as the space evaluation computed it: a job parked on offline keeps its reservation so the
    /// bytes it will need at the remount stay committed to it and no other job overcommits them.
    /// </summary>
    private async Task ApplyOfflineGateAsync(OperationJob job, CancellationToken ct)
    {
        var source = await LoadVolumeAsync(job.SourceVolumeId, ct);
        var target = job.TargetVolumeId == job.SourceVolumeId
            ? source
            : await LoadVolumeAsync(job.TargetVolumeId, ct);

        var reason = VolumeOfflineGate.Evaluate(source, target);
        if (reason == JobBlockReason.None)
            return;

        job.State = JobState.Blocked;
        job.BlockReason = reason;
        job.ErrorMessage = VolumeOfflineGate.Describe(reason, source, target);

        // Whatever the estimate says about an unmounted target, it is not live data.
        if (target is { IsOnline: false })
            job.EstimateIsLive = false;

        _logger.LogInformation(
            "Enqueue of a {Type} job parked at birth: {Reason} (source={Src}, target={Tgt}).",
            job.Type, reason, job.SourceVolumeId, job.TargetVolumeId);
    }

    /// <summary>
    /// Serializes operations that touch the same place (MVP: one pending operation per entity,
    /// §5). A conflict does NOT refuse the request — §4 is explicit that an enqueue is never
    /// rejected: the job enters the queue <see cref="JobState.Blocked"/> with
    /// <see cref="JobBlockReason.DependencyPending"/>, naming the job that holds the entity, and
    /// the revaluation releases it when that job is done.
    ///
    /// Its verdict wins over the offline gate's: the dependency is what decides whether this job
    /// owns the entity — and therefore whether it may write the projection overlay at all. Once
    /// the prerequisite resolves, the revaluation re-applies the offline and space gates, so a
    /// job that is also waiting for a drive is re-parked on that reason then, with nothing lost.
    ///
    /// Asks through the SAME entry point the revaluation and «Riprova» use, so the question is
    /// posed once in the codebase and always with the same bound: only jobs AHEAD in the queue.
    /// </summary>
    private async Task ApplyPendingWorkGuardAsync(OperationJob job, CancellationToken ct)
    {
        var conflict = await _unblocker.FindConflictAsync(job, ct);
        if (conflict is null) return;

        job.State = JobState.Blocked;
        job.BlockReason = JobBlockReason.DependencyPending;
        job.DependsOnJobId = conflict.JobId;
        job.ErrorMessage = DescribeDependency(conflict);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Enqueue of a {Type} job parked behind job {Prerequisite} ({PrereqType} on '{Path}').",
            job.Type, conflict.JobId, conflict.Type, conflict.Path);
    }

    /// <summary>The user-facing reason a job is waiting, in Italian, naming the job it waits for.</summary>
    internal static string DescribeDependency(PendingConflict conflict) =>
        $"In attesa dell'operazione #{conflict.JobId} ({conflict.Type}) su '{conflict.Path}': " +
        "una sola operazione per volta sulla stessa entità.";

    private Task<Volume?> LoadVolumeAsync(int? volumeId, CancellationToken ct) =>
        volumeId is null
            ? Task.FromResult<Volume?>(null)
            : _db.Volumes.AsNoTracking().FirstOrDefaultAsync(v => v.Id == volumeId.Value, ct);

    private async Task BuildCreateFolderAsync(CreateJobRequest req, OperationJob job, CancellationToken ct)
    {
        if (req.TargetVolumeId is null || req.TargetRelativePath is null)
            throw new ArgumentException("CreateFolder requires TargetVolumeId and TargetRelativePath.");

        if (!OperationName.TryValidatePath(req.TargetRelativePath, allowRoot: false, out var folderPath, out var pathError))
            throw new ArgumentException(pathError);

        if (!await _db.Volumes.AnyAsync(v => v.Id == req.TargetVolumeId.Value, ct))
            throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        job.TargetVolumeId = req.TargetVolumeId;
        job.TargetRelativePath = folderPath;
        job.IsIntraVolume = true;
    }

    private async Task BuildRenameFileAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceFileId is null || req.NewName is null)
            throw new ArgumentException("RenameFile requires SourceFileId and NewName.");

        if (!OperationName.TryValidateLeaf(req.NewName, out var nameError))
            throw new ArgumentException(nameError);

        var file = await _db.Files
            .Include(f => f.Directory)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct)
            ?? throw new InvalidOperationException($"File {req.SourceFileId} not found.");

        var srcPath = ScanPath.Join(file.Directory.MaterializedPath, file.Name);
        var dstPath = ScanPath.Join(file.Directory.MaterializedPath, req.NewName);

        job.SourceVolumeId = file.VolumeId;
        job.TargetVolumeId = file.VolumeId;
        job.TargetRelativePath = req.NewName;
        job.IsIntraVolume = true;

        items.Add(new OperationJobItem
        {
            FileId = file.Id,
            SourceRelativePath = srcPath,
            TargetRelativePath = dstPath,
            SizeBytes = file.SizeBytes,
            State = JobItemState.Pending
        });
    }

    private async Task BuildRenameFolderAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, CancellationToken ct)
    {
        if (req.SourceDirectoryId is null || req.NewName is null)
            throw new ArgumentException("RenameFolder requires SourceDirectoryId and NewName.");

        if (!OperationName.TryValidateLeaf(req.NewName, out var nameError))
            throw new ArgumentException(nameError);

        var dir = await LoadSourceDirectoryAsync(req.SourceDirectoryId.Value, ct);

        var parentPath = ScanPath.Parent(dir.MaterializedPath);
        var dstPath = ScanPath.Join(parentPath, req.NewName);

        job.SourceVolumeId = dir.VolumeId;
        job.TargetVolumeId = dir.VolumeId;
        job.TargetRelativePath = req.NewName;
        job.IsIntraVolume = true;

        items.Add(new OperationJobItem
        {
            FileId = null,
            SourceRelativePath = dir.MaterializedPath,
            TargetRelativePath = dstPath,
            State = JobItemState.Pending
        });
    }

    private async Task<bool> BuildMoveFileAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, long committedDemandOnTarget, CancellationToken ct)
    {
        if (req.SourceFileId is null || req.TargetVolumeId is null || req.TargetRelativePath is null)
            throw new ArgumentException("MoveFile requires SourceFileId, TargetVolumeId and TargetRelativePath.");

        if (!OperationName.TryValidatePath(req.TargetRelativePath, allowRoot: true, out var targetPath, out var pathError))
            throw new ArgumentException(pathError);

        var file = await _db.Files
            .Include(f => f.Directory)
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct)
            ?? throw new InvalidOperationException($"File {req.SourceFileId} not found.");

        var targetVol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == req.TargetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        bool intra = file.VolumeId == targetVol.Id;
        var srcPath = ScanPath.Join(file.Directory.MaterializedPath, file.Name);
        var dstPath = ScanPath.Join(targetPath, file.Name);

        job.SourceVolumeId = file.VolumeId;
        job.TargetVolumeId = targetVol.Id;
        job.TargetRelativePath = dstPath;
        job.IsIntraVolume = intra;

        if (!intra)
        {
            job.TotalBytes = file.SizeBytes;
            job.RequiredBytesTarget = file.SizeBytes;
            job.FreedBytesSource = file.SizeBytes;

            // Weighed together with everything this batch has already promised to the same
            // volume (0 for a lone enqueue): the batch is one demand, and judging its last file
            // against free space that ignores its first forty-nine would declare a selection
            // feasible that plainly is not.
            var f = await _spaceCheck.PlanAsync(
                targetVol, committedDemandOnTarget + file.SizeBytes,
                excludeJobId: null, sequenceOrder: null, ct);

            job.EstimateIsLive = f.EstimateIsLive;
            if (!f.Feasible)
            {
                job.State = JobState.Blocked;
                job.BlockReason = JobBlockReason.InsufficientSpace;
            }
        }

        items.Add(new OperationJobItem
        {
            FileId = file.Id,
            SourceRelativePath = srcPath,
            TargetRelativePath = dstPath,
            SizeBytes = file.SizeBytes,
            State = JobItemState.Pending
        });

        return !intra && job.State == JobState.Pending;
    }

    private async Task<bool> BuildMoveFolderAsync(CreateJobRequest req, OperationJob job,
        List<OperationJobItem> items, long committedDemandOnTarget, CancellationToken ct)
    {
        if (req.SourceDirectoryId is null || req.TargetVolumeId is null || req.TargetRelativePath is null)
            throw new ArgumentException("MoveFolder requires SourceDirectoryId, TargetVolumeId and TargetRelativePath.");

        if (!OperationName.TryValidatePath(req.TargetRelativePath, allowRoot: true, out var targetPath, out var pathError))
            throw new ArgumentException(pathError);

        var dir = await LoadSourceDirectoryAsync(req.SourceDirectoryId.Value, ct);

        var targetVol = await _db.Volumes.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == req.TargetVolumeId.Value, ct)
            ?? throw new InvalidOperationException($"Volume {req.TargetVolumeId} not found.");

        bool intra = dir.VolumeId == targetVol.Id;
        var dstDirPath = ScanPath.Join(targetPath, dir.Name);

        // C22: geometrically impossible or pointless moves are a 400 at enqueue, not a
        // Failed job at execution. Both checks are intra-volume only — on another volume
        // the same relative path is a different physical location.
        if (intra)
        {
            if (ScanPath.IsWithin(targetPath, dir.MaterializedPath))
                throw new ArgumentException(
                    $"Impossibile spostare la cartella '{dir.MaterializedPath}' dentro sé stessa " +
                    $"o in una sua sottocartella ('{targetPath}').");

            if (string.Equals(dstDirPath, dir.MaterializedPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"La cartella '{dir.MaterializedPath}' si trova già in questa posizione.");
        }

        job.SourceVolumeId = dir.VolumeId;
        job.TargetVolumeId = targetVol.Id;
        job.TargetRelativePath = dstDirPath;
        job.IsIntraVolume = intra;

        if (intra)
        {
            items.Add(new OperationJobItem
            {
                FileId = null,
                SourceRelativePath = dir.MaterializedPath,
                TargetRelativePath = dstDirPath,
                State = JobItemState.Pending
            });
            return false;
        }

        // Root marker item (FileId = null): represents the folder itself. It gives the
        // engine the source root path and guarantees real work happens — the target folder
        // is created even when the subtree holds no indexed file (C21: an empty or
        // all-excluded folder must never reach Completed without a syscall).
        items.Add(new OperationJobItem
        {
            FileId = null,
            SourceRelativePath = dir.MaterializedPath,
            TargetRelativePath = dstDirPath,
            SizeBytes = 0,
            State = JobItemState.Pending
        });

        // Cross-volume: expand to one item per file in the subtree.
        var expanded = await ExpandSubtreeAsync(dir, dstDirPath, ct);
        items.AddRange(expanded);

        long total = expanded.Sum(i => i.SizeBytes);
        job.TotalBytes = total;
        job.RequiredBytesTarget = total;
        job.FreedBytesSource = total;

        if (total > 0)
        {
            // Same rule as MoveFile: the verdict is taken on the batch's cumulative demand.
            var f = await _spaceCheck.PlanAsync(
                targetVol, committedDemandOnTarget + total, excludeJobId: null, sequenceOrder: null, ct);

            job.EstimateIsLive = f.EstimateIsLive;
            if (!f.Feasible)
            {
                job.State = JobState.Blocked;
                job.BlockReason = JobBlockReason.InsufficientSpace;
            }
        }

        return job.State == JobState.Pending && total > 0;
    }

    // ── private: subtree expansion ────────────────────────────────────────────

    private async Task<List<OperationJobItem>> ExpandSubtreeAsync(
        DirectoryNode sourceDir, string dstDirPath, CancellationToken ct)
    {
        var srcPath = sourceDir.MaterializedPath;

        var dirIds = await _db.Directories
            .InSubtree(sourceDir.VolumeId, srcPath)
            .Select(d => new { d.Id, d.MaterializedPath })
            .ToListAsync(ct);

        var dirIdSet = dirIds.ToDictionary(d => d.Id, d => d.MaterializedPath);

        var files = await _db.Files
            .AsNoTracking()
            .Where(f => f.IsPresent && f.IsIncluded && dirIdSet.Keys.Contains(f.DirectoryId))
            .Select(f => new { f.Id, f.DirectoryId, f.Name, f.SizeBytes })
            .ToListAsync(ct);

        return files.Select(f =>
        {
            var dirMatPath = dirIdSet[f.DirectoryId];
            // strip source dir prefix to get the relative-within-subtree path
            var relWithinSrc = dirMatPath.Length > srcPath.Length
                ? dirMatPath[(srcPath.Length + 1)..]
                : string.Empty;

            var srcFilePath = ScanPath.Join(dirMatPath, f.Name);
            var dstFilePath = relWithinSrc.Length > 0
                ? dstDirPath + "\\" + relWithinSrc + "\\" + f.Name
                : ScanPath.Join(dstDirPath, f.Name);

            return new OperationJobItem
            {
                FileId = f.Id,
                SourceRelativePath = srcFilePath,
                TargetRelativePath = dstFilePath,
                SizeBytes = f.SizeBytes,
                State = JobItemState.Pending
            };
        }).ToList();
    }

    // ── private: guards ────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the source directory of a folder operation. A row the last scan no longer
    /// found on disk (<c>IsPresent = false</c>) is not a legal source: the catalog keeps
    /// it (no hard-delete, §6) but there is nothing to rename or move. A row carrying an
    /// overlay is still a legal source in principle — the enqueue guard is what serializes
    /// it — so the check is limited to absent rows with nothing pending on them.
    /// </summary>
    private async Task<DirectoryNode> LoadSourceDirectoryAsync(int directoryId, CancellationToken ct)
    {
        var dir = await _db.Directories.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == directoryId, ct)
            ?? throw new InvalidOperationException($"Directory {directoryId} not found.");

        if (!dir.IsPresent && dir.PendingState == EntityPendingState.None)
            throw new InvalidOperationException(
                $"La cartella '{dir.MaterializedPath}' non è più presente sul volume: " +
                "l'ultima scansione non l'ha trovata sul disco.");

        return dir;
    }

    // ── private: preview meta (no guards, no side effects) ────────────────────

    private async Task<(int? targetVolumeId, long totalBytes)> ResolvePreviewMetaAsync(
        CreateJobRequest req, CancellationToken ct)
    {
        switch (req.Type)
        {
            case JobType.CreateFolder:
                return (req.TargetVolumeId, 0);

            case JobType.RenameFile:
            case JobType.RenameFolder:
                return (null, 0); // always intra-volume

            case JobType.MoveFile when req.SourceFileId.HasValue && req.TargetVolumeId.HasValue:
            {
                var file = await _db.Files.AsNoTracking()
                    .Select(f => new { f.Id, f.VolumeId, f.SizeBytes })
                    .FirstOrDefaultAsync(f => f.Id == req.SourceFileId.Value, ct);
                if (file is null) return (req.TargetVolumeId, 0);
                bool intra = file.VolumeId == req.TargetVolumeId.Value;
                return (req.TargetVolumeId, intra ? 0 : file.SizeBytes);
            }

            case JobType.MoveFolder when req.SourceDirectoryId.HasValue && req.TargetVolumeId.HasValue:
            {
                var dir = await _db.Directories.AsNoTracking()
                    .Select(d => new { d.Id, d.VolumeId, d.MaterializedPath })
                    .FirstOrDefaultAsync(d => d.Id == req.SourceDirectoryId.Value, ct);
                if (dir is null) return (req.TargetVolumeId, 0);
                bool intra = dir.VolumeId == req.TargetVolumeId.Value;
                if (intra) return (req.TargetVolumeId, 0);

                var dirIds = await _db.Directories
                    .InSubtree(dir.VolumeId, dir.MaterializedPath)
                    .Select(d => d.Id)
                    .ToListAsync(ct);

                var total = await _db.Files.AsNoTracking()
                    .Where(f => f.IsPresent && f.IsIncluded && dirIds.Contains(f.DirectoryId))
                    .SumAsync(f => f.SizeBytes, ct);

                return (req.TargetVolumeId, total);
            }

            default:
                return (null, 0);
        }
    }

    // ── private: mapping ───────────────────────────────────────────────────────

    /// <summary>
    /// For a caller that already holds the job's items — the enqueue and the retry, which have
    /// just written them. The only thing the DTO wants out of them is the first source path.
    /// </summary>
    private static OperationJobDto MapToDto(OperationJob job, List<OperationJobItem> items,
        FeasibilityResult? feasibility) =>
        MapToDto(job, items.OrderBy(i => i.Id).FirstOrDefault()?.SourceRelativePath, feasibility);

    /// <summary>
    /// For a caller that read the source path without loading the items — the list (E1), where
    /// loading them would mean one entity per file of every job on the page.
    /// </summary>
    private static OperationJobDto MapToDto(OperationJob job, string? sourcePath,
        FeasibilityResult? feasibility)
    {
        return new OperationJobDto
        {
            Id = job.Id,
            Type = job.Type.ToString(),
            State = job.State.ToString(),
            BlockReason = job.BlockReason.ToString(),
            SourceVolumeId = job.SourceVolumeId,
            SourceVolumeLabel = job.SourceVolume?.Label,
            TargetVolumeId = job.TargetVolumeId,
            TargetVolumeLabel = job.TargetVolume?.Label,
            SourcePath = sourcePath,
            TargetPath = job.TargetRelativePath,
            IsIntraVolume = job.IsIntraVolume,
            TotalBytes = job.TotalBytes,
            BytesProcessed = job.BytesProcessed,
            RequiredBytesTarget = job.RequiredBytesTarget,
            FreedBytesSource = job.FreedBytesSource,
            EstimateIsLive = job.EstimateIsLive,
            SequenceOrder = job.SequenceOrder,
            DependsOnJobId = job.DependsOnJobId,
            ErrorMessage = job.ErrorMessage,
            CreatedUtc = job.CreatedUtc,
            StartedUtc = job.StartedUtc,
            CompletedUtc = job.CompletedUtc,
            Feasibility = feasibility
        };
    }

}
