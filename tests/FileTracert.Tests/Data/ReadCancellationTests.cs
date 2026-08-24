using FileTracert.Contracts.Search;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Search;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace FileTracert.Tests.Data;

/// <summary>
/// Step 14b — a cancelled read must STOP, not merely stop being awaited.
///
/// <para>The defect, as step 13 measured it on the installed service: a search went long, the
/// client gave up, and the service kept burning over a core for more than twenty minutes
/// (+147 s of CPU in 119 s of wall clock); a following <c>sc stop</c> sat in <c>StopPending</c>
/// for over 270 s against the 30 s <c>ShutdownTimeout</c> §3 promises.
/// <c>Microsoft.Data.Sqlite</c> implements <c>DbCommand.Cancel()</c> as a no-op and never looks at
/// the token once <c>sqlite3_step</c> is running, so a performance defect became a shutdown
/// defect.</para>
///
/// <para><b>The unit is rows stepped through, not milliseconds.</b> "It stopped consuming" is a
/// claim about work, and the machine's mood is not evidence. <c>Files</c> is shadowed by a view
/// carrying a UDF, exactly as in steps 11e and 14a — with one addition: the UDF is also what
/// cancels, so the cancellation provably lands INSIDE a running statement rather than whenever a
/// timer and the thread pool agree. Each test measures, on the same data, how many rows the
/// statement visits when nobody cancels it, and asserts the cancelled run visited a small fraction
/// of that.</para>
/// </summary>
public sealed class ReadCancellationTests : IClassFixture<BigCatalogFixture>
{
    /// <summary>Where the cancellation is fired, counted in rows the statement has stepped.</summary>
    private const int CancelAtRow = 1_000;

    private readonly BigCatalogFixture _big;
    private readonly ITestOutputHelper _out;

    public ReadCancellationTests(BigCatalogFixture big, ITestOutputHelper output)
    {
        _big = big;
        _out = output;
    }

    /// <summary>
    /// A query whose FTS term matches every seeded row and whose extension filter matches none, so
    /// the statement has to walk the whole match set before it can answer "nothing". The COUNT is
    /// the long statement here, deliberately: were the long one the second, a token cancelled early
    /// would be caught by ADO.NET's own pre-execution check between the two and the guard would
    /// never be exercised at all.
    /// </summary>
    private static FileSearchQuery LongQuery() =>
        new("match", SearchScope.Name, null, ["zzz"], null, null, null, null,
            null, false, SearchSort.Relevance, false, 0, 50);

    private static FileSearchQuery ShortQuery() =>
        new("match", SearchScope.Name, null, null, null, null, null, null,
            null, false, SearchSort.Name, false, 0, 20);

    private FileSearchIndex Index(DatabaseShutdownSignal? shutdown = null)
        => new(_big.Context, shutdown);

    /// <summary>The interrupt this guard asks for, as it looks from the outside.</summary>
    private static void ShouldBeAnInterrupt(Exception thrown)
    {
        thrown.InnerException.Should().BeOfType<SqliteException>(
            "a read that stopped early stopped because it was interrupted, not because someone " +
            "noticed afterwards that the token had been cancelled all along")
            .Which.SqliteErrorCode.Should().Be(SQLitePCL.raw.SQLITE_INTERRUPT);
    }

    /// <summary>Rows the uncancelled query steps through — the number a cancelled run must beat.</summary>
    private async Task<int> UninterruptedVisitsAsync()
    {
        await using var view = await FilesShadowView.InstallAsync(_big.Context);
        var result = await Index().SearchAsync(LongQuery(), CancellationToken.None);
        result.TotalCount.Should().Be(0);
        _out.WriteLine($"uninterrupted: {view.Visits} rows stepped");
        view.Visits.Should().BeGreaterThan(CancelAtRow * 10,
            "the query has to be long enough that stopping early is visible");
        return view.Visits;
    }

    // ── 1. the defect ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cancelled_search_stops_stepping_instead_of_running_to_completion()
    {
        var full = await UninterruptedVisitsAsync();

        using var cts = new CancellationTokenSource();
        await using var view = await FilesShadowView.InstallAsync(
            _big.Context, row => { if (row == CancelAtRow) cts.Cancel(); });

        var act = async () => await Index().SearchAsync(LongQuery(), cts.Token);
        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;

        _out.WriteLine($"cancelled at row {CancelAtRow}: {view.Visits} rows stepped (of {full})");

        ShouldBeAnInterrupt(thrown);
        view.Visits.Should().BeLessThan(full / 10,
            "a cancelled read must stop stepping, not run to completion for nobody");
    }

