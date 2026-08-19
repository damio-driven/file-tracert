using FileTracert.Business.Operations;
using FileTracert.Contracts.Operations;
using FileTracert.Data;
using FileTracert.Tests.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Unit tests for SpaceLedger: verifies the FIFO space math (reserve/release/compute)
/// and DB persistence/rebuild. Uses a real SQLite in-memory DB via SqliteInMemoryContext.
/// </summary>
public sealed class SpaceLedgerTests : IDisposable
{
    private readonly SqliteInMemoryContext _harness;
    private readonly SpaceLedger _ledger;

    public SpaceLedgerTests()
    {
        _harness = new SqliteInMemoryContext();

        // EnsureCreated() opens the connection and EF Core runs PRAGMA foreign_keys = ON.
        // These tests validate business math, not DB schema constraints — reset it.
        using (var setup = _harness.CreateContext())
            setup.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");

        _ledger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
    }

    public void Dispose() => _harness.Dispose();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static IServiceScopeFactory CreateScopeFactory(SqliteInMemoryContext harness)
    {
        var services = new ServiceCollection();
        // Each scope gets a fresh DbContext backed by the shared in-memory connection.
        services.AddScoped<FileTracertDbContext>(_ => harness.CreateContext());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private Task<FeasibilityResult> Compute(int volId, long free, long required, bool online = true,
                                            int? excludeJobId = null, int? sequenceOrder = null,
                                            bool includeQueuedLiberations = true, long marginBytes = 0) =>
        _ledger.ComputeFeasibilityAsync(volId, free, online, required, marginBytes, excludeJobId, sequenceOrder,
            includeQueuedLiberations, CancellationToken.None);

    // SequenceOrder defaults to the job id — tests that care about FIFO pass it explicitly.
    private Task Reserve(int jobId, int targetVol, long required, int? srcVol = null, long freed = 0,
                         int? sequenceOrder = null) =>
        _ledger.ReserveAsync(jobId, sequenceOrder ?? jobId, targetVol, required, srcVol, freed,
            CancellationToken.None);

    private Task Release(int jobId) =>
        _ledger.ReleaseAsync(jobId, CancellationToken.None);

    /// <summary>RebuildFromDbAsync joins SequenceOrder back from OperationJobs — seed the row.</summary>
    private void SeedJobRow(int jobId, int sequenceOrder,
        FileTracert.Contracts.Enums.JobState state = FileTracert.Contracts.Enums.JobState.Pending)
    {
        using var db = _harness.CreateContext();
        db.OperationJobs.Add(new FileTracert.Data.Entities.OperationJob
        {
            Id = jobId,
            Type = FileTracert.Contracts.Enums.JobType.MoveFile,
            State = state,
            SequenceOrder = sequenceOrder,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ── no entries ───────────────────────────────────────────────────────────

    [Fact]
    public async Task No_reservations_returns_full_free_space()
    {
        var r = await Compute(volId: 1, free: 1000, required: 500);

        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(1000);
        r.ReservedBytes.Should().Be(0);
        r.DeficitBytes.Should().Be(0);
    }

    [Fact]
    public async Task Zero_required_bytes_always_feasible()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 900);

        var r = await Compute(volId: 1, free: 1000, required: 0);

        r.Feasible.Should().BeTrue();
        r.DeficitBytes.Should().Be(0);
    }

    // ── reservation reduces available ────────────────────────────────────────

    [Fact]
    public async Task Reservation_reduces_available_space()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 400);

        var r = await Compute(volId: 1, free: 1000, required: 100);

        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(600);
        r.ReservedBytes.Should().Be(400);
    }

