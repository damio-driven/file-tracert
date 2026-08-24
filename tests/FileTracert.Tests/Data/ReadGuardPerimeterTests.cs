using FileTracert.Contracts.Enums;
using FileTracert.Data;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Entities;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace FileTracert.Tests.Data;

/// <summary>
/// Step 14b — what the read guard deliberately does NOT touch.
///
/// <para>Interrupting a SELECT throws away nothing. Interrupting a write inside an explicit
/// transaction makes SQLite roll the whole transaction back, and that is the queue's crash-safety
/// discipline — the one thing the task forbids this mechanism from reaching. Both exclusions are
/// asserted here rather than merely written down, because a later hand widening the perimeter would
/// otherwise find nothing in its way.</para>
///
/// <para>Both tests cancel from INSIDE the running statement, through a UDF, for the reason given
/// in <see cref="FilesShadowView"/>: a cancellation that lands between two statements would let a
/// broken perimeter pass.</para>
/// </summary>
public sealed class ReadGuardPerimeterTests : IAsyncLifetime
{
    private const int Rows = 200_000;
    private const int CancelAtRow = 1_000;

    private readonly ITestOutputHelper _out;
    private SqliteInMemoryContext _harness = null!;
    private FileTracertDbContext _ctx = null!;
    private int _volumeId;
    private int _rootId;

    public ReadGuardPerimeterTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _harness = new SqliteInMemoryContext();
        _ctx = _harness.CreateContext();

        var volume = new Volume
        {
            VolumeGuid = $@"\\?\Volume{{{Guid.NewGuid()}}}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        _ctx.Volumes.Add(volume);
        await _ctx.SaveChangesAsync();
        _volumeId = volume.Id;

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        _ctx.Directories.Add(root);
        await _ctx.SaveChangesAsync();
        _rootId = root.Id;
    }

