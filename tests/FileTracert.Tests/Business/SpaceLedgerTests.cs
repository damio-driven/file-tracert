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

    private Task<FeasibilityResult> Compute(int volId, long free, long required, bool online = true) =>
        _ledger.ComputeFeasibilityAsync(volId, free, online, required, CancellationToken.None);

    private Task Reserve(int jobId, int targetVol, long required, int? srcVol = null, long freed = 0) =>
        _ledger.ReserveAsync(jobId, targetVol, required, srcVol, freed, CancellationToken.None);

    private Task Release(int jobId) =>
        _ledger.ReleaseAsync(jobId, CancellationToken.None);

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
        // Persist via the original ledger
        await Reserve(jobId: 1, targetVol: 1, required: 600);

        // Create a fresh ledger instance pointing to the same DB
        var newLedger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        await newLedger.RebuildFromDbAsync(CancellationToken.None);

        // Should see the 600-byte reservation from the DB
        var r = await newLedger.ComputeFeasibilityAsync(1, 1000, true, 500, CancellationToken.None);
        r.AvailableEstimateBytes.Should().Be(400);
        r.Feasible.Should().BeFalse();
    }

    [Fact]
    public async Task RebuildFromDb_ignores_inactive_entries()
    {
        await Reserve(jobId: 1, targetVol: 1, required: 600);
        await Release(jobId: 1); // marks entries inactive in DB

        var newLedger = new SpaceLedger(CreateScopeFactory(_harness), NullLogger<SpaceLedger>.Instance);
        await newLedger.RebuildFromDbAsync(CancellationToken.None);

        // Inactive entries must not be loaded
        var r = await newLedger.ComputeFeasibilityAsync(1, 1000, true, 999, CancellationToken.None);
        r.Feasible.Should().BeTrue();
        r.AvailableEstimateBytes.Should().Be(1000);
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
}