    /// <summary>
    /// The other source of cancellation, and the one that turns a slow query into a service that
    /// cannot be stopped: nobody cancelled the request, the host started stopping.
    /// </summary>
    [Fact]
    public async Task The_shutdown_signal_interrupts_a_read_nobody_cancelled()
    {
        var full = await UninterruptedVisitsAsync();

        using var stopping = new CancellationTokenSource();
        // Cancellable, but never cancelled: what fires is the host's signal, not the caller's.
        using var caller = new CancellationTokenSource();

        await using var view = await FilesShadowView.InstallAsync(
            _big.Context, row => { if (row == CancelAtRow) stopping.Cancel(); });

        var act = async () =>
            await Index(new DatabaseShutdownSignal(stopping.Token)).SearchAsync(LongQuery(), caller.Token);
        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;

        _out.WriteLine($"shutdown at row {CancelAtRow}: {view.Visits} rows stepped (of {full})");

        ShouldBeAnInterrupt(thrown);
        view.Visits.Should().BeLessThan(full / 10);
        caller.IsCancellationRequested.Should().BeFalse();
    }

    /// <summary>
    /// A deliberate <see cref="CancellationToken.None"/> means "this must run to completion", and
    /// the queue engine passes exactly that on its checkpoints. The shutdown signal does not
    /// override it: no guard is installed at all, so nothing can interrupt the statement.
    /// </summary>
    [Fact]
    public async Task A_read_that_cannot_be_cancelled_is_not_interrupted_by_shutdown()
    {
        var full = await UninterruptedVisitsAsync();

        using var stopping = new CancellationTokenSource();
        await using var view = await FilesShadowView.InstallAsync(
            _big.Context, row => { if (row == CancelAtRow) stopping.Cancel(); });

        var result = await Index(new DatabaseShutdownSignal(stopping.Token))
            .SearchAsync(LongQuery(), CancellationToken.None);

        stopping.IsCancellationRequested.Should().BeTrue("otherwise the test proved nothing");
        result.TotalCount.Should().Be(0);
        view.Visits.Should().BeGreaterThan(full / 2,
            "a read the caller declared uncancellable has to run to the end");
    }

    // ── 2. the EF read path ───────────────────────────────────────────────────

    /// <summary>
    /// The same guarantee for the queries that go through EF — the Catalogue and every list. Here
    /// the interceptor is what puts the statement under the guard.
    /// </summary>
    [Fact]
    public async Task A_cancelled_EF_read_stops_stepping()
    {
        var query = () => _big.Context.Files
            .OrderBy(f => f.Name)
            .ThenBy(f => f.SizeBytes)
            .Skip(BigCatalogFixture.Rows - 50)
            .Take(50);

        int full;
        await using (var baseline = await FilesShadowView.InstallAsync(_big.Context))
        {
            (await query().ToListAsync()).Should().HaveCount(50);
            full = baseline.Visits;
            _out.WriteLine($"uninterrupted EF read: {full} rows stepped");
            full.Should().BeGreaterThan(CancelAtRow * 10);
        }

        using var cts = new CancellationTokenSource();
        await using var view = await FilesShadowView.InstallAsync(
            _big.Context, row => { if (row == CancelAtRow) cts.Cancel(); });

        var act = async () => await query().ToListAsync(cts.Token);
        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;

        _out.WriteLine($"cancelled EF read: {view.Visits} rows stepped (of {full})");

        ShouldBeAnInterrupt(thrown);
        view.Visits.Should().BeLessThan(full / 10);
    }

    // ── 3. no spurious interruption ───────────────────────────────────────────

    /// <summary>
    /// The guard has to be invisible to a query that finishes. Same token shape as a real request
    /// (cancellable), never cancelled while it runs — repeated, because an interrupt leaking onto
    /// the next statement of the same connection would show up as one wrong answer out of many.
    /// </summary>
    [Fact]
    public async Task A_query_that_is_never_cancelled_is_never_interrupted()
    {
        var expected = await Index().SearchAsync(ShortQuery(), CancellationToken.None);
        expected.Items.Should().NotBeEmpty();

        for (var i = 0; i < 50; i++)
        {
            using var cts = new CancellationTokenSource();
            var actual = await Index().SearchAsync(ShortQuery(), cts.Token);

            actual.TotalCount.Should().Be(expected.TotalCount);
            actual.Items.Should().Equal(expected.Items);

            // Cancelling AFTER the read returned must not reach the connection the next read will
            // use: the registration is gone by then, which is the whole safety argument.
            cts.Cancel();
        }
    }
}