    public Task DisposeAsync()
    {
        _harness.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// One INSERT of <see cref="Rows"/> real rows, with a UDF in its SELECT so the caller can act
    /// at a chosen row while the statement is running.
    /// </summary>
    private async Task InsertManyAsync(CancellationToken ct, Action<int>? onRow = null)
    {
        var conn = (SqliteConnection)_ctx.Database.GetDbConnection();
        await _ctx.Database.OpenConnectionAsync();
        try
        {
            var seen = 0;
            conn.CreateFunction("tick", (long _) => { onRow?.Invoke(++seen); return 1L; });

            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
            // Every interpolated value is an int or a value produced right here — nothing user-supplied.
#pragma warning disable EF1002
            await _ctx.Database.ExecuteSqlRawAsync($"""
                WITH RECURSIVE seq(n) AS (
                    SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {Rows})
                INSERT INTO Files
                  (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
                   CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
                   ExcludedByScan, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
                SELECT {_volumeId}, {_rootId}, 'match' || n || '.bin', 'bin', 'Other', 1024 * n,
                       '{now}', '{now}', 0, 1, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
                FROM seq WHERE tick(n) = 1
                """, ct);
#pragma warning restore EF1002
        }
        finally
        {
            await _ctx.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// A write is never interrupted, whatever the token does. Asserted on the EFFECT: every row the
    /// statement promised is there.
    /// </summary>
    [Fact]
    public async Task A_cancelled_write_runs_to_completion()
    {
        using var cts = new CancellationTokenSource();

        try
        {
            await InsertManyAsync(cts.Token, row => { if (row == CancelAtRow) cts.Cancel(); });
        }
        catch (OperationCanceledException)
        {
            // ADO.NET may report the cancellation once the statement has already returned. What is
            // under test is the statement, and the row count below is what says whether it ran.
        }

        cts.IsCancellationRequested.Should().BeTrue("otherwise the test proved nothing");
        (await _ctx.Files.CountAsync()).Should().Be(Rows,
            "interrupting a write inside a transaction rolls it back — the queue's crash-safety " +
            "discipline is built on those transactions and this mechanism must not reach them");
    }

    /// <summary>
    /// A read that carries a <c>DbTransaction</c> belongs to a write unit of work (the state
    /// machine reads a job before it moves it). It is left alone — the second, independent way the
    /// queue's connections stay outside this mechanism.
    /// </summary>
    [Fact]
    public async Task A_read_inside_a_transaction_is_not_interrupted()
    {
        await InsertManyAsync(CancellationToken.None);

        var query = () => _ctx.Files
            .OrderBy(f => f.Name)
            .ThenBy(f => f.SizeBytes)
            .Skip(Rows - 50)
            .Take(50);

        int full;
        await using (var baseline = await FilesShadowView.InstallAsync(_ctx))
        {
            (await query().ToListAsync()).Should().HaveCount(50);
            full = baseline.Visits;
            _out.WriteLine($"uninterrupted: {full} rows stepped");
            full.Should().BeGreaterThan(CancelAtRow * 10);
        }

        using var cts = new CancellationTokenSource();
        await using var view = await FilesShadowView.InstallAsync(
            _ctx, row => { if (row == CancelAtRow) cts.Cancel(); });
        await using var tx = await _ctx.Database.BeginTransactionAsync();

        var act = async () => await query().ToListAsync(cts.Token);
        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;

        _out.WriteLine($"transactional read: {view.Visits} rows stepped (of {full})");

        (thrown.InnerException as SqliteException).Should().BeNull(
            "a read inside an explicit transaction must not be interrupted — EF's own check " +
            "between rows throws a bare cancellation, while the guard's translation carries the " +
            "SQLite interrupt it converted");
        view.Visits.Should().BeGreaterThan(full / 2,
            "and the statement must actually have run to the end — an assertion on the exception " +
            "alone would also pass if it had never started");
    }

    /// <summary>
    /// The overhead of the guard, in the unit that means something: allocations per command. Two
    /// token registrations and one small object — and nothing at all when the caller's token cannot
    /// be cancelled, which is the whole deliberate-<see cref="CancellationToken.None"/> path.
    /// </summary>
    [Fact]
    public async Task The_guard_costs_two_registrations_and_nothing_when_it_cannot_fire()
    {
        const int Iterations = 2_000;

        var conn = (SqliteConnection)_ctx.Database.GetDbConnection();
        await _ctx.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";

            using var live = new CancellationTokenSource();

            // Warm up both shapes so JIT and the statement cache are out of the measurement.
            for (var i = 0; i < 100; i++)
            {
                await SqliteReadGuard.ExecuteAsync(
                    cmd, CancellationToken.None, default, null, c => cmd.ExecuteScalarAsync(c));
                await SqliteReadGuard.ExecuteAsync(
                    cmd, live.Token, default, null, c => cmd.ExecuteScalarAsync(c));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Iterations; i++)
            {
                await SqliteReadGuard.ExecuteAsync(
                    cmd, CancellationToken.None, default, null, c => cmd.ExecuteScalarAsync(c));
            }
            var unguarded = GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < Iterations; i++)
            {
                await SqliteReadGuard.ExecuteAsync(
                    cmd, live.Token, default, null, c => cmd.ExecuteScalarAsync(c));
            }
            var guarded = GC.GetAllocatedBytesForCurrentThread() - before;

            var perCommand = (guarded - unguarded) / (double)Iterations;
            _out.WriteLine(
                $"unguarded {unguarded / Iterations} B/cmd, guarded {guarded / Iterations} B/cmd, " +
                $"guard costs {perCommand:F0} B/cmd");

            perCommand.Should().BeLessThan(512,
                "the guard is two CancellationTokenRegistrations and one small object per command");
            perCommand.Should().BeGreaterThan(0, "otherwise the guarded path was not taken at all");
        }
        finally
        {
            await _ctx.Database.CloseConnectionAsync();
        }
    }
}