    [Fact]
    public async Task Reservation_causes_infeasibility_when_not_enough_space()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 800);

        var r = await Compute(volId: 1, free: 1000, required: 300);

        r.Feasible.Should().BeFalse();
        r.AvailableEstimateBytes.Should().Be(200);
        r.DeficitBytes.Should().Be(100);
        r.BlockingVolumeId.Should().Be(1);
    }

    [Fact]
    public async Task Multiple_reservations_accumulate_FIFO()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 300);
        await Reserve(jobId: 2, targetVol: 1, required: 400);

        // available = 1000 - 300 - 400 = 300
        var r = await Compute(volId: 1, free: 1000, required: 200);
        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(300);

        var r2 = await Compute(volId: 1, free: 1000, required: 400);
        r2.Feasible.Should().BeFalse();
        r2.DeficitBytes.Should().Be(100);
    }

    // ── liberation increases available on source ──────────────────────────────

    [Fact]
    public async Task Liberation_on_source_increases_available_on_source_volume()
    {
        // Job 1: move 300 bytes FROM vol 2 → TO vol 1
        // vol 1 gets +300 (reservation), vol 2 gets -300 (liberation)
        await Reserve(jobId: 1, targetVol: 1, required: 300, srcVol: 2, freed: 300);

        // Vol 2 had 500 free; job 1 will release 300 → plannable = 500 - (-300) = 800
        var r = await Compute(volId: 2, free: 500, required: 750);
        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(800); // 500 − (−300) = 800
    }

    // FIX #2-FIFO: planning view credits promised liberations, the hard view must not.
    [Fact]
    public async Task Hard_view_ignores_unmaterialized_liberations_but_keeps_reservations()
    {
        // Job 1 (seq 1): move 300 bytes FROM vol 2 → TO vol 1: +300 on vol 1, −300 on vol 2.
        await Reserve(jobId: 1, targetVol: 1, required: 300, srcVol: 2, freed: 300);

        // Planning (enqueue/preview): vol 2 can count on the future liberation.
        var planning = await Compute(volId: 2, free: 500, required: 750,
            excludeJobId: 9, sequenceOrder: 9, includeQueuedLiberations: true);
        planning.Feasible.Should().BeTrue();
        planning.AvailableEstimateBytes.Should().Be(800);

        // Hard (execution re-check): the 300 bytes are not on disk until job 1 completes.
        var hard = await Compute(volId: 2, free: 500, required: 750,
            excludeJobId: 9, sequenceOrder: 9, includeQueuedLiberations: false);
        hard.Feasible.Should().BeFalse("a liberation is a promise, not physical space");
        hard.AvailableEstimateBytes.Should().Be(500);
        hard.DeficitBytes.Should().Be(250);

        // Reservations (positive deltas) still count in the hard view.
        var hardTarget = await Compute(volId: 1, free: 1000, required: 800,
            excludeJobId: 9, sequenceOrder: 9, includeQueuedLiberations: false);
        hardTarget.Feasible.Should().BeFalse();
        hardTarget.AvailableEstimateBytes.Should().Be(700);
    }

    [Fact]
    public async Task Reservation_on_source_volume_does_not_affect_target_computation()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 400, srcVol: 2, freed: 400);

        // Checking vol 1 (target): sees the +400 reservation
        var rTarget = await Compute(volId: 1, free: 1000, required: 100);
        rTarget.AvailableEstimateBytes.Should().Be(600);

        // Checking vol 2 (source): sees the -400 liberation (increases available)
        var rSource = await Compute(volId: 2, free: 200, required: 550);
        rSource.Feasible.Should().BeTrue(); // 200 − (−400) = 600 ≥ 550
    }

    // ── release restores space ────────────────────────────────────────────────

    [Fact]
    public async Task Release_removes_reservation_from_available()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 500);
        (await Compute(volId: 1, free: 1000, required: 600)).Feasible.Should().BeFalse();

        await Release(jobId: 1);

        var r = await Compute(volId: 1, free: 1000, required: 600);
        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(1000);
    }

    [Fact]
    public async Task Release_removes_both_reservation_and_liberation()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 500, srcVol: 2, freed: 300);
        await Release(jobId: 1);

        // Vol 1: back to full free
        (await Compute(volId: 1, free: 1000, required: 999)).Feasible.Should().BeTrue();
        // Vol 2: back to unmodified free
        (await Compute(volId: 2, free: 100, required: 100)).Feasible.Should().BeTrue();
        (await Compute(volId: 2, free: 100, required: 101)).Feasible.Should().BeFalse();
    }

    [Fact]
    public async Task Releasing_one_job_does_not_affect_other_jobs_entries()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 300);
        await Reserve(jobId: 2, targetVol: 1, required: 200);

        await Release(jobId: 1);

        // Only job 2's 200 remains
        var r = await Compute(volId: 1, free: 1000, required: 801);
        r.Feasible.Should().BeFalse();
        r.AvailableEstimateBytes.Should().Be(800);
    }

    // ── FIX #2: excludeJobId — a job's own reservation must not count against it ──

    [Fact]
    public async Task Own_reservation_is_excluded_when_rechecking_an_enqueued_job()
    {
        // 800 required, 1000 free: feasible at enqueue. The enqueue writes +800.
        await Reserve(jobId: 1, targetVol: 1, required: 800, sequenceOrder: 1);

        // Without the exclusion the recheck would see available = 1000-800 = 200 < 800.
        var r = await Compute(volId: 1, free: 1000, required: 800, excludeJobId: 1, sequenceOrder: 1);

        r.Feasible.Should().BeTrue("the job's own reservation must not be double-counted");
        r.AvailableEstimateBytes.Should().Be(1000);
        r.ReservedBytes.Should().Be(0);
    }

    [Fact]
    public async Task Exclusion_removes_both_own_reservation_and_own_liberation()
    {
        // Job 1 moves 500 from vol 2 to vol 1: +500 on vol 1, -500 on vol 2.
        await Reserve(jobId: 1, targetVol: 1, required: 500, srcVol: 2, freed: 500, sequenceOrder: 1);

        // Evaluating job 1 itself on its SOURCE volume must not credit its own liberation.
        var r = await Compute(volId: 2, free: 100, required: 400, excludeJobId: 1, sequenceOrder: 1);

        r.AvailableEstimateBytes.Should().Be(100);
        r.Feasible.Should().BeFalse();
    }

    // ── FIFO order: only jobs that PRECEDE in the queue contribute ────────────

    [Fact]
    public async Task Earlier_liberation_unblocks_a_later_job()
    {
        // Job 1 (seq 1) moves 300 OFF vol 1 → liberation -300 on vol 1.
        await Reserve(jobId: 1, targetVol: 2, required: 300, srcVol: 1, freed: 300, sequenceOrder: 1);

        // Job 2 (seq 2) needs 350 on vol 1 with only 100 free: job 1's liberation counts.
        var r = await Compute(volId: 1, free: 100, required: 350, excludeJobId: 2, sequenceOrder: 2);

        r.Feasible.Should().BeTrue("the preceding job frees 300 before job 2 runs");
        r.AvailableEstimateBytes.Should().Be(400); // 100 − (−300)
    }

    [Fact]
    public async Task Later_liberation_is_not_credited_to_an_earlier_job()
    {
        // Job 3 (seq 3) will free 300 on vol 1 — but it runs AFTER job 2.
        await Reserve(jobId: 3, targetVol: 2, required: 300, srcVol: 1, freed: 300, sequenceOrder: 3);

        // Job 2 (seq 2) needs 350 on vol 1 with 100 free: job 3's liberation must NOT count.
        var r = await Compute(volId: 1, free: 100, required: 350, excludeJobId: 2, sequenceOrder: 2);

        r.Feasible.Should().BeFalse("a job enqueued later cannot free space for an earlier one");
        r.AvailableEstimateBytes.Should().Be(100);
        r.DeficitBytes.Should().Be(250);
    }

    [Fact]
    public async Task Later_reservation_does_not_reduce_availability_for_an_earlier_job()
    {
        // Job 3 (seq 3) reserves 800 on vol 1 — irrelevant to job 2 (seq 2), which runs first.
        await Reserve(jobId: 3, targetVol: 1, required: 800, sequenceOrder: 3);

        var r = await Compute(volId: 1, free: 1000, required: 900, excludeJobId: 2, sequenceOrder: 2);

        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(1000);
    }

    [Fact]
    public async Task Prospective_job_with_no_order_sees_all_active_deltas()
    {
        // Preview/enqueue path (null/null): the new job lands at the end of the queue,
        // so every active entry precedes it.
        await Reserve(jobId: 1, targetVol: 1, required: 400, sequenceOrder: 1);
        await Reserve(jobId: 2, targetVol: 1, required: 300, sequenceOrder: 2);

        var r = await Compute(volId: 1, free: 1000, required: 400);

        r.Feasible.Should().BeFalse();
        r.AvailableEstimateBytes.Should().Be(300);
    }

    // ── EstimateIsLive ────────────────────────────────────────────────────────

    [Fact]
    public async Task Offline_volume_sets_EstimateIsLive_false()
    {
        var r = await Compute(volId: 1, free: 5000, required: 100, online: false);

        r.EstimateIsLive.Should().BeFalse();
        r.Feasible.Should().BeTrue(); // still uses FreeBytesLastKnown
    }

    [Fact]
    public async Task Online_volume_sets_EstimateIsLive_true()
    {
        var r = await Compute(volId: 1, free: 5000, required: 100, online: true);

        r.EstimateIsLive.Should().BeTrue();
    }

    // ── deficit + BlockingVolumeId ───────────────────────────────────────────

    [Fact]
    public async Task Deficit_equals_required_minus_available()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 900);
        // available = 1000 - 900 = 100, required = 150 → deficit = 50
        var r = await Compute(volId: 1, free: 1000, required: 150);

        r.DeficitBytes.Should().Be(50);
        r.Feasible.Should().BeFalse();
        r.BlockingVolumeId.Should().Be(1);
    }

    [Fact]
    public async Task BlockingVolumeId_is_null_when_feasible()
    {
        var r = await Compute(volId: 1, free: 1000, required: 500);
        r.BlockingVolumeId.Should().BeNull();
    }

    // ── available clamped at zero ────────────────────────────────────────────

    [Fact]
    public async Task Available_clamped_to_zero_when_overbooked()
    {
        // Reserve more than free (shouldn't happen in practice but must not crash)
        await Reserve(jobId: 1, targetVol: 1, required: 2000);

        var r = await Compute(volId: 1, free: 1000, required: 100);

        r.AvailableEstimateBytes.Should().Be(0);
        r.Feasible.Should().BeFalse();
    }

    // ── DB rebuild ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RebuildFromDb_restores_in_memory_state_from_active_entries()
    {
        // Persist via the original ledger (rebuild joins SequenceOrder from the job row)
        SeedJobRow(jobId: 1, sequenceOrder: 1);
        await Reserve(jobId: 1, targetVol: 1, required: 600);

        // Create a fresh ledger instance pointing to the same DB
        var newLedger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        await newLedger.RebuildFromDbAsync(CancellationToken.None);

        // Should see the 600-byte reservation from the DB
        var r = await newLedger.ComputeFeasibilityAsync(1, 1000, true, 500, 0, null, null, true, CancellationToken.None);
        r.AvailableEstimateBytes.Should().Be(400);
        r.Feasible.Should().BeFalse();
    }

    [Fact]
    public async Task RebuildFromDb_preserves_FIFO_order_information()
    {
        // Job 2 (seq 2) reserves 600 on vol 1. After a restart+rebuild, evaluating job 1
        // (seq 1, ahead of job 2) must NOT see job 2's later reservation.
        SeedJobRow(jobId: 2, sequenceOrder: 2);
        await Reserve(jobId: 2, targetVol: 1, required: 600, sequenceOrder: 2);

        var newLedger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        await newLedger.RebuildFromDbAsync(CancellationToken.None);

        var r = await newLedger.ComputeFeasibilityAsync(1, 1000, true, 900, marginBytes: 0,
            excludeJobId: 1, sequenceOrder: 1, includeQueuedLiberations: true, ct: CancellationToken.None);
        r.Feasible.Should().BeTrue("job 2's reservation comes later in the queue");
        r.AvailableEstimateBytes.Should().Be(1000);
    }

    [Fact]
    public async Task RebuildFromDb_ignores_inactive_entries()
    {
        SeedJobRow(jobId: 1, sequenceOrder: 1);
        await Reserve(jobId: 1, targetVol: 1, required: 600);
        await Release(jobId: 1); // marks entries inactive in DB

        var newLedger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        await newLedger.RebuildFromDbAsync(CancellationToken.None);

        // Inactive entries must not be loaded
        var r = await newLedger.ComputeFeasibilityAsync(1, 1000, true, 999, 0, null, null, true, CancellationToken.None);
        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(1000);
    }

    [Fact]
    public async Task RebuildFromDb_reconciles_phantom_reservations_of_terminal_jobs()
    {
        // Finding #5 crash footprint: the job committed a terminal state but died before the
        // ledger release — its entries are still IsActive. The rebuild must not resurrect
        // them (feasibility would under-count space forever) and must heal the DB rows.
        SeedJobRow(jobId: 1, sequenceOrder: 1, state: FileTracert.Contracts.Enums.JobState.Completed);
        await Reserve(jobId: 1, targetVol: 1, required: 900);

        SeedJobRow(jobId: 2, sequenceOrder: 2);
        await Reserve(jobId: 2, targetVol: 1, required: 100, sequenceOrder: 2);

        var newLedger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        await newLedger.RebuildFromDbAsync(CancellationToken.None);

        // Only the live job's 100 bytes count — the terminal job's 900 are a phantom.
        var r = await newLedger.ComputeFeasibilityAsync(1, 1000, true, 900, 0, null, null, true, CancellationToken.None);
        r.Feasible.Should().BeTrue("a completed job's reservation is not demand anymore");
        r.AvailableEstimateBytes.Should().Be(900);

        // And the orphan rows are healed, not just skipped, so every later consumer agrees.
        using var db = _harness.CreateContext();
        var job1Active = await db.SpaceLedgerEntries.CountAsync(e => e.JobId == 1 && e.IsActive);
        job1Active.Should().Be(0, "rebuild must deactivate entries of terminal jobs in the DB");
    }

    // ── independent volumes ───────────────────────────────────────────────────

    [Fact]
    public async Task Reservations_on_different_volumes_are_independent()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 500);
        await Reserve(jobId: 2, targetVol: 2, required: 700);

        var r1 = await Compute(volId: 1, free: 1000, required: 499);
        r1.Feasible.Should().BeTrue();
        r1.AvailableEstimateBytes.Should().Be(500);

        var r2 = await Compute(volId: 2, free: 1000, required: 301);
        r2.Feasible.Should().BeFalse();
        r2.AvailableEstimateBytes.Should().Be(300);
    }
    // ── §4 safety margin ──────────────────────────────────────────────────────

    [Fact]
    public async Task Margin_is_demanded_on_top_of_the_requirement()
    {
        // 1 000 free, 1 000 required: it fits exactly — until a 30-byte cushion is asked for.
        var without = await Compute(volId: 1, free: 1_000, required: 1_000);
        var with = await Compute(volId: 1, free: 1_000, required: 1_000, marginBytes: 30);

        without.Feasible.Should().BeTrue();
        with.Feasible.Should().BeFalse("the margin is part of the demand");
        with.DeficitBytes.Should().Be(30);
        with.RequiredBytes.Should().Be(1_000, "the requirement stays the honest size of the job");
        with.MarginBytes.Should().Be(30, "the cushion is reported apart so the UI can explain it");
    }

}
